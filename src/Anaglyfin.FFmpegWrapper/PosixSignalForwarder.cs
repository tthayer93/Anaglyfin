using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// Listens for the three stop signals on POSIX and hands each one to the attached FFmpeg
/// child, under exactly the number the wrapper itself received it under.
/// </summary>
/// <remarks>
/// <para>
/// <b>Receiving is the safe .NET API's job.</b> The signal arrives through
/// <see cref="PosixSignalRegistration"/>, the runtime's own registration: it installs the
/// native handler, moves delivery off the signal context onto a thread that may do real
/// work, and hands over a cancel flag that decides whether the runtime performs the
/// signal's default disposition afterwards. The wrapper uses that flag for exactly one
/// decision, and it is the decision this type's handler returns: a signal that reached a
/// running child is <em>canceled</em>, so the wrapper outlives it and stays to do what a
/// wrapper is for - wait for the encoder and relay its exit code - while a signal that
/// found no child runs off the default disposition, stopping the wrapper exactly as it
/// stopped FFmpeg before any wrapper existed. A handler that always canceled would leave a
/// wrapper that no stop signal can reach; a handler that never canceled would kill the
/// wrapper in the middle of relaying.
/// </para>
/// <para>
/// <b>Sending is the C library's <c>kill(2)</c>.</b> There is no managed way in this
/// runtime to deliver an arbitrary signal: <see cref="System.Diagnostics.Process.Kill()"/>
/// knows only <c>SIGKILL</c>, and a wrapper that answered a <c>SIGTERM</c> with a
/// <c>SIGKILL</c> would deny FFmpeg the final segment and the finished playlist that the
/// graceful stop is the encoder's last chance to write. So the send is the platform call
/// the shell's own <c>kill</c> command makes, bound once through
/// <see cref="NativeLibrary"/> and kept behind <see cref="SendSignal"/> and injectable for
/// the tests - directly, through a delegate, because the alternative syntax is compiled
/// through unsafe code and this assembly does not enable it. The return code is retained
/// and deliberately unread: a child that has already exited is not an error worth acting
/// on, and the wait for it ends on its own.
/// </para>
/// <para>
/// <b>The three signals, and only the three.</b> <c>SIGTERM</c>, <c>SIGINT</c> and
/// <c>SIGHUP</c> are the stop paths a server, an administrator or a dying session uses;
/// <c>SIGQUIT</c>, <c>SIGUSR1</c> and everything else are left at their default behaviour,
/// because forwarding a signal the encoder does not stop for would be inventing behaviour
/// the server did not ask for. Their numbers (1, 2, 15) are the same on every POSIX
/// platform, and <see cref="SignalNumber"/> writes them out rather than casting the
/// enum, so the one mapping the wrapper depends on is visible where it is made.
/// </para>
/// <para>
/// <b>Windows has none of this.</b> POSIX signals do not exist there, and
/// <see cref="PosixSignalRegistration"/> says so by refusing to register. The factory
/// <see cref="Create"/> asks the platform first and hands out a forwarder that does
/// nothing when the signals cannot arrive at all - so the skip is the platform's answer,
/// not an exception handled later.
/// </para>
/// </remarks>
public sealed class PosixSignalForwarder : IChildSignalForwarder
{
    /// <summary>
    /// The stop signals a wrapper invocation listens for, in the order the forwarder
    /// registers them.
    /// </summary>
    private static readonly PosixSignal[] StopSignals =
    {
        PosixSignal.SIGTERM,
        PosixSignal.SIGINT,
        PosixSignal.SIGHUP
    };

    /// <summary>
    /// The names a C library answers to on the POSIX families this wrapper runs on.
    /// </summary>
    /// <remarks>
    /// The library that owns <c>kill(2)</c> is one thing with different names: glibc
    /// answers to <c>libc.so.6</c>, musl to <c>libc.so</c>, and Apple platforms folded it
    /// into <c>libSystem.B.dylib</c>; the bare <c>libc</c> is the name the
    /// platform-invoke convention uses for it. Trying them in order rather than picking
    /// one is what lets a single wrapper build talk signals on every POSIX deployment
    /// without asking beforehand which C library it got. A handle that loads is never
    /// released - the bound function keeps pointing into it.
    /// </remarks>
    private static readonly string[] LibCNames = { "libc", "libc.so.6", "libc.so", "libSystem.B.dylib" };

    /// <summary>
    /// The process's one binding of <c>kill(2)</c>, resolved on first use.
    /// </summary>
    /// <remarks>
    /// A <see cref="Lazy{T}"/> with publication-safe initialization because two threads
    /// can meet this function for the first time in production: the launcher's thread
    /// and - if a stop signal arrives while the launcher is still starting the binary -
    /// the signal delivery thread. A failed resolution is cached like a successful one:
    /// a C library that was not there on the first question is not found by asking again.
    /// </remarks>
    private static readonly Lazy<KillFunction?> Kill = new(ResolveKill, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly IDisposable?[] _registrations = new IDisposable?[StopSignals.Length];
    private readonly ChildSignalSender _sender;

    /// <summary>
    /// The attached child's process id, or 0 for "no child". Written by the launcher's
    /// thread and read on the signal-delivery thread, so every access goes through
    /// <see cref="Interlocked"/> or <see cref="Volatile"/> - see
    /// <see cref="AttachChild"/>, <see cref="DetachChild"/> and <see cref="OnStopSignal"/>.
    /// </summary>
    private int _childProcessId;

    /// <summary>
    /// Handles one stop signal and reports whether the wrapper should stay alive because
    /// of it.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the signal was handed to a running child, which cancels the
    /// signal's default disposition: the wrapper waits for the encoder and reports its
    /// exit code. <c>false</c> when no child was running, and the signal stops the wrapper
    /// as it stopped FFmpeg before the wrapper existed.
    /// </returns>
    public delegate bool StopSignalHandler();

    /// <summary>
    /// Registers <paramref name="handler"/> for one signal until the returned token is
    /// disposed - the seam over
    /// <see cref="PosixSignalRegistration.Create(PosixSignal, Action{PosixSignalContext})"/>,
    /// which is what production registers with.
    /// </summary>
    /// <param name="signal">The signal to listen for.</param>
    /// <param name="handler">What a delivery of it runs.</param>
    /// <returns>The registration token.</returns>
    public delegate IDisposable StopSignalRegistrar(PosixSignal signal, StopSignalHandler handler);

    /// <summary>
    /// Hands one raw signal to one process id and returns the operating system's result -
    /// the seam over <c>kill(2)</c>, which is what production sends with.
    /// </summary>
    /// <param name="processId">The process to signal.</param>
    /// <param name="signalNumber">The raw signal number to deliver.</param>
    /// <returns>
    /// Zero when the operating system accepted the request; anything else is its refusal,
    /// which the wrapper keeps rather than throws over - a child mid-exit is not a failure.
    /// </returns>
    public delegate int ChildSignalSender(int processId, int signalNumber);

    /// <summary>
    /// The native <c>kill(2)</c> as a delegate: deliver a signal, get the return code.
    /// </summary>
    /// <remarks>
    /// A delegate rather than a direct native declaration because declarations of native
    /// imports compile through unsafe code, which this assembly does not enable for
    /// anything - and <c>kill(int, int)</c> is entirely blittable, so the delegate route
    /// gives up nothing but the keyword.
    /// </remarks>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int KillFunction(int pid, int sig);

    /// <summary>
    /// Creates a forwarder that is already listening, over an explicit registration and an
    /// explicit send.
    /// </summary>
    /// <param name="registrar">How this forwarder subscribes to the stop signals.</param>
    /// <param name="sender">How it delivers one to the child.</param>
    /// <remarks>
    /// The constructor registers rather than a method being called later: a stop signal
    /// that arrives between "the launcher decided to start FFmpeg" and "the child exists"
    /// must already find a listener, which is also why the launcher creates the forwarder
    /// before it starts the process. Production goes through <see cref="Create"/>, which
    /// supplies the live registration and the live send; the two parameters exist so the
    /// tests can watch the whole policy - which signals, which numbers, what a childless
    /// signal answers - without touching the machine's signal handlers.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public PosixSignalForwarder(StopSignalRegistrar registrar, ChildSignalSender sender)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        ArgumentNullException.ThrowIfNull(sender);

        _sender = sender;

        for (var i = 0; i < StopSignals.Length; i++)
        {
            var signal = StopSignals[i];
            _registrations[i] = registrar(signal, () => OnStopSignal(signal));
        }
    }

    /// <summary>
    /// Gets whether this platform has POSIX signals for a wrapper to forward at all.
    /// </summary>
    /// <remarks>
    /// The mirror of the platforms <see cref="PosixSignalRegistration"/> marks unsupported;
    /// where the type says it cannot listen, neither can the wrapper.
    /// </remarks>
    public static bool IsSupported =>
        !OperatingSystem.IsWindows()
        && !OperatingSystem.IsAndroid()
        && !OperatingSystem.IsIOS()
        && !OperatingSystem.IsBrowser();

    /// <summary>
    /// Creates the forwarder for the platform this process actually runs on: a listening
    /// one on POSIX, a silent one everywhere else.
    /// </summary>
    /// <returns>
    /// A forwarder the launcher may attach a child to and dispose; never <c>null</c>, and
    /// never one that throws.
    /// </returns>
    /// <remarks>
    /// <para>
    /// On a platform without the signals - Windows, and the single-platform runtimes that
    /// <see cref="PosixSignalRegistration"/> excludes - the caller is handed
    /// <see cref="NoOpChildSignalForwarder"/> and nothing is registered, because
    /// registering there is a <see cref="PlatformNotSupportedException"/>, not a no-op.
    /// </para>
    /// <para>
    /// The fallback matters beyond the skip: even where the platform claims the signals, a
    /// registration that fails (the runtime cannot install its handler) is met with the
    /// silent forwarder rather than an exception, because the alternative is a wrapper that
    /// refuses every playback because of a signal nobody has sent. That is exactly the
    /// behaviour the wrapper had before forwarding existed - degraded, attributable, and
    /// still playing.
    /// </para>
    /// </remarks>
    public static IChildSignalForwarder Create()
    {
        if (!IsSupported)
        {
            return new NoOpChildSignalForwarder();
        }

        try
        {
            return new PosixSignalForwarder(RegisterStopSignal, SendSignal);
        }
#pragma warning disable CA1031 // Deliberate: see the remarks - a signal layer that cannot start must not stop a playback.
        catch (Exception exception) when (exception is PlatformNotSupportedException or IOException)
        {
            return new NoOpChildSignalForwarder();
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Creates the forwarder for a platform decided by the caller, over the registration
    /// and the send the caller supplies.
    /// </summary>
    /// <param name="posixSignalsReachThisProcess">
    /// Stands in for what <see cref="IsSupported"/> answers about the real machine.
    /// </param>
    /// <param name="registrar">Receives the registrations, when there are any.</param>
    /// <param name="sender">Receives the signal deliveries, when there are any.</param>
    /// <returns>The listening forwarder or the silent one.</returns>
    /// <remarks>
    /// The one line of <see cref="Create"/> that decides whether a Windows deployment sees
    /// any signal machinery at all is otherwise only testable by running on Windows, which
    /// the CI image does not. This overload is that decision with the platform question
    /// made a parameter; production calls <see cref="Create"/> and never this.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Either delegate argument is null.</exception>
    public static IChildSignalForwarder Create(
        bool posixSignalsReachThisProcess,
        StopSignalRegistrar registrar,
        ChildSignalSender sender)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        ArgumentNullException.ThrowIfNull(sender);

        return posixSignalsReachThisProcess
            ? new PosixSignalForwarder(registrar, sender)
            : new NoOpChildSignalForwarder();
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="childProcessId"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">A child is already attached.</exception>
    public void AttachChild(int childProcessId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(childProcessId, 0);

        // The compare is the check: attaching while a child is attached is refused rather
        // than quietly retargeted, because the launcher that did it has lost track of the
        // encoder it is supposed to be guarding.
        var previous = Interlocked.CompareExchange(ref _childProcessId, childProcessId, comparand: 0);
        if (previous != 0)
        {
            throw new InvalidOperationException(
                "This forwarder already speaks for a running child; one wrapper starts one FFmpeg.");
        }
    }

    /// <inheritdoc />
    public void DetachChild()
    {
        // Not just a store: detaching is the moment the id stops naming our encoder and
        // starts naming whoever the operating system lends it to next, so after this the
        // handler must never see the number again.
        Interlocked.Exchange(ref _childProcessId, 0);
    }

    /// <summary>
    /// Stops listening and forgets the child. Safe to call twice, and safe from any thread
    /// the launcher's <c>finally</c> happens to be on.
    /// </summary>
    public void Dispose()
    {
        DetachChild();

        for (var i = 0; i < _registrations.Length; i++)
        {
            var registration = _registrations[i];
            if (registration is not null)
            {
                _registrations[i] = null;
                registration.Dispose();
            }
        }
    }

    /// <summary>
    /// The number POSIX gives each forwarded signal - on every platform that has them,
    /// which is the point of writing the table rather than casting the enum.
    /// </summary>
    /// <param name="signal">One of the three stop signals.</param>
    /// <returns><c>1</c>, <c>2</c> or <c>15</c>.</returns>
    /// <remarks>
    /// A cast of the enum would be wrong twice over: the named <see cref="PosixSignal"/>
    /// members carry negative sentinel values precisely because they are platform-neutral
    /// names rather than numbers, and a positive member - a raw signal number, which the
    /// type also admits - would reach <c>kill(2)</c> as itself, unasked. This table is
    /// the only place the wrapper turns a name into a number, and it answers for the three
    /// names it owns.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The signal is not one of the three; the wrapper has no business delivering anything
    /// it has not decided to understand.
    /// </exception>
    public static int SignalNumber(PosixSignal signal) => signal switch
    {
        PosixSignal.SIGHUP => 1,
        PosixSignal.SIGINT => 2,
        PosixSignal.SIGTERM => 15,
        _ => throw new ArgumentException(
            $"Signal '{signal}' is not a stop signal the wrapper forwards.",
            nameof(signal))
    };

    /// <summary>
    /// Delivers one signal to one process through the operating system.
    /// </summary>
    /// <param name="processId">The process to signal.</param>
    /// <param name="signalNumber">The raw POSIX signal number.</param>
    /// <returns>
    /// The operating system's answer: zero once the signal is queued, minus one with an
    /// error (a process already gone answers <c>ESRCH</c>) - kept, never thrown.
    /// </returns>
    /// <remarks>
    /// Public because it is the wrapper's whole sending capability stated once: the tests
    /// of the mapping and of the binding itself go through this method rather than
    /// repeating it, and production reaches it only through the injected
    /// <see cref="ChildSignalSender"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Either argument is not positive.</exception>
    public static int SendSignal(int processId, int signalNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(processId, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(signalNumber, 0);

        var killFunction = Kill.Value;
        if (killFunction is null)
        {
            // The C library could not be bound on this machine. Answering like the
            // operating system answering "no" is the only honest default: nothing is
            // delivered, the wrapper still waits for its child, and the forwarder is
            // exactly as effective as it was before this type existed.
            return -1;
        }

        return killFunction(processId, signalNumber);
    }

    /// <summary>
    /// What a delivery of one of the stop signals does: forward it, and answer whether the
    /// wrapper should outlive it.
    /// </summary>
    /// <param name="signal">The signal that arrived.</param>
    /// <returns>The cancel answer - see <see cref="StopSignalHandler"/>.</returns>
    private bool OnStopSignal(PosixSignal signal)
    {
        var childProcessId = Volatile.Read(ref _childProcessId);
        if (childProcessId == 0)
        {
            // Nothing to speak for: not our signal to absorb. The wrapper stops right here
            // as FFmpeg used to stop at this point of a launch, slot file and all - the
            // operating system takes the slot's handle back with the process.
            return false;
        }

        try
        {
            _sender(childProcessId, SignalNumber(signal));
        }
#pragma warning disable CA1031 // Deliberate: an exception must not leave this signal handler.
        catch (Exception)
        {
            // A signal handler that throws takes the wrapper down with the child still
            // running - the exact orphan this type exists to prevent - so nothing may
            // escape. A failed send still means a child may be running and still worth
            // waiting for, which is what returning true buys.
        }
#pragma warning restore CA1031

        return true;
    }

    /// <summary>
    /// Finds <c>kill(2)</c> in the first C library that loads and exports it.
    /// </summary>
    /// <returns>
    /// The bound function, or <c>null</c> when no candidate named a library that has it.
    /// </returns>
    private static KillFunction? ResolveKill()
    {
        foreach (var name in LibCNames)
        {
            if (NativeLibrary.TryLoad(name, out var handle)
                && NativeLibrary.TryGetExport(handle, "kill", out var entryPoint))
            {
                return Marshal.GetDelegateForFunctionPointer<KillFunction>(entryPoint);
            }
        }

        return null;
    }

    /// <summary>
    /// The live registration: the runtime's API, with the handler's answer wired straight
    /// to its cancel flag.
    /// </summary>
    /// <param name="signal">The signal to listen for.</param>
    /// <param name="handler">What a delivery runs.</param>
    /// <returns>The registration token.</returns>
    /// <remarks>
    /// <para>
    /// One line and worth reading slowly: <c>context.Cancel = handler()</c> runs the
    /// forward first and lets the result decide the disposition afterwards, which is the
    /// whole policy of this type - forwarded means waited-out, unforwarded means default.
    /// For the three signals registered here the runtime performs the default disposition
    /// by re-raising once the handler returns uncanceled, so the wrapper's exit then is a
    /// signal death, exactly as an un-wrapped FFmpeg's would have been at the same moment.
    /// </para>
    /// <para>
    /// The platform check is not the factory's check repeated for comfort: this method is
    /// the only name of <see cref="PosixSignalRegistration"/> in the wrapper, and it
    /// refuses for itself on the platforms that API excludes, so the platform claim is
    /// stated where the platform is used and a future caller cannot inherit the guard by
    /// accident. <see cref="Create"/> treats this exception like any other registration
    /// failure - the silent forwarder, playback on.
    /// </para>
    /// </remarks>
    /// <exception cref="PlatformNotSupportedException">
    /// This is a platform without the POSIX signals this API registers for.
    /// </exception>
    private static IDisposable RegisterStopSignal(PosixSignal signal, StopSignalHandler handler)
    {
        if (OperatingSystem.IsWindows()
            || OperatingSystem.IsAndroid()
            || OperatingSystem.IsIOS()
            || OperatingSystem.IsBrowser())
        {
            throw new PlatformNotSupportedException(
                "The FFmpeg wrapper's stop-signal forwarding needs POSIX signals, which this platform does not have.");
        }

        return PosixSignalRegistration.Create(signal, context => context.Cancel = handler());
    }
}

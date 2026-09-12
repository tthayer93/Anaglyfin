using System;
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
/// <b>Sending is <c>kill(2)</c>'s job.</b> There is no managed way in this runtime to
/// deliver an arbitrary signal: <see cref="System.Diagnostics.Process.Kill()"/> knows only
/// <c>SIGKILL</c>, and a wrapper that answered a <c>SIGTERM</c> with a <c>SIGKILL</c> would
/// deny FFmpeg the final segment and the finished playlist that the graceful stop is the
/// encoder's last chance to write. So the send is one P/Invoke of <c>kill(pid, sig)</c> -
/// the call the shell's own <c>kill</c> command makes - kept behind
/// <see cref="SendSignal"/> and injectable for the tests. Its return code is deliberately
/// unread beyond being retained: a child that has already exited is not an error worth
/// acting on, and the wait for it ends on its own.
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
public sealed partial class PosixSignalForwarder : IChildSignalForwarder
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
    /// disposed - the seam over <see cref="PosixSignalRegistration.Create(PosixSignal, Action{PosixSignalContext})"/>,
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
    /// of the mapping and of the P/Invoke itself go through this method rather than
    /// repeating it, and production reaches it only through the injected
    /// <see cref="ChildSignalSender"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Either argument is not positive.</exception>
    public static int SendSignal(int processId, int signalNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(processId, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(signalNumber, 0);

        return kill(processId, signalNumber);
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

    /// <summary>
    /// The operating system's <c>kill(2)</c>: deliver a signal to a process.
    /// </summary>
    /// <param name="pid">The process to signal.</param>
    /// <param name="sig">The raw signal number.</param>
    /// <returns>Zero on success, minus one with <c>errno</c> set otherwise.</returns>
    /// <remarks>
    /// The declaration is the entire dependency: no shell is started to send a signal to a
    /// process this process already knows the id of, the same reasoning that keeps the
    /// launcher off <c>cmd</c> and <c>/bin/sh</c>. The name is spelled lower-case because
    /// it is the C symbol, and the entry point follows the name.
    /// </remarks>
    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int sig);
}

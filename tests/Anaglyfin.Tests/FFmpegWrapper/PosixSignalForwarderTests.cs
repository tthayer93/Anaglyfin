using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Anaglyfin.FFmpegWrapper;
using Xunit;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// Policy tests for the POSIX stop-signal forwarder: which signals it listens for, which
/// process it hands them to under which number, what a signal with no child answers, and
/// when it stops listening.
/// </summary>
/// <remarks>
/// <para>
/// Everything here runs against an injected registration and an injected send, and that
/// is deliberate: the object under test is the wrapper's <em>policy</em> - the mapping,
/// the childless answer, the refusal of stale ids - and the two operating-system calls it
/// leans on are the two things a test must not lean on. A test that registered real
/// handlers and sent itself real signals would be one buggy handler away from killing the
/// test run itself, which is a worse failure than any it could find.
/// </para>
/// <para>
/// The exception is the pair of tests that send a <em>real</em> signal to a <em>real</em>
/// child: the mapping from <see cref="PosixSignal"/> to raw number, and the
/// <c>kill(2)</c> P/Invoke itself, are promises to the operating system, and promises to
/// the operating system are checked against the operating system. They signal a process
/// the test started and waited for - never the test host.
/// </para>
/// </remarks>
public sealed class PosixSignalForwarderTests
{
    private const string PosixShell = "/bin/sh";

    private const int ChildPid = 4242;

    // ----- what gets registered -------------------------------------------------------

    [Fact]
    public void TheThreeStopSignalsAreRegisteredAndNothingElse()
    {
        var registrar = new FakeRegistrar();
        var sender = new FakeSender();

        using var forwarder = new PosixSignalForwarder(registrar.Register, sender.Send);

        // Exactly the stop signals a server, a terminal or a dying session sends - not
        // SIGQUIT (a core dump nobody asked for). Ordered by name rather than value
        // because the named members' enum values are negative sentinels, not numbers.
        Assert.Equal(
            new[] { PosixSignal.SIGHUP, PosixSignal.SIGINT, PosixSignal.SIGTERM },
            registrar.Registrations
                .Select(registration => registration.Signal)
                .OrderBy(signal => signal.ToString(), StringComparer.Ordinal)
                .ToArray());

        // Listening began with the constructor: a signal that arrives before the child
        // does must find a listener that answers "no child", not the old silence.
        Assert.All(registrar.Registrations, registration => Assert.False(registration.Disposed));
    }

    [Fact]
    public void NothingIsRegisteredWherePosixSignalsCannotArrive()
    {
        var registrar = new FakeRegistrar();
        var sender = new FakeSender();

        // The Windows deployment: the platform question asked before anything is
        // registered, because registering there is an exception, not a no-op.
        using var forwarder = PosixSignalForwarder.Create(
            posixSignalsReachThisProcess: false,
            registrar.Register,
            sender.Send);

        Assert.IsNotType<PosixSignalForwarder>(forwarder);
        Assert.Empty(registrar.Registrations);

        // The launcher's whole sequence still has to be walkable on it - the launcher is
        // one code path for every platform.
        forwarder.AttachChild(ChildPid);
        forwarder.DetachChild();
        forwarder.Dispose();

        Assert.Empty(registrar.Registrations);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public void TheRealMachineDecidesWhetherTheRealForwarderListens()
    {
        // On the CI image (Linux) this asserts a listening forwarder; on Windows it
        // asserts the silent one. IsSupported is exactly the platform the runtime's own
        // registration API excludes, so the two halves of the assertion pin the gate to
        // the platform this suite is actually running on.
        Assert.Equal(!OperatingSystem.IsWindows(), PosixSignalForwarder.IsSupported);

        using var forwarder = PosixSignalForwarder.Create();
        Assert.Equal(PosixSignalForwarder.IsSupported, forwarder is PosixSignalForwarder);
    }

    // ----- what a signal does -----------------------------------------------------------

    [Theory]
    [InlineData(PosixSignal.SIGHUP, 1)]
    [InlineData(PosixSignal.SIGINT, 2)]
    [InlineData(PosixSignal.SIGTERM, 15)]
    public void AStopSignalReachesTheChildUnderTheNumberItArrivedUnder(PosixSignal signal, int signalNumber)
    {
        var registrar = new FakeRegistrar();
        var sender = new FakeSender();

        using var forwarder = new PosixSignalForwarder(registrar.Register, sender.Send);
        forwarder.AttachChild(ChildPid);

        var forwarded = registrar.Invoke(signal);

        // The same number, the same child: a SIGTERM answered with a SIGKILL would deny
        // FFmpeg the final segment and the finished playlist its graceful stop exists for.
        Assert.Equal(new[] { (ChildPid, signalNumber) }, sender.Sent);

        // Forwarded means canceled-by-us: the wrapper outlives the signal, waits for the
        // child, and reports the child's exit code rather than its own signal death.
        Assert.True(forwarded);
    }

    [Theory]
    [InlineData(PosixSignal.SIGHUP)]
    [InlineData(PosixSignal.SIGINT)]
    [InlineData(PosixSignal.SIGTERM)]
    public void ASignalWithNoChildSentNothingAndAbsorbsNothing(PosixSignal signal)
    {
        var registrar = new FakeRegistrar();
        var sender = new FakeSender();

        using var forwarder = new PosixSignalForwarder(registrar.Register, sender.Send);

        var forwarded = registrar.Invoke(signal);

        // Nothing runs, and the signal goes on to stop the wrapper as it stopped FFmpeg
        // before any wrapper existed. An always-cancel handler here would leave a wrapper
        // no stop signal can reach.
        Assert.False(forwarded);
        Assert.Empty(sender.Sent);
    }

    [Theory]
    [InlineData(PosixSignal.SIGHUP)]
    [InlineData(PosixSignal.SIGINT)]
    [InlineData(PosixSignal.SIGTERM)]
    public void ASignalAfterTheChildIsGoneSendsNothingToTheIdItOnceHad(PosixSignal signal)
    {
        var registrar = new FakeRegistrar();
        var sender = new FakeSender();

        using var forwarder = new PosixSignalForwarder(registrar.Register, sender.Send);
        forwarder.AttachChild(ChildPid);
        forwarder.DetachChild();

        var forwarded = registrar.Invoke(signal);

        // The exited child's id belongs to the operating system again - by the next
        // signal it can name any other process on the machine, and kill(2) would not know
        // the difference. After DetachChild the number must be unremembered, not unread.
        Assert.False(forwarded);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public void OnlyTheNewestChildIsEverSignalled()
    {
        var registrar = new FakeRegistrar();
        var sender = new FakeSender();

        using var forwarder = new PosixSignalForwarder(registrar.Register, sender.Send);
        forwarder.AttachChild(111);
        forwarder.DetachChild();
        forwarder.AttachChild(222);

        Assert.True(registrar.Invoke(PosixSignal.SIGTERM));

        // One launch is one encoder, so in the wrapper this pair only ever comes from
        // one launcher loop; whichever pair it is, the old id never gets the signal.
        Assert.Equal(new[] { (222, 15) }, sender.Sent);
    }

    [Fact]
    public void ASecondChildOnOneForwarderIsRefusedRatherThanTracked()
    {
        var registrar = new FakeRegistrar();
        var sender = new FakeSender();

        using var forwarder = new PosixSignalForwarder(registrar.Register, sender.Send);
        forwarder.AttachChild(ChildPid);

        // Silent retargeting would mean the launcher forgot the encoder it is guarding,
        // and the first one would go unstopped; a refused attach is that bug as itself.
        Assert.Throws<InvalidOperationException>(() => forwarder.AttachChild(ChildPid + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => forwarder.AttachChild(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => forwarder.AttachChild(-7));
    }

    [Fact]
    public void ASenderFaultStaysInsideTheHandlerAndStillBuysTheWait()
    {
        var registrar = new FakeRegistrar();
        var sender = new FakeSender { Throw = new InvalidOperationException("no send for you") };

        using var forwarder = new PosixSignalForwarder(registrar.Register, sender.Send);
        forwarder.AttachChild(ChildPid);

        // A handler that throws takes the wrapper down mid-signal with the child still
        // running - the orphan this type exists to prevent. And a child that may be
        // running is still worth waiting for.
        Assert.True(registrar.Invoke(PosixSignal.SIGTERM));
    }

    // ----- lifecycle ---------------------------------------------------------------------

    [Fact]
    public void DisposingUnregistersEverythingAndIsSafeToRepeat()
    {
        var registrar = new FakeRegistrar();
        var sender = new FakeSender();

        var forwarder = new PosixSignalForwarder(registrar.Register, sender.Send);
        forwarder.AttachChild(ChildPid);

        forwarder.Dispose();
        forwarder.Dispose();

        Assert.All(registrar.Registrations, registration => Assert.True(registration.Disposed));
        Assert.Empty(sender.Sent);
    }

    // ----- the mapping, against the operating system --------------------------------------

    [Theory]
    [InlineData(PosixSignal.SIGHUP, 1)]
    [InlineData(PosixSignal.SIGINT, 2)]
    [InlineData(PosixSignal.SIGTERM, 15)]
    public void TheForwardedSignalsCarryTheNumbersPosixGivesThem(PosixSignal signal, int signalNumber)
    {
        // Not a cast of the enum: this table is the one promise the wrapper makes to
        // POSIX about signal identity, and 1/2/15 are these signals' numbers on every
        // platform that has them.
        Assert.Equal(signalNumber, PosixSignalForwarder.SignalNumber(signal));
    }

    [Fact]
    public void ASignalThatIsNotOneOfTheThreeHasNoNumberToSend()
    {
        // Anything outside the three stop signals is refused at the mapping itself.
        // SIGQUIT is a core dump nobody asked the wrapper to forward. The raw numbers -
        // 9, which is SIGKILL's, and 15, which is SIGTERM's - are refused too, and that
        // is not a hole: the wrapper only ever registers the named members, so a raw
        // number arriving here is a caller reaching outside what was registered.
        Assert.Throws<ArgumentException>(() => PosixSignalForwarder.SignalNumber(PosixSignal.SIGQUIT));
        Assert.Throws<ArgumentException>(() => PosixSignalForwarder.SignalNumber((PosixSignal)9));
        Assert.Throws<ArgumentException>(() => PosixSignalForwarder.SignalNumber((PosixSignal)15));
    }

    [Fact]
    public void SendRefusesImpossibleTargetsBeforeTheOperatingSystemIsAsked()
    {
        // No kill(2) with a non-positive pid - the C API's own error case, answered
        // before it can be handed a signal to broadcast (pid 0 signals the whole group).
        Assert.Throws<ArgumentOutOfRangeException>(() => PosixSignalForwarder.SendSignal(0, 15));
        Assert.Throws<ArgumentOutOfRangeException>(() => PosixSignalForwarder.SendSignal(-1, 15));
        Assert.Throws<ArgumentOutOfRangeException>(() => PosixSignalForwarder.SendSignal(1234, 0));
    }

    [Theory]
    [InlineData(PosixSignal.SIGHUP, "HUP")]
    [InlineData(PosixSignal.SIGINT, "INT")]
    [InlineData(PosixSignal.SIGTERM, "TERM")]
    public void TheRealSendDeliversEachForwardedSignalToARealChild(PosixSignal signal, string trapName)
    {
        // The two links nothing else can prove - that SignalNumber's numbers are the
        // numbers kill(2) accepts, and that the P/Invoke resolves and binds - tested the
        // only honest way: a real signal to a real child that reports what it heard.
        // Where /bin/sh is not there (Windows), this is the promise the deployment never
        // makes anyway, and the test passes trivially like the launcher's own.
        if (!PosixSignalForwarder.IsSupported || !File.Exists(PosixShell))
        {
            return;
        }

        var report = Path.Combine(Path.GetTempPath(), "anaglyfin-signal-" + Guid.NewGuid().ToString("N") + ".txt");

        using var child = StartTrappingChild(report, trapName);

        try
        {
            // Nothing may be sent before the trap is up: a signal delivered to a shell
            // that has not installed its handler yet is a shell that died of the default
            // disposition with nothing written, and that is a flake rather than a finding.
            WaitUntilReported(child, report, "ready");

            Assert.Equal(0, PosixSignalForwarder.SendSignal(child.Id, PosixSignalForwarder.SignalNumber(signal)));

            // The trap writes its name over the report and exits; waiting for the exit
            // is the wait for the delivery, with the process object as its own evidence.
            Assert.True(child.WaitForExit(TimeSpan.FromSeconds(10)), "the child never reacted to the signal");
            Assert.Equal(trapName, File.ReadAllText(report));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: false);
                child.WaitForExit(TimeSpan.FromSeconds(5));
            }

            TryDelete(report);
        }
    }

    // ----- fixture ------------------------------------------------------------------------

    /// <summary>
    /// Starts a <c>/bin/sh</c> that answers one named trap by writing that name to
    /// <paramref name="report"/> and exiting; anything else kills it the ordinary way.
    /// </summary>
    private static Process StartTrappingChild(string report, string trapName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = PosixShell,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        // A sleep loop rather than `wait`, so the trap always runs: a POSIX shell
        // services a trapped signal when the foreground command it was waiting on ends,
        // and the foreground command here ends every few tens of milliseconds. The
        // "ready" write comes after the trap line for the same reason the test waits on
        // it: it is the first moment the handler is guaranteed to exist.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            "trap 'printf " + trapName + " > \"$1\"; exit 0' " + trapName
            + "; printf ready > \"$1\"; while :; do sleep 0.05; done");
        startInfo.ArgumentList.Add("anaglyfin-signal-test");
        startInfo.ArgumentList.Add(report);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The test could not start a shell to signal.");
    }

    /// <summary>
    /// Waits for the child to have written <paramref name="text"/> to its report.
    /// </summary>
    private static void WaitUntilReported(Process child, string report, string text)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (true)
        {
            try
            {
                if (File.Exists(report)
                    && text.Equals(File.ReadAllText(report), StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (IOException)
            {
                // A half-written line is "not yet": the write and the read race freely.
            }

            if (child.HasExited)
            {
                Assert.Fail("The signalling child died before it reported '" + text + "'.");
            }

            Thread.Sleep(millisecondsTimeout: 20);

            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("The signalling child never reported '" + text + "'.");
            }
        }
    }

    private sealed class FakeRegistrar
    {
        public List<Registration> Registrations { get; } = new();

        public IDisposable Register(PosixSignal signal, PosixSignalForwarder.StopSignalHandler handler)
        {
            var registration = new Registration(signal, handler);
            Registrations.Add(registration);
            return registration;
        }

        /// <summary>Stands in for the operating system delivering the signal.</summary>
        public bool Invoke(PosixSignal signal)
            => Registrations.Single(registration => registration.Signal == signal).Handler();

        public sealed class Registration : IDisposable
        {
            public Registration(PosixSignal signal, PosixSignalForwarder.StopSignalHandler handler)
            {
                Signal = signal;
                Handler = handler;
            }

            public PosixSignal Signal { get; }

            public PosixSignalForwarder.StopSignalHandler Handler { get; }

            public bool Disposed { get; private set; }

            public void Dispose() => Disposed = true;
        }
    }

    private sealed class FakeSender
    {
        public List<(int ProcessId, int SignalNumber)> Sent { get; } = new();

        public Exception? Throw { get; set; }

        public int Send(int processId, int signalNumber)
        {
            if (Throw is { } failure)
            {
                throw failure;
            }

            Sent.Add((processId, signalNumber));
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temp file is not a test failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

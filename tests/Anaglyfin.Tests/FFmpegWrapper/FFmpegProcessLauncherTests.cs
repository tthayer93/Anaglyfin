using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Anaglyfin.FFmpegWrapper;
using Xunit;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// Contract tests for the real process launcher: the exit code of the child is the exit
/// code of the wrapper, and the arguments reach the child exactly as they were handed over.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place in the wrapper that executes something, and the property it has to
/// prove cannot be tested against a fake: a command built as an argument list must not be
/// re-interpreted. A shell in the middle would not fail loudly - it would quietly eat the
/// <c>$</c> of a subtitle path, split a filename at a space, or turn a bracket in a media
/// path into a file list - and every one of those would surface as "the encode failed"
/// somewhere else. So these tests run a real process and read back what that process saw.
/// </para>
/// <para>
/// <c>/bin/sh</c> stands in for FFmpeg because the launcher does not care what the binary
/// is, and it is the executable this can rely on: it is the shell the toolchain image is
/// itself written with. Where it is not there - a Windows developer machine - the two
/// process tests have nothing to execute and pass trivially, which is honest for tests
/// whose whole subject is a POSIX <c>exec</c>.
/// </para>
/// </remarks>
public sealed class FFmpegProcessLauncherTests
{
    private const string PosixShell = "/bin/sh";

    [Fact]
    public void ABlankBinaryNameIsRejectedBeforeAnythingIsStarted()
    {
        var launcher = new FFmpegProcessLauncher();

        Assert.Throws<ArgumentException>(() => launcher.Launch(" ", new[] { "-version" }));
        Assert.Throws<ArgumentNullException>(() => launcher.Launch("/usr/bin/ffmpeg", null!));
    }

    [Fact]
    public void ANullArgumentIsRejectedRatherThanDropped()
    {
        var launcher = new FFmpegProcessLauncher();

        // A hole in the vector would otherwise reach FFmpeg as a missing option value, the
        // kind of damage that only shows up in the finished encode.
        Assert.Throws<ArgumentException>(() => launcher.Launch(PosixShell, new[] { "-c", null! }));
    }

    [Fact]
    public void ABinaryThatIsNotThereIsAnExceptionRatherThanAnExitCode()
    {
        var launcher = new FFmpegProcessLauncher();

        // Only a process that actually ran gets to decide the wrapper's exit code, so a
        // missing binary has to travel as the failure it is for the application to refuse
        // with - rather than as a code the server would misread as an encode failure.
        var missing = Path.Combine(Path.GetTempPath(), "anaglyfin-not-a-binary-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<Win32Exception>(() => launcher.Launch(missing, new[] { "-version" }));
    }

    [Fact]
    public void TheChildDecidesTheExitCodeTheWrapperSees()
    {
        if (!File.Exists(PosixShell))
        {
            return;
        }

        var launcher = new FFmpegProcessLauncher();

        // 42 belongs to the child and to nobody else: the launcher neither normalises a
        // failure down to 1 nor reports 0 because starting the process worked.
        Assert.Equal(42, launcher.Launch(PosixShell, new[] { "-c", "exit 42" }));
    }

    [Fact]
    public void ArgumentsReachTheChildAsDataAndNotAsSyntax()
    {
        if (!File.Exists(PosixShell))
        {
            return;
        }

        var report = Path.Combine(Path.GetTempPath(), "anaglyfin-launcher-" + Guid.NewGuid().ToString("N") + ".txt");

        // The dangerous characters a Jellyfin command line really does contain, in the one
        // argument the launcher is asked to deliver. The child writes what it received to a
        // file, so the assertion compares the argument handed to the launcher against the
        // argument the process saw: with a shell in between, the variable would have been
        // expanded, the substitution would have run, the quote would have closed the string
        // and the asterisk would have become a file list.
        var argument = "$HOME `id` \"quoted\" 'single' * ; echo $(touch /nope) Movie (2010) [S01].mkv";

        try
        {
            var exitCode = new FFmpegProcessLauncher().Launch(
                PosixShell,
                new[] { "-c", "printf '%s' \"$1\" > \"$2\"", "anaglyfin", argument, report });

            Assert.Equal(0, exitCode);
            Assert.Equal(argument, File.ReadAllText(report));
        }
        finally
        {
            TryDelete(report);
        }
    }

    // ----- the child under stop signals ------------------------------------------------

    [Fact]
    public void TheLauncherProtectsTheChildExactlyAsLongAsThatChildRuns()
    {
        if (!File.Exists(PosixShell))
        {
            return;
        }

        var forwarders = new List<FakeChildSignalForwarder>();
        var launcher = new FFmpegProcessLauncher(() =>
        {
            var forwarder = new FakeChildSignalForwarder();
            forwarders.Add(forwarder);
            return forwarder;
        });

        Assert.Equal(42, launcher.Launch(PosixShell, new[] { "-c", "exit 42" }));

        // One forwarder per launch - its registrations are scoped to the encoder they
        // speak for, never to the wrapper process that may outlive it by a thousand
        // playbacks - and the etiquette around the child is attach, detach, dispose.
        var forwarder = Assert.Single(forwarders);
        Assert.Equal(new[] { "attach", "detach", "dispose" }, forwarder.Events);

        // The id named is the running child's own: this is the number a forwarded stop
        // signal will be sent to, so a wrong or stale one is an orphan or a stranger.
        var attached = Assert.Single(forwarder.AttachedProcessIds);
        Assert.True(attached > 0, "the launcher attached a process that has no positive id.");

        Assert.True(forwarder.IsDisposed);
    }

    [Fact]
    public void AChildThatNeverStartedIsNeverAttached()
    {
        var missing = Path.Combine(Path.GetTempPath(), "anaglyfin-not-a-binary-" + Guid.NewGuid().ToString("N"));

        var forwarder = new FakeChildSignalForwarder();
        var launcher = new FFmpegProcessLauncher(() => forwarder);

        Assert.Throws<Win32Exception>(() => launcher.Launch(missing, new[] { "-version" }));

        // The refusal the application already reports must not leave a half-taught
        // forwarder behind: no child was ever named, and the listener is given back.
        Assert.Empty(forwarder.AttachedProcessIds);
        Assert.Equal(new[] { "dispose" }, forwarder.Events);
    }

    [Fact]
    public void ANullForwarderFactoryIsAProgrammingErrorRatherThanAPlatform()
    {
        Assert.Throws<ArgumentNullException>(() => new FFmpegProcessLauncher(null!));
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

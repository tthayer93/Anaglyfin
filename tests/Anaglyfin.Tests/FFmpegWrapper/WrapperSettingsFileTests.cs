using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Anaglyfin.Configuration;
using Anaglyfin.FFmpegWrapper;
using Xunit;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// Tests for the settings document the plugin leaves for the wrapper: the shape both processes
/// have to agree on, the way it is written so that a reader never sees half of it, and the way
/// it is read so that anything unreadable costs nothing.
/// </summary>
/// <remarks>
/// <para>
/// The document crosses a process boundary in the worst possible direction - from a component
/// that is configured at runtime to a process that is started per playback and cannot ask
/// questions - so the two halves of its contract are tested from both ends: what the writer
/// produces is read back, and what a reader is expected to survive includes the malformations a
/// human editing a file in a deployment directory actually produces.
/// </para>
/// <para>
/// The round trips are through real files in the job's temp directory rather than through a
/// stream, because atomicity, directory creation and the file's mode are properties of the
/// filesystem and of nothing a test could substitute for it.
/// </para>
/// </remarks>
public sealed class WrapperSettingsFileTests : IDisposable
{
    private readonly string _root;

    public WrapperSettingsFileTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "anaglyfin-wrapper-settings-tests", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void TheDocumentIsTheSchemaVersionAndTheDepthRequestAndNothingElse()
    {
        // The rule "do not expose the full settings object to the wrapper" is a shape, and a shape
        // is testable: two keys at the root, four in the request, and no name in the document that
        // belongs to a setting the wrapper has no business reading.
        var document = JsonNode.Parse(WrapperSettingsFile.ToJson(new SubtitleDepthSettings(true, SubtitleDepthMode.Plane, 0, 6)))!
            .AsObject();

        Assert.Equal(
            new[] { "schemaVersion", "subtitleDepth" },
            document.Select(pair => pair.Key).ToArray());

        var depth = document["subtitleDepth"]!.AsObject();

        Assert.Equal(
            new[] { "enabled", "mode", "shiftPixels", "plane" },
            depth.Select(pair => pair.Key).ToArray());

        Assert.Equal(WrapperSettingsFile.SchemaVersion, document["schemaVersion"]!.GetValue<int>());
        Assert.True(depth["enabled"]!.GetValue<bool>());
        Assert.Equal("plane", depth["mode"]!.GetValue<string>());
        Assert.Equal(0, depth["shiftPixels"]!.GetValue<int>());
        Assert.Equal(6, depth["plane"]!.GetValue<int>());
    }

    [Fact]
    public void TheDocumentCarriesNoCredentialAndNoTextSomebodyTyped()
    {
        // Everything the plugin knows about the server - its library paths, its colours, its
        // profile list - is out of scope here, and the settings themselves hold no secret. The
        // point of the test is that the document cannot acquire either of those by accident: it is
        // booleans, integers and one fixed vocabulary of mode names.
        var samples = new[]
        {
            SubtitleDepthSettings.Disabled,
            new SubtitleDepthSettings(true, SubtitleDepthMode.Automatic, 0, 0),
            new SubtitleDepthSettings(true, SubtitleDepthMode.ConstantShift, -64, 0),
            new SubtitleDepthSettings(true, SubtitleDepthMode.Plane, 0, 31)
        };

        foreach (var settings in samples)
        {
            var json = WrapperSettingsFile.ToJson(settings);

            foreach (var forbidden in new[] { "token", "secret", "password", "credential", "api", "path" })
            {
                Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
            }

            // No filename crosses the boundary either: where this document lives is what the
            // deployment decides, and the document itself has no reason to know.
            Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), json, StringComparison.Ordinal);
            Assert.DoesNotContain(Path.AltDirectorySeparatorChar.ToString(), json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheDisabledRequestIsStatedRatherThanOmitted()
    {
        // An absent field and a stated "off" are the same to this reader, but not to the next one,
        // and not to an administrator reading the file: off is a decision somebody took, and the
        // document says so.
        var json = WrapperSettingsFile.ToJson(SubtitleDepthSettings.Disabled);
        var depth = JsonNode.Parse(json)!.AsObject()["subtitleDepth"]!.AsObject();

        Assert.False(depth["enabled"]!.GetValue<bool>());
        Assert.Equal("automatic", depth["mode"]!.GetValue<string>());
        Assert.Equal(0, depth["shiftPixels"]!.GetValue<int>());
        Assert.Equal(0, depth["plane"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(SubtitleDepthMode.Automatic, 0, 0)]
    [InlineData(SubtitleDepthMode.ConstantShift, 24, 0)]
    [InlineData(SubtitleDepthMode.ConstantShift, -24, 0)]
    [InlineData(SubtitleDepthMode.Plane, 0, 13)]
    public void EveryModeTheWriterStatesIsTheModeTheReaderReads(SubtitleDepthMode mode, int shift, int plane)
    {
        var written = new SubtitleDepthSettings(true, mode, shift, plane);

        var read = WrapperSettingsFile.Parse(WrapperSettingsFile.ToJson(written));

        Assert.Equal(written, read);
    }

    [Fact]
    public void TheDisabledRequestRoundTrips()
    {
        Assert.Equal(
            SubtitleDepthSettings.Disabled,
            WrapperSettingsFile.Parse(WrapperSettingsFile.ToJson(SubtitleDepthSettings.Disabled)));
    }

    [Fact]
    public void TheModeNamesAreTheOnesTheDepthGrammarUses()
    {
        // These three strings are the contract with the deployment: an administrator who edits the
        // file by hand types one of them, and a name outside them is not a mode this build knows.
        Assert.Equal("automatic", WrapperSettingsFile.ModeWireName(SubtitleDepthMode.Automatic));
        Assert.Equal("constantShift", WrapperSettingsFile.ModeWireName(SubtitleDepthMode.ConstantShift));
        Assert.Equal("plane", WrapperSettingsFile.ModeWireName(SubtitleDepthMode.Plane));

        Assert.True(WrapperSettingsFile.TryReadMode("automatic", out var automatic));
        Assert.Equal(SubtitleDepthMode.Automatic, automatic);

        Assert.True(WrapperSettingsFile.TryReadMode("constantShift", out var shift));
        Assert.Equal(SubtitleDepthMode.ConstantShift, shift);

        Assert.True(WrapperSettingsFile.TryReadMode("plane", out var plane));
        Assert.Equal(SubtitleDepthMode.Plane, plane);

        // Read case-insensitively because the file is hand-editable; refused otherwise.
        Assert.True(WrapperSettingsFile.TryReadMode("  CONSTANTSHIFT ", out var sloppy));
        Assert.Equal(SubtitleDepthMode.ConstantShift, sloppy);

        Assert.False(WrapperSettingsFile.TryReadMode("flat", out _));
        Assert.False(WrapperSettingsFile.TryReadMode("auto", out _));
        Assert.False(WrapperSettingsFile.TryReadMode("shift", out _));
        Assert.False(WrapperSettingsFile.TryReadMode(null, out _));
        Assert.False(WrapperSettingsFile.TryReadMode(string.Empty, out _));

        // An ordinal nobody declared has no wire name of its own, and the name it does get is the
        // mode the reader would have fallen back to in any case.
        Assert.Equal("automatic", WrapperSettingsFile.ModeWireName((SubtitleDepthMode)44));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("\"automatic\"")]
    [InlineData("{ \"schemaVersion\": 1, \"subtitleDepth\": { \"enabled\": true, ")]
    public void ADocumentThatIsNotADocumentIsReadAsNoDepth(string json)
    {
        Assert.Equal(SubtitleDepthSettings.Disabled, WrapperSettingsFile.Parse(json));
    }

    [Theory]
    [InlineData("""{ "subtitleDepth": { "enabled": true, "mode": "plane", "plane": 4 } }""")]
    [InlineData("""{ "schemaVersion": 0, "subtitleDepth": { "enabled": true, "mode": "plane", "plane": 4 } }""")]
    [InlineData("""{ "schemaVersion": 2, "subtitleDepth": { "enabled": true, "mode": "plane", "plane": 4 } }""")]
    [InlineData("""{ "schemaVersion": "1", "subtitleDepth": { "enabled": true, "mode": "plane", "plane": 4 } }""")]
    [InlineData("""{ "schemaVersion": 1 }""")]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": null }""")]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": "true", "mode": "plane", "plane": 4 } }""")]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "mode": "plane", "plane": 4 } }""")]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "plane": 4 } }""")]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "depth", "plane": 4 } }""")]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": 2, "plane": 4 } }""")]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "plane" } }""")]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "plane", "plane": 32 } }""")]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "plane", "plane": -1 } }""")]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "plane", "plane": "4" } }""")]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "plane", "plane": 4.5 } }""")]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "constantShift" } }""")]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "constantShift", "shiftPixels": 65 } }""")]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "constantShift", "shiftPixels": -65 } }""")]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "constantShift", "shiftPixels": null } }""")]
    public void ADocumentWhoseMeaningThisBuildDoesNotKnowIsReadAsNoDepth(string json)
    {
        // The list is what a deployment produces when a file is edited by hand or written by a
        // build with different rules: a wrong shape, a wrong type, a number off the end of a range.
        // Each is answered the way an absent file is, because the alternative is a transcode that
        // refuses to start over a settings file.
        Assert.Equal(SubtitleDepthSettings.Disabled, WrapperSettingsFile.Parse(json));
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": false, "mode": "nonsense" } }""")]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": false, "mode": "plane", "plane": 9999 } }""")]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": false } }""")]
    public void ASwitchThatIsOffDoesNotHaveToStateAnythingElse(string json)
    {
        // Whatever the rest of a switched-off document says - or fails to say - it is a request for
        // nothing, and the reader does not go looking for a problem to find.
        Assert.Equal(SubtitleDepthSettings.Disabled, WrapperSettingsFile.Parse(json));
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "automatic", "shiftPixels": 999 } }""", SubtitleDepthMode.Automatic, 0, 0)]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "plane", "plane": 2, "shiftPixels": 999 } }""", SubtitleDepthMode.Plane, 0, 2)]
    [InlineData("""{ "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "constantShift", "shiftPixels": 7, "plane": 400 } }""", SubtitleDepthMode.ConstantShift, 7, 0)]
    public void TheNumberThePickedModeDoesNotUseIsNeitherReadNorJudged(
        string json,
        SubtitleDepthMode mode,
        int shift,
        int plane)
    {
        // The document states both numbers whether or not the mode uses them, so a reader that
        // judged the unused one would refuse requests that are perfectly well formed - including
        // every document an older build wrote, which is free to leave anything in the field nobody
        // asked it for.
        Assert.Equal(new SubtitleDepthSettings(true, mode, shift, plane), WrapperSettingsFile.Parse(json));
    }

    [Fact]
    public void AKeyThisBuildDoesNotKnowIsNotAReasonToRefuseTheOnesItDoes()
    {
        // A newer plugin can write a field this wrapper has never heard of. What it cannot do is
        // make the fields this wrapper does read unreadable, because that would put every server
        // that was upgraded out of order instead of one setting behind.
        const string NewerDocument = """
            { "schemaVersion": 1, "subtitleDepth": { "enabled": true, "mode": "plane", "plane": 5 }, "concurrency": 99, "notes": "hand written" }
            """;

        Assert.Equal(
            new SubtitleDepthSettings(true, SubtitleDepthMode.Plane, 0, 5),
            WrapperSettingsFile.Parse(NewerDocument));
    }

    [Fact]
    public void TheRequestArrivesThroughTheFileTheDeploymentNamed()
    {
        var path = Path.Combine(_root, "nested", "deeper", WrapperSettingsFile.DefaultFileName);

        Assert.True(WrapperSettingsFile.TryWrite(
            path,
            new SubtitleDepthSettings(true, SubtitleDepthMode.ConstantShift, -12, 0),
            out var failure));

        Assert.Null(failure);

        // The plugin writes on startup, into a directory a fresh deployment has not created yet:
        // "could not find a path" is not an acceptable answer from a best-effort write.
        Assert.True(File.Exists(path));
        Assert.Equal(
            new SubtitleDepthSettings(true, SubtitleDepthMode.ConstantShift, -12, 0),
            WrapperSettingsFile.Read(path));
    }

    [Fact]
    public void TheWriteLeavesOneFileAndNoTemporaryBehind()
    {
        var directory = Path.Combine(_root, "once");
        var path = Path.Combine(directory, WrapperSettingsFile.DefaultFileName);

        WrapperSettingsFile.TryWrite(path, new SubtitleDepthSettings(true, SubtitleDepthMode.Plane, 0, 3), out _);
        WrapperSettingsFile.TryWrite(path, new SubtitleDepthSettings(true, SubtitleDepthMode.Plane, 0, 4), out _);

        // Two saves over the same target: the rename has to replace, and the temporary file it was
        // written as has to be gone. A leftover per save is a data directory filling up with
        // documents nobody reads.
        Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        Assert.Equal(
            new SubtitleDepthSettings(true, SubtitleDepthMode.Plane, 0, 4),
            WrapperSettingsFile.Read(path));
    }

    [Fact]
    public void TheFileEndsUpReadableByTheProcessThatHasToOpenIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_root, "modes", WrapperSettingsFile.DefaultFileName);

        WrapperSettingsFile.TryWrite(path, SubtitleDepthSettings.Disabled, out var failure);

        Assert.Null(failure);

        // The writer is the server process and the reader is the FFmpeg the server started; a umask
        // that denies others would otherwise hand the wrapper a file it cannot open, and the depth
        // setting would go missing with nothing to show for it.
        var mode = File.GetUnixFileMode(path);

        Assert.True(mode.HasFlag(UnixFileMode.OtherRead), $"{mode} denies reads to other users.");
        Assert.False(mode.HasFlag(UnixFileMode.OtherExecute), "a settings file is not a program.");
    }

    [Fact]
    public void AWriteThatCannotBeMadeIsReportedAndNotThrown()
    {
        // The callers are the plugin's startup and its save path, and neither can afford an
        // exception: the settings the server stored are the settings, and this document is one
        // consumer of them that could not be reached. A file standing where the document's
        // directory has to be is the unwritable location every deployment eventually turns out to
        // have picked.
        var blocker = Path.Combine(_root, "configurations");

        Directory.CreateDirectory(_root);
        File.WriteAllText(blocker, "a file where the settings directory should be");

        var path = Path.Combine(blocker, WrapperSettingsFile.DefaultFileName);

        var wrote = WrapperSettingsFile.TryWrite(path, SubtitleDepthSettings.Disabled, out var failure);

        Assert.False(wrote);
        Assert.NotNull(failure);
        Assert.False(File.Exists(path));

        // And the thing that went wrong did not leave a half-written document behind either.
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void ReadingAFileNobodyWroteIsTheSameAnswerAsReadingNothing()
    {
        var absent = Path.Combine(_root, "absent", WrapperSettingsFile.DefaultFileName);
        var unreadable = Path.Combine(_root, "unreadable");

        Assert.Equal(SubtitleDepthSettings.Disabled, WrapperSettingsFile.Read(absent));
        Assert.Equal(SubtitleDepthSettings.Disabled, WrapperSettingsFile.Read(null));
        Assert.Equal(SubtitleDepthSettings.Disabled, WrapperSettingsFile.Read(string.Empty));
        Assert.Equal(SubtitleDepthSettings.Disabled, WrapperSettingsFile.Read("   "));

        // A plugin that died mid-save leaves exactly this behind.
        Directory.CreateDirectory(Path.GetDirectoryName(absent)!);
        File.WriteAllText(absent, "{ \"schemaVersion\": 1, \"subtitleDepth\": { \"enabled\": true, \"mo");

        Assert.Equal(SubtitleDepthSettings.Disabled, WrapperSettingsFile.Read(absent));

        // A directory where the file was expected is a real misconfiguration rather than a
        // hypothetical, and it is answered the same way.
        Directory.CreateDirectory(unreadable);

        Assert.Equal(SubtitleDepthSettings.Disabled, WrapperSettingsFile.Read(unreadable));
    }

    [Fact]
    public void AFileAnotherBuildWroteInAFormThisOneCannotReadIsAnsweredWithFlatSubtitles()
    {
        var path = Path.Combine(_root, "stale", WrapperSettingsFile.DefaultFileName);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        File.WriteAllText(path, "this is not the document anybody expected");
        Assert.Equal(SubtitleDepthSettings.Disabled, WrapperSettingsFile.Read(path));

        File.WriteAllText(path, string.Empty);
        Assert.Equal(SubtitleDepthSettings.Disabled, WrapperSettingsFile.Read(path));
    }

    [Theory]
    [InlineData("/deployed/anaglyfin-wrapper-settings.json")]
    [InlineData("  /deployed/anaglyfin-wrapper-settings.json  ")]
    public void TheVariableNamesTheFileForBothProcesses(string configured)
    {
        // The plugin writes where the wrapper was told to read, and a variable padded out by a
        // compose file or an init script still names one file rather than one file and a missing
        // one.
        var path = WrapperSettingsFile.ResolveWritePath(_ => configured, "/var/lib/jellyfin/plugins/configurations");

        Assert.Equal("/deployed/anaglyfin-wrapper-settings.json", path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnsetVariableLeavesTheFileInThePluginHands(string? configured)
    {
        // The default is not a guess at where a server keeps its data: it is the directory this
        // plugin's own settings live in, which the plugin knows and the wrapper does not.
        var path = WrapperSettingsFile.ResolveWritePath(_ => configured, "/var/lib/jellyfin/plugins/configurations");

        Assert.Equal(
            Path.Combine("/var/lib/jellyfin/plugins/configurations", WrapperSettingsFile.DefaultFileName),
            path);
    }

    [Fact]
    public void TheVariableTheDeploymentSetsIsTheVariableBothSidesRead()
    {
        // One name, spelled once, is the whole of the interface between a deployment that wants the
        // depth honoured and one that does not.
        Assert.Equal("ANAGLYFIN_WRAPPER_SETTINGS", WrapperSettingsFile.EnvironmentVariable);

        var view = new Dictionary<string, string?> { [WrapperSettingsFile.EnvironmentVariable] = "/tmp/x.json" };

        Assert.Equal(
            "/tmp/x.json",
            WrapperSettingsFile.ReadConfiguredPath(name => view.TryGetValue(name, out var value) ? value : null));

        Assert.Null(WrapperSettingsFile.ReadConfiguredPath(_ => null));
    }

    [Fact]
    public void ResolvingAPathNeedsSomewhereToPutTheFile()
    {
        Assert.Throws<ArgumentNullException>(() => WrapperSettingsFile.ResolveWritePath(null!, "/var/lib"));
        Assert.Throws<ArgumentNullException>(() => WrapperSettingsFile.ReadConfiguredPath(null!));
        Assert.Throws<ArgumentException>(() => WrapperSettingsFile.ResolveWritePath(_ => null, "  "));
        Assert.Throws<ArgumentNullException>(() => WrapperSettingsFile.ToJson(null!));
        Assert.Throws<ArgumentNullException>(() => WrapperSettingsFile.Parse(null!));
        Assert.Throws<ArgumentNullException>(() => WrapperSettingsFile.TryWrite("/tmp/x.json", null!, out _));
        Assert.Throws<ArgumentException>(() => WrapperSettingsFile.TryWrite(" ", SubtitleDepthSettings.Disabled, out _));
    }

    [Fact]
    public void TheSettingsModelAndTheDocumentAgreeOnEveryRequestTheModelCanMake()
    {
        // The plugin writes what its own settings helper says, and the wrapper reads what it is
        // given: this is the one test that walks the whole path from a stored request to a read one,
        // through the two halves the two processes actually use.
        var stored = new[]
        {
            new PluginConfiguration(),
            new PluginConfiguration { SubtitleDepthMode = SubtitleDepthMode.Flat },
            new PluginConfiguration
            {
                SubtitleDepthMode = SubtitleDepthMode.ConstantShift,
                SubtitleDepthShift = -48,
                SubtitleDepthPlane = 9
            },
            new PluginConfiguration
            {
                SubtitleDepthMode = SubtitleDepthMode.Plane,
                SubtitleDepthShift = 33,
                SubtitleDepthPlane = 30
            },

            // Out of range: the document states the refusal, so the wrapper never has to guess what
            // an impossible number was meant to mean.
            new PluginConfiguration
            {
                SubtitleDepthMode = SubtitleDepthMode.ConstantShift,
                SubtitleDepthShift = 500
            }
        };

        var path = Path.Combine(_root, "model", WrapperSettingsFile.DefaultFileName);

        foreach (var configuration in stored)
        {
            var request = configuration.GetEffectiveSubtitleDepth();

            Assert.True(WrapperSettingsFile.TryWrite(path, request, out var failure));
            Assert.Null(failure);

            Assert.Equal(request, WrapperSettingsFile.Read(path));
            Assert.Equal(request, WrapperSettingsFile.Parse(WrapperSettingsFile.ToJson(request)));
        }
    }

    [Fact]
    public void TheDocumentStaysInsideOneScreenful()
    {
        // Not a style rule: this file is read over a shoulder, in a container, by somebody looking
        // for why a setting did not take. If it ever grows to the size of the settings object it
        // replaced, that is the moment to ask what is crossing the process boundary.
        var json = WrapperSettingsFile.ToJson(new SubtitleDepthSettings(true, SubtitleDepthMode.Plane, 0, 31));

        Assert.True(json.Length < 400, $"The settings document is {json.Length} characters.");
        Assert.EndsWith("}" + Environment.NewLine, json, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

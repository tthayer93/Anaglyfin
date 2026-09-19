using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anaglyfin.Configuration;

namespace Anaglyfin.FFmpegWrapper;

/// <summary>
/// The settings file the plugin writes for the FFmpeg wrapper to read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a file at all.</b> The wrapper is started by the server, in the server's process
/// tree, outside the plugin and outside its container. It has no access to the plugin's
/// settings object, its DI container or its logging, and the one channel that does reach
/// it - the environment - is written by the deployment, not by the administrator who edits
/// the settings. So the settings that have to survive the process boundary are handed over
/// as a file the plugin owns and the wrapper only reads: the deployment names the location
/// once, by setting <see cref="EnvironmentVariable"/>, and every save from then on is
/// visible to the wrapper without a restart.
/// </para>
/// <para>
/// <b>What it carries.</b> Exactly the subtitle depth request, and nothing else. The full
/// settings object is deliberately not mirrored: it holds profile ids, colours and a
/// concurrency limit that the wrapper either cannot use or already reads from its own
/// environment, and a channel that grows towards "everything the plugin knows" is how a
/// playback path ends up depending on a value its reader cannot validate. Everything here is
/// one switch, one mode name and two numbers, so the reader can reject the whole document on
/// a single shape check.
/// </para>
/// <para>
/// <b>One definition, two users.</b> The plugin writes it and the wrapper reads it, from this
/// class, so the spelling of the document cannot drift from one side to the other. The
/// wrapper's project already references the plugin's for the same reason
/// <see cref="WrapperArgumentRewriter"/> lives here: what both processes have to agree on is
/// written once.
/// </para>
/// </remarks>
public static class WrapperSettingsFile
{
    /// <summary>
    /// Name of the variable naming the file - the only way a deployment tells either
    /// process where it lives.
    /// </summary>
    public const string EnvironmentVariable = "ANAGLYFIN_WRAPPER_SETTINGS";

    /// <summary>
    /// File name used inside the plugin's own data directory when
    /// <see cref="EnvironmentVariable"/> is unset.
    /// </summary>
    public const string DefaultFileName = "anaglyfin-wrapper-settings.json";

    /// <summary>
    /// The version of the document this build writes, and the only one it reads.
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>The wire name of <see cref="SubtitleDepthMode.Automatic"/>.</summary>
    public const string AutomaticModeName = "automatic";

    /// <summary>The wire name of <see cref="SubtitleDepthMode.ConstantShift"/>.</summary>
    public const string ConstantShiftModeName = "constantShift";

    /// <summary>The wire name of <see cref="SubtitleDepthMode.Plane"/>.</summary>
    public const string PlaneModeName = "plane";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Resolves where the plugin writes.
    /// </summary>
    /// <param name="readEnvironmentVariable">
    /// Reads one environment variable; null when it is not set. Taken as a delegate so the
    /// precedence below is testable without editing the process environment, which is state a
    /// test does not own.
    /// </param>
    /// <param name="pluginDataPath">
    /// The directory the plugin's own settings live in, used as the default. Only the plugin
    /// can name it: the wrapper has no plugin directory to ask, which is exactly why a
    /// deployment that wants its wrapper to see these settings sets
    /// <see cref="EnvironmentVariable"/>.
    /// </param>
    /// <returns>The path to write the document to.</returns>
    /// <remarks>
    /// The variable wins when it names a path, because that is the path the wrapper was
    /// started with - and two files, each process reading a different one, is the failure this
    /// order exists to prevent.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="readEnvironmentVariable"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="pluginDataPath"/> is empty.</exception>
    public static string ResolveWritePath(Func<string, string?> readEnvironmentVariable, string pluginDataPath)
    {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDataPath);

        return ReadConfiguredPath(readEnvironmentVariable)
               ?? Path.Combine(pluginDataPath, DefaultFileName);
    }

    /// <summary>
    /// Reads the path <see cref="EnvironmentVariable"/> names, which is all the wrapper is
    /// told about where the settings live.
    /// </summary>
    /// <param name="readEnvironmentVariable">
    /// Reads one environment variable; null when it is not set.
    /// </param>
    /// <returns>
    /// The named path, or null when no variable names one - which the wrapper reads as "there
    /// was nothing to read", not as "the feature is off in the settings".
    /// </returns>
    /// <remarks>
    /// No default is guessed here, and that is the point: the wrapper cannot know where a
    /// plugin's data lives, so a wrapper started without the variable plays the server's
    /// ordinary way rather than reading a file some other deployment chose.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="readEnvironmentVariable"/> is null.</exception>
    public static string? ReadConfiguredPath(Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        var value = readEnvironmentVariable(EnvironmentVariable);

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Renders the document for one subtitle depth request.
    /// </summary>
    /// <param name="settings">The request to write out.</param>
    /// <returns>The JSON text, with a trailing newline.</returns>
    /// <remarks>
    /// Fixed key order, fixed names, and only booleans, integers and one of the three declared
    /// mode names - so the document cannot carry text an administrator typed, and cannot carry
    /// a credential, because the settings it is built from hold none. Indented because an
    /// administrator diagnosing a deployment opens this file with an editor.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    public static string ToJson(SubtitleDepthSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var document = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["subtitleDepth"] = new JsonObject
            {
                ["enabled"] = settings.Enabled,
                ["mode"] = ModeWireName(settings.Mode),
                ["shiftPixels"] = settings.ShiftPixels,
                ["plane"] = settings.Plane
            }
        };

        return document.ToJsonString(WriteOptions) + Environment.NewLine;
    }

    /// <summary>
    /// Reads a document.
    /// </summary>
    /// <param name="json">The file text.</param>
    /// <returns>
    /// The request it states, or <see cref="SubtitleDepthSettings.Disabled"/> for anything this
    /// build cannot read, including text that is not JSON.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The reader is as narrow as the writer: an unknown schema version, a missing object, a
    /// mode no name declares, or a number outside the range the filter honours is a document
    /// whose meaning this build does not know, and the one meaning it does know is "flat
    /// subtitles". Never a clamp, and never a failure - this document is not worth a playback,
    /// and a half-written one is a thing that happens when a plugin is saving while a wrapper is
    /// starting.
    /// </para>
    /// <para>
    /// Only <c>null</c> is refused outright, because that is not a document but a caller that
    /// has nothing to read.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    public static SubtitleDepthSettings Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !TryGetInt(root, "schemaVersion", out var schemaVersion)
                || schemaVersion != SchemaVersion
                || !root.TryGetProperty("subtitleDepth", out var depth)
                || depth.ValueKind != JsonValueKind.Object)
            {
                return SubtitleDepthSettings.Disabled;
            }

            if (!depth.TryGetProperty("enabled", out var enabled)
                || (enabled.ValueKind != JsonValueKind.True && enabled.ValueKind != JsonValueKind.False))
            {
                return SubtitleDepthSettings.Disabled;
            }

            return enabled.GetBoolean() ? ReadEnabled(depth) : SubtitleDepthSettings.Disabled;
        }
        catch (JsonException)
        {
            // Not JSON, or JSON nobody would call a document. Both settings processes are on the
            // other side of this, and neither is being told about it by exception.
            return SubtitleDepthSettings.Disabled;
        }
    }

    /// <summary>
    /// Reads a document from the file a wrapper invocation was pointed at.
    /// </summary>
    /// <param name="path">
    /// The file to read; null or blank means nothing was pointed at, which is where a wrapper
    /// started without <see cref="EnvironmentVariable"/> stands.
    /// </param>
    /// <returns>
    /// The stated request, or <see cref="SubtitleDepthSettings.Disabled"/> when the file does
    /// not exist, cannot be opened, is not JSON, or states something this build does not
    /// understand.
    /// </returns>
    /// <remarks>
    /// Nothing about this file is worth failing a transcode over, so every read failure -
    /// absent, locked, half-written by a plugin that died mid-save, written by a newer build -
    /// answers with the same flat subtitles an unconfigured server plays.
    /// </remarks>
    public static SubtitleDepthSettings Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return SubtitleDepthSettings.Disabled;
        }

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return SubtitleDepthSettings.Disabled;
        }
    }

    /// <summary>
    /// Writes a document, in place, atomically.
    /// </summary>
    /// <param name="path">The file to write.</param>
    /// <param name="settings">The request to state.</param>
    /// <param name="failure">What stopped the write, when this returns <c>false</c>.</param>
    /// <returns><c>true</c> when the file now states the request.</returns>
    /// <remarks>
    /// <para>
    /// Written as a temporary file in the destination directory and renamed over the target,
    /// because a wrapper can be reading at any moment: an in-place rewrite would let it read
    /// half a document, and half a document states nothing. A rename inside one directory is
    /// the operation that leaves a reader holding the old file or the new one, never a mix.
    /// </para>
    /// <para>
    /// The file is made world readable on the way, because the process that reads it is the
    /// server's rather than this one's, and a plugin running under a umask that denies others
    /// would otherwise write a settings file its own deployment cannot read.
    /// </para>
    /// <para>
    /// Failures come back as the answer rather than as an exception: the callers are the
    /// plugin's startup and save paths, where an unwritable directory has to cost an
    /// administrator a depth setting that does not take effect, not a plugin that cannot load.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public static bool TryWrite(string path, SubtitleDepthSettings settings, out Exception? failure)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var destination = Path.GetFullPath(path);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";

        failure = null;

        try
        {
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // No byte-order mark: a reader that expects JSON should not have to know which
            // editor last touched the deployment.
            File.WriteAllText(
                temporary,
                ToJson(settings),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            MakeWorldReadable(temporary);

            File.Move(temporary, destination, overwrite: true);
            temporary = null;

            return true;
        }
        catch (Exception exception) when (IsWriteFailure(exception))
        {
            failure = exception;

            TryDelete(temporary);

            return false;
        }
    }

    /// <summary>
    /// The wire name of one mode.
    /// </summary>
    /// <param name="mode">The mode to name.</param>
    /// <returns>
    /// The declared camelCase name, or <see cref="AutomaticModeName"/> for an ordinal no name
    /// declares - which reaches here only through a request that is off anyway, since
    /// <see cref="PluginConfigurationExtensions.GetEffectiveSubtitleDepth"/> refuses to name a
    /// mode it has not been told about.
    /// </returns>
    public static string ModeWireName(SubtitleDepthMode mode)
        => mode switch
        {
            SubtitleDepthMode.ConstantShift => ConstantShiftModeName,
            SubtitleDepthMode.Plane => PlaneModeName,
            _ => AutomaticModeName
        };

    /// <summary>
    /// Reads one mode name.
    /// </summary>
    /// <param name="text">The name the document states.</param>
    /// <param name="mode">The mode it names, when it names one.</param>
    /// <returns><c>true</c> when the name is one of the three this build writes.</returns>
    /// <remarks>
    /// Read case-insensitively, because this file is a deployment artefact an administrator
    /// opens and edits by hand; the spelling this build writes is the camelCase one.
    /// </remarks>
    public static bool TryReadMode(string? text, out SubtitleDepthMode mode)
    {
        var name = text?.Trim();

        if (string.Equals(name, AutomaticModeName, StringComparison.OrdinalIgnoreCase))
        {
            mode = SubtitleDepthMode.Automatic;
            return true;
        }

        if (string.Equals(name, ConstantShiftModeName, StringComparison.OrdinalIgnoreCase))
        {
            mode = SubtitleDepthMode.ConstantShift;
            return true;
        }

        if (string.Equals(name, PlaneModeName, StringComparison.OrdinalIgnoreCase))
        {
            mode = SubtitleDepthMode.Plane;
            return true;
        }

        mode = default;
        return false;
    }

    /// <summary>
    /// Reads a document whose feature is switched on, which is where the mode and the number
    /// it asks for start to matter.
    /// </summary>
    private static SubtitleDepthSettings ReadEnabled(JsonElement depth)
    {
        if (!depth.TryGetProperty("mode", out var mode)
            || mode.ValueKind != JsonValueKind.String
            || !TryReadMode(mode.GetString(), out var wanted))
        {
            return SubtitleDepthSettings.Disabled;
        }

        // Only the number the mode asks for is read, and it has to be stated: an enabled
        // constant shift with no shift stated is not a request for zero, it is a document
        // whose request cannot be recovered. The number the mode does not use is ignored
        // rather than inspected, since the writer states it as zero and a document from an
        // older build may not state it at all.
        return wanted switch
        {
            SubtitleDepthMode.Automatic
                => new SubtitleDepthSettings(true, SubtitleDepthMode.Automatic, 0, 0),

            SubtitleDepthMode.ConstantShift
                when TryGetInt(depth, "shiftPixels", out var shift) && SubtitleDepthSettings.IsShiftInRange(shift)
                => new SubtitleDepthSettings(true, SubtitleDepthMode.ConstantShift, shift, 0),

            SubtitleDepthMode.Plane
                when TryGetInt(depth, "plane", out var plane) && SubtitleDepthSettings.IsPlaneInRange(plane)
                => new SubtitleDepthSettings(true, SubtitleDepthMode.Plane, 0, plane),

            _ => SubtitleDepthSettings.Disabled
        };
    }

    /// <summary>
    /// Reads an integer property, refusing everything that is not one.
    /// </summary>
    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;

        return element.TryGetProperty(name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out value);
    }

    /// <summary>
    /// Gives the file the mode a process other than this one has to read it under.
    /// </summary>
    /// <remarks>
    /// The read bits are the whole ask: whoever wrote the file owns it, and the wrapper only
    /// ever opens it for reading. Skipped where the concept does not exist, and skipped - not
    /// failed - where the filesystem declines to record it, because a settings file that could
    /// not be given its mode is still a settings file.
    /// </remarks>
    private static void MakeWorldReadable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        catch (Exception exception) when (IsWriteFailure(exception))
        {
        }
    }

    private static bool IsWriteFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or InvalidOperationException;

    private static bool IsReadFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (IsWriteFailure(exception))
        {
            // A half-written temporary file left behind is untidy, and nothing else: what the
            // administrator asked for either arrived or did not, and the caller is being told
            // which.
        }
    }
}

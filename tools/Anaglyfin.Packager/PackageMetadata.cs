using System;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace Anaglyfin.Packager;

/// <summary>
/// The <c>meta.json</c> a Jellyfin install record is built from: the identity from
/// <c>Plugin.manifest.xml</c>, and the measured facts about the artifacts that carry it.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of value appear in this document, and the distinction is the reason the file is
/// generated at all. The identity fields are copied from the hand-maintained manifest, so a
/// hand-written copy of them can never drift from the assembly it ships with. The artifact
/// fields - archive name, size, checksum - are measured from the finished files, so the record
/// cannot describe a build other than the one it was written next to.
/// </para>
/// <para>
/// The document is written with a fixed key order and a fixed serializer configuration, and it
/// carries no timestamp: two packaging runs over the same build produce the same bytes, which
/// is what makes a recorded checksum worth comparing.
/// </para>
/// </remarks>
public sealed class PackageMetadata
{
    /// <summary>The file name the install record is written under.</summary>
    public const string FileName = "meta.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="PackageMetadata"/> class.
    /// </summary>
    /// <param name="manifest">The identity being shipped.</param>
    /// <param name="plugin">The plugin archive the document records.</param>
    /// <param name="wrapper">The wrapper artifact, when the package carries one.</param>
    public PackageMetadata(PluginManifest manifest, PluginArtifactRecord plugin, WrapperArtifactRecord? wrapper)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(plugin);

        Manifest = manifest;
        Plugin = plugin;
        Wrapper = wrapper;
    }

    /// <summary>Gets the identity the document declares.</summary>
    public PluginManifest Manifest { get; }

    /// <summary>Gets the plugin archive the document describes.</summary>
    public PluginArtifactRecord Plugin { get; }

    /// <summary>
    /// Gets the wrapper artifact the document describes, or <see langword="null"/> when the
    /// package carries none.
    /// </summary>
    public WrapperArtifactRecord? Wrapper { get; }

    /// <summary>
    /// Renders the document.
    /// </summary>
    /// <returns>The JSON text, with a trailing newline.</returns>
    public string ToJson()
    {
        var document = new JsonObject
        {
            ["name"] = Manifest.Name,
            ["guid"] = Manifest.Guid.ToString("D", CultureInfo.InvariantCulture),
            ["version"] = Manifest.Version,
            ["description"] = Manifest.Description,
            ["overview"] = Manifest.Overview,
            ["owner"] = Manifest.Owner,
            ["category"] = Manifest.Category,
            ["targetAbi"] = Manifest.TargetAbi,
            ["framework"] = Manifest.Framework,
            ["offline"] = Manifest.Offline,
            ["zipFileName"] = Plugin.ZipFileName,
            ["assemblyFileName"] = Plugin.AssemblyFileName,
            ["checksumType"] = Plugin.ChecksumType,
            ["checksum"] = Plugin.Checksum,
            ["size"] = Plugin.Size,
            ["packages"] = new JsonArray { PluginEntry() },
        };

        if (Wrapper is not null)
        {
            document["wrapper"] = new JsonObject
            {
                ["fileName"] = Wrapper.FileName,
                ["runtime"] = Wrapper.Runtime,
                ["checksumType"] = Wrapper.ChecksumType,
                ["checksum"] = Wrapper.Checksum,
                ["size"] = Wrapper.Size,
            };
        }

        return document.ToJsonString(WriteOptions) + "\n";
    }

    /// <summary>
    /// Writes the document into the artifact directory.
    /// </summary>
    /// <param name="artifactDirectory">Where the artifacts are installed from.</param>
    /// <returns>The path written.</returns>
    public string Write(string artifactDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);

        var path = Path.Combine(artifactDirectory, FileName);

        Directory.CreateDirectory(artifactDirectory);
        File.WriteAllText(path, ToJson());

        return path;
    }

    /// <summary>
    /// Reads a generated document back, applying the manifest's own rules to the values it
    /// claims.
    /// </summary>
    /// <param name="artifactDirectory">The directory that holds the document.</param>
    /// <returns>The metadata it records.</returns>
    /// <exception cref="PackagingError">
    /// The document is missing, unreadable, contradicts itself, or claims an identity that could
    /// not be installed.
    /// </exception>
    public static PackageMetadata Read(string artifactDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);

        var path = Path.Combine(artifactDirectory, FileName);

        if (!File.Exists(path))
        {
            throw new PackagingError(
                $"The install record '{path}' is missing. Run the packaging step before verifying the artifacts.");
        }

        JsonObject document;

        try
        {
            document = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new PackagingError($"The install record '{path}' is not a JSON object.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or NotSupportedException)
        {
            throw new PackagingError($"The install record '{path}' is not a package metadata document.", exception);
        }

        var manifest = PluginManifest.FromValues(
            Text(document, "name", path),
            Text(document, "guid", path),
            Text(document, "version", path),
            Text(document, "description", path),
            Text(document, "overview", path),
            Text(document, "owner", path),
            Text(document, "category", path),
            Text(document, "targetAbi", path),
            Text(document, "framework", path),
            Boolean(document, "offline", path),
            $"'{path}'");

        var plugin = new PluginArtifactRecord(
            Text(document, "zipFileName", path),
            Text(document, "assemblyFileName", path),
            ChecksumType(document, "checksumType", path),
            Text(document, "checksum", path),
            Number(document, "size", path));

        Require(
            string.Equals(plugin.ZipFileName, manifest.ZipFileName, StringComparison.Ordinal),
            $"'{path}' records the archive '{plugin.ZipFileName}', which is not how {manifest} packages ('{manifest.ZipFileName}').");

        Require(
            string.Equals(plugin.AssemblyFileName, manifest.AssemblyFileName, StringComparison.Ordinal),
            $"'{path}' names the assembly '{plugin.AssemblyFileName}', which is not the '{manifest.AssemblyFileName}' {manifest} installs as.");

        var entry = SinglePackageEntry(document, path);

        Require(
            string.Equals(Text(entry, "assemblyFileName", path), plugin.AssemblyFileName, StringComparison.Ordinal)
            && string.Equals(Text(entry, "checksum", path), plugin.Checksum, StringComparison.Ordinal)
            && string.Equals(Text(entry, "checksumType", path), plugin.ChecksumType, StringComparison.Ordinal)
            && Number(entry, "size", path) == plugin.Size
            && string.Equals(Text(entry, "version", path), manifest.Version, StringComparison.Ordinal),
            $"'{path}' contradicts itself: its 'packages' entry does not record the archive the document describes.");

        WrapperArtifactRecord? wrapper = null;

        if (document["wrapper"] is JsonObject wrapperNode)
        {
            // Rejected before anything is combined with the artifact directory: the record is
            // untrusted input by the time a gate or an administrator hands it back.
            var wrapperFileName = Text(wrapperNode, "fileName", path);

            WrapperArtifact.RequireFileName(wrapperFileName);

            wrapper = new WrapperArtifactRecord(
                wrapperFileName,
                Text(wrapperNode, "runtime", path),
                ChecksumType(wrapperNode, "checksumType", path),
                Text(wrapperNode, "checksum", path),
                Number(wrapperNode, "size", path));
        }

        return new PackageMetadata(manifest, plugin, wrapper);
    }

    /// <summary>
    /// The package entry the plugin repository format expects. It records the same archive as
    /// the top level of the document, from the same measurements, and <see cref="Read"/> refuses
    /// a document whose two copies disagree.
    /// </summary>
    /// <returns>The single package entry.</returns>
    private JsonObject PluginEntry() =>
        new()
        {
            ["assemblyFileName"] = Plugin.AssemblyFileName,
            ["checksum"] = Plugin.Checksum,
            ["checksumType"] = Plugin.ChecksumType,
            ["size"] = Plugin.Size,
            ["version"] = Manifest.Version,
            ["runtime"] = string.Empty,
            ["targetplatform"] = string.Empty,
        };

    private static JsonArray PackageEntries(JsonObject document, string origin)
    {
        var value = document["packages"]
            ?? throw new PackagingError($"The install record '{origin}' has no 'packages' array.");

        JsonArray packages;

        try
        {
            packages = value.AsArray();
        }
        catch (InvalidOperationException exception)
        {
            throw new PackagingError($"The install record '{origin}' has a 'packages' value that is not an array.", exception);
        }

        Require(
            packages.Count == 1,
            $"The install record '{origin}' has {packages.Count} 'packages' entries. A plugin package has exactly one.");

        return packages;
    }

    private static JsonObject SinglePackageEntry(JsonObject document, string origin)
    {
        var packages = PackageEntries(document, origin);
        var entry = packages[0];

        if (entry is null)
        {
            throw new PackagingError($"The install record '{origin}' has a 'packages' entry that is not an object.");
        }

        try
        {
            return entry.AsObject();
        }
        catch (InvalidOperationException exception)
        {
            throw new PackagingError($"The install record '{origin}' has a 'packages' entry that is not an object.", exception);
        }
    }

    private static string Text(JsonObject document, string field, string origin)
    {
        var node = Required(document, field, origin);

        try
        {
            return node.GetValue<string>()
                ?? throw new PackagingError($"The install record '{origin}' has '{field}' as null.");
        }
        catch (InvalidOperationException exception)
        {
            throw new PackagingError($"The install record '{origin}' has '{field}' as something other than text.", exception);
        }
    }

    private static long Number(JsonObject document, string field, string origin)
    {
        var node = Required(document, field, origin);

        try
        {
            return node.GetValue<long>();
        }
        catch (InvalidOperationException exception)
        {
            throw new PackagingError($"The install record '{origin}' has '{field}' as something other than a byte count.", exception);
        }
    }

    private static bool Boolean(JsonObject document, string field, string origin)
    {
        var node = Required(document, field, origin);

        try
        {
            return node.GetValue<bool>();
        }
        catch (InvalidOperationException exception)
        {
            throw new PackagingError($"The install record '{origin}' has '{field}' as something other than true or false.", exception);
        }
    }

    private static string ChecksumType(JsonObject document, string field, string origin)
    {
        var value = Text(document, field, origin);

        Require(
            string.Equals(value, Digest.Sha256Algorithm, StringComparison.Ordinal),
            $"The install record '{origin}' declares '{field}' as '{value}'; this job checksums with '{Digest.Sha256Algorithm}'.");

        return value;
    }

    private static JsonNode Required(JsonObject document, string field, string origin) =>
        document[field] ?? throw new PackagingError($"The install record '{origin}' has no '{field}'.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new PackagingError(message);
        }
    }
}

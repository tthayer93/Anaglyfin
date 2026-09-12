using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Anaglyfin.Packager;

/// <summary>
/// The plugin identity a Jellyfin package has to declare, as written by hand in
/// <c>Plugin.manifest.xml</c>.
/// </summary>
/// <remarks>
/// <para>
/// This manifest is the only place those values are authored. Everything the packaging job
/// emits is derived from it - the archive name, the assembly name Jellyfin will look for,
/// the whole of <c>meta.json</c> - which is why every rule about them lives here, at the one
/// place they can be wrong: a version that cannot go into a file name, a name the compiled
/// assembly will not carry, an ABI this repository does not ship for.
/// </para>
/// <para>
/// The same rules are applied to the values read back out of a generated
/// <see cref="PackageMetadata"/>, so a metadata document is checked against the identity it
/// claims rather than against whatever the writer happened to hold in memory.
/// </para>
/// </remarks>
public sealed class PluginManifest
{
    /// <summary>The oldest Jellyfin server line this repository is allowed to package for.</summary>
    public const int MinimumTargetAbiMajor = 12;

    /// <summary>The oldest .NET framework line a packaged plugin is allowed to target.</summary>
    public const int MinimumFrameworkMajor = 10;

    private static readonly Regex PackageVersionPattern = new(
        @"^\d+\.\d+\.\d+(\.\d+)?$",
        RegexOptions.CultureInvariant);

    private static readonly Regex FrameworkPattern = new(
        @"^net(?<major>\d+)\.(?<minor>\d+)$",
        RegexOptions.CultureInvariant);

    private PluginManifest(
        string name,
        Guid guid,
        string version,
        string description,
        string overview,
        string owner,
        string category,
        string targetAbi,
        string framework,
        bool offline)
    {
        Name = name;
        Guid = guid;
        Version = version;
        Description = description;
        Overview = overview;
        Owner = owner;
        Category = category;
        TargetAbi = targetAbi;
        Framework = framework;
        Offline = offline;
    }

    /// <summary>Gets the plugin name, which is also the installed assembly's name.</summary>
    public string Name { get; }

    /// <summary>Gets the fixed plugin identifier the server keys the installation on.</summary>
    public Guid Guid { get; }

    /// <summary>Gets the package version, verbatim as declared (for example <c>0.1.0</c>).</summary>
    public string Version { get; }

    /// <summary>Gets the one-line description the server lists.</summary>
    public string Description { get; }

    /// <summary>Gets the longer text the server lists.</summary>
    public string Overview { get; }

    /// <summary>Gets the maintainer the server lists.</summary>
    public string Owner { get; }

    /// <summary>Gets the catalogue section the server lists.</summary>
    public string Category { get; }

    /// <summary>Gets the Jellyfin server ABI this build was compiled against.</summary>
    public string TargetAbi { get; }

    /// <summary>Gets the target framework moniker the assembly was built for.</summary>
    public string Framework { get; }

    /// <summary>Gets a value indicating whether the plugin needs no network at install.</summary>
    public bool Offline { get; }

    /// <summary>Gets the file name the assembly has to carry inside the archive.</summary>
    public string AssemblyFileName => Name + ".dll";

    /// <summary>Gets the deterministic archive name derived from the identity.</summary>
    public string ZipFileName => Name + "_" + Version + ".zip";

    /// <summary>
    /// Reads and validates the hand-maintained manifest.
    /// </summary>
    /// <param name="path">The manifest file.</param>
    /// <returns>The identity it declares.</returns>
    /// <exception cref="PackagingError">
    /// The file is missing, is not well-formed XML, or declares an identity that could not be
    /// packaged safely.
    /// </exception>
    public static PluginManifest Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new PackagingError($"The plugin identity manifest was not found at '{path}'.");
        }

        XDocument document;

        try
        {
            document = XDocument.Load(path, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new PackagingError($"The plugin identity manifest at '{path}' is not well-formed XML.", exception);
        }

        return FromDocument(document, path);
    }

    /// <summary>
    /// Reads and validates a manifest carried as text, such as the copy embedded in the
    /// plugin assembly.
    /// </summary>
    /// <param name="manifestXml">The manifest document.</param>
    /// <param name="origin">Where it came from, for the refusal message.</param>
    /// <returns>The identity it declares.</returns>
    /// <exception cref="PackagingError">The document is unreadable or declares an unusable identity.</exception>
    public static PluginManifest Parse(string manifestXml, string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestXml);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        XDocument document;

        try
        {
            document = XDocument.Parse(manifestXml, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new PackagingError($"The plugin identity manifest in {origin} is not well-formed XML.", exception);
        }

        return FromDocument(document, origin);
    }

    /// <summary>
    /// Validates a set of identity values that were read back from a generated document.
    /// </summary>
    /// <param name="name">The plugin name.</param>
    /// <param name="guid">The plugin identifier as text.</param>
    /// <param name="version">The package version as text.</param>
    /// <param name="description">The description.</param>
    /// <param name="overview">The overview.</param>
    /// <param name="owner">The maintainer.</param>
    /// <param name="category">The catalogue section.</param>
    /// <param name="targetAbi">The Jellyfin ABI as text.</param>
    /// <param name="framework">The target framework moniker.</param>
    /// <param name="offline">Whether the plugin needs no network at install.</param>
    /// <param name="origin">Where the values came from, for the refusal message.</param>
    /// <returns>The validated identity.</returns>
    /// <exception cref="PackagingError">One of the values could not be packaged safely.</exception>
    public static PluginManifest FromValues(
        string name,
        string guid,
        string version,
        string description,
        string overview,
        string owner,
        string category,
        string targetAbi,
        string framework,
        bool offline,
        string origin)
    {
        RequireText(name, "name", origin);
        RequireText(version, "version", origin);
        RequireText(description, "description", origin);
        RequireText(overview, "overview", origin);
        RequireText(owner, "owner", origin);
        RequireText(category, "category", origin);
        RequireText(targetAbi, "targetAbi", origin);
        RequireText(framework, "framework", origin);

        if (!Guid.TryParse(guid, out var identifier))
        {
            throw new PackagingError($"The identity manifest in {origin} declares <guid>'{guid}', which is not a GUID.");
        }

        if (!PackageVersionPattern.IsMatch(version))
        {
            throw new PackagingError(
                $"The identity manifest in {origin} declares <version>'{version}'. A package version has to be three"
                + " or four dot-separated numbers, because the archive name and the checksummed artifact are named after it.");
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || AnyWhiteSpace(name))
        {
            throw new PackagingError(
                $"The identity manifest in {origin} declares <name>'{name}'. The plugin name becomes both the archive"
                + " name and the installed assembly name, so it cannot contain path separators or whitespace.");
        }

        // Qualified: this type's own Version property is the declared package version, not the
        // System.Version the target ABI is parsed as.
        if (!System.Version.TryParse(targetAbi, out var abi) || abi.Major < MinimumTargetAbiMajor)
        {
            throw new PackagingError(
                $"The identity manifest in {origin} declares <targetAbi>'{targetAbi}'. This repository packages for"
                + $" Jellyfin {MinimumTargetAbiMajor}.0 and later only, and does not target older servers.");
        }

        var frameworkMatch = FrameworkPattern.Match(framework);
        if (!frameworkMatch.Success
            || int.Parse(frameworkMatch.Groups["major"].Value, CultureInfo.InvariantCulture) < MinimumFrameworkMajor)
        {
            throw new PackagingError(
                $"The identity manifest in {origin} declares <framework>'{framework}'. A packaged plugin has to target"
                + $" a 'net<N>.<Y>' framework of .NET {MinimumFrameworkMajor} or later.");
        }

        return new PluginManifest(
            name,
            identifier,
            version,
            description,
            overview,
            owner,
            category,
            targetAbi,
            framework,
            offline);
    }

    /// <summary>
    /// Lists every identity value in this identity that differs from the one it is compared
    /// with, so a disagreement names all of it at once.
    /// </summary>
    /// <param name="expected">The identity that is taken as authoritative - the manifest.</param>
    /// <param name="subject">
    /// What is being compared with the manifest, used to phrase each difference ("the generated
    /// <c>meta.json</c>", "the manifest embedded in the assembly").
    /// </param>
    /// <returns>One line per differing field, empty when the two agree.</returns>
    public IReadOnlyList<string> IdentityDifferences(PluginManifest expected, string subject)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var differences = new List<string>();

        Compare(differences, subject, "name", Name, expected.Name);
        Compare(differences, subject, "guid", Guid.ToString("D", CultureInfo.InvariantCulture), expected.Guid.ToString("D", CultureInfo.InvariantCulture));
        Compare(differences, subject, "version", Version, expected.Version);
        Compare(differences, subject, "description", Description, expected.Description);
        Compare(differences, subject, "overview", Overview, expected.Overview);
        Compare(differences, subject, "owner", Owner, expected.Owner);
        Compare(differences, subject, "category", Category, expected.Category);
        Compare(differences, subject, "targetAbi", TargetAbi, expected.TargetAbi);
        Compare(differences, subject, "framework", Framework, expected.Framework);
        Compare(differences, subject, "offline", Offline.ToString(CultureInfo.InvariantCulture), expected.Offline.ToString(CultureInfo.InvariantCulture));

        return differences;
    }

    /// <summary>Returns the plugin name and version, which is how an artifact wants to be called out.</summary>
    /// <returns>The identity in <c>Name version</c> form.</returns>
    public override string ToString() => Name + " " + Version;

    private static PluginManifest FromDocument(XDocument document, string origin)
    {
        var root = document.Root;

        if (root is null)
        {
            throw new PackagingError($"The plugin identity manifest at '{origin}' has no root element.");
        }

        return FromValues(
            Value(root, "name", origin),
            Value(root, "guid", origin),
            Value(root, "version", origin),
            Value(root, "description", origin),
            Value(root, "overview", origin),
            Value(root, "owner", origin),
            Value(root, "category", origin),
            Value(root, "targetAbi", origin),
            Value(root, "framework", origin),
            OfflineValue(root, origin),
            origin);
    }

    private static string Value(XElement root, string field, string origin)
    {
        var element = root.Element(field);

        if (element is null)
        {
            throw new PackagingError($"The plugin identity manifest at '{origin}' has no <{field}> element.");
        }

        return element.Value.Trim();
    }

    private static bool OfflineValue(XElement root, string origin)
    {
        var element = root.Element("offline");

        if (element is null)
        {
            return false;
        }

        if (!bool.TryParse(element.Value.Trim(), out var offline))
        {
            throw new PackagingError(
                $"The plugin identity manifest at '{origin}' declares <offline>'{element.Value.Trim()}',"
                + " which is neither 'true' nor 'false'.");
        }

        return offline;
    }

    private static void RequireText(string value, string field, string origin)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PackagingError($"The plugin identity manifest in {origin} leaves <{field}> empty.");
        }
    }

    private static bool AnyWhiteSpace(string value)
    {
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                return true;
            }
        }

        return false;
    }

    private static void Compare(List<string> differences, string subject, string field, string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            differences.Add($"in {subject}, '{field}' is '{actual}' while the manifest declares '{expected}'");
        }
    }
}

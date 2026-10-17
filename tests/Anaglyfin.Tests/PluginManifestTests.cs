using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Anaglyfin.Packager;
using Xunit;

namespace Anaglyfin.Tests;

/// <summary>
/// Verifies the packaging manifest and the plugin implementation agree.
/// </summary>
public class PluginManifestTests
{
    private const string ManifestResourceName = "Anaglyfin.Plugin.manifest.xml";

    // 0.2.2 is the version this release cuts, and it is written down once - here. Every other
    // place the number appears is derived: the project files compile it into their assembly
    // versions, the manifest beside them states it, and the packaging job reads it out of that
    // manifest to name the archive. A bump therefore has to be noticed somewhere on purpose,
    // and this is that somewhere; the tests below then refuse any build that disagrees.
    private const string ReleaseVersion = "0.2.2";

    [Fact]
    public void PluginAssemblyCarriesTheIdentityManifest()
    {
        var manifest = typeof(Plugin).Assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(".manifest.xml", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(ManifestResourceName, manifest);
    }

    [Fact]
    public void ManifestIdentityMatchesThePluginImplementation()
    {
        var root = ReadManifest().Root;

        Assert.NotNull(root);
        Assert.Equal("Anaglyfin", root!.Element("name")?.Value);
        Assert.Equal(Plugin.PluginId, Guid.Parse(root.Element("guid")?.Value ?? string.Empty));
        Assert.Equal(typeof(Plugin).Assembly.GetName().Version?.ToString(3), root.Element("version")?.Value);
    }

    [Fact]
    public void TheAssemblyTheManifestAndThePackageAgreeOnTheReleaseVersion()
    {
        // Three places state the number for the plugin itself: the project file, which becomes the
        // assembly version the server logs; the hand-maintained manifest beside it; and the archive
        // name the packaging job derives from that manifest. The test above proves the first two
        // cannot disagree; this one names the number, so a tag cannot be cut with the loaded plugin
        // and the packaged archive speaking different versions.
        var manifest = ReadManifest();

        Assert.Equal(ReleaseVersion, manifest.Root?.Element("version")?.Value);
        Assert.Equal(ReleaseVersion, typeof(Plugin).Assembly.GetName().Version?.ToString(3));
        Assert.Equal(
            "Anaglyfin_" + ReleaseVersion + ".zip",
            PluginManifest.Parse(manifest.ToString(), "the plugin assembly").ZipFileName);
    }

    [Fact]
    public void EveryProjectTheReleaseBuildsCarriesThatSameVersion()
    {
        // One tag builds three projects and ships them as one release: the plugin the server loads,
        // the wrapper an administrator publishes from that same tag and installs beside FFmpeg, and
        // the packaging job CI runs to write the release's artifacts. A bump that moved only the
        // plugin would cut a release whose wrapper binary and whose packaging tool still call
        // themselves the previous version, so the three assemblies bearing one release's name would
        // disagree about which release they are - and the assembly version is what a server log, a
        // file listing, and the CI log each read off them. All three are read out of the assemblies
        // this build produced rather than repeated here, so a project that missed the bump fails its
        // own line and names itself.
        Assert.Equal(ReleaseVersion, VersionOf<Plugin>());
        Assert.Equal(ReleaseVersion, VersionOf<Anaglyfin.FFmpegWrapper.WrapperApplication>());
        Assert.Equal(ReleaseVersion, VersionOf<PluginManifest>());
    }

    [Fact]
    public void ManifestDeclaresTheJellyfinAbiAndFrameworkItBuildsAgainst()
    {
        var root = ReadManifest().Root;

        Assert.NotNull(root);
        Assert.Equal("12.0.0", root!.Element("targetAbi")?.Value);
        Assert.Equal("net10.0", root.Element("framework")?.Value);
    }

    private static XDocument ReadManifest()
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(ManifestResourceName)
            ?? throw new FileNotFoundException($"Embedded manifest '{ManifestResourceName}' was not found.");

        using (stream)
        {
            return XDocument.Load(stream);
        }
    }

    // The version a project's own <Version> property compiles into its assembly, in the X.Y.Z form a
    // release states. Three parts rather than four, because the assembly's fourth part is the build
    // number and the release does not name one.
    private static string VersionOf<T>()
        => typeof(T).Assembly.GetName().Version?.ToString(3)
            ?? throw new InvalidOperationException(
                $"The assembly carrying '{typeof(T).FullName}' declares no assembly version.");
}

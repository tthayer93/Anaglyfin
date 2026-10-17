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
        // 0.2.2 is the version this branch prepares a tag for, and three places state it: the
        // project file, which becomes the assembly version the server logs; the hand-maintained
        // manifest beside it; and the archive name the packaging job derives from that manifest.
        // The test above proves the first two cannot disagree; this one names the number, so a
        // tag cannot be cut with the loaded plugin and the packaged archive speaking different
        // versions - and a bump has to be written down here, on purpose.
        const string ReleaseVersion = "0.2.2";

        var manifest = ReadManifest();

        Assert.Equal(ReleaseVersion, manifest.Root?.Element("version")?.Value);
        Assert.Equal(ReleaseVersion, typeof(Plugin).Assembly.GetName().Version?.ToString(3));
        Assert.Equal(
            "Anaglyfin_" + ReleaseVersion + ".zip",
            PluginManifest.Parse(manifest.ToString(), "the plugin assembly").ZipFileName);
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
}

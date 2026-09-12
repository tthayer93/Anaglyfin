using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
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

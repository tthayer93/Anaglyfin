using System.IO;
using MediaBrowser.Common.Configuration;

namespace Anaglyfin.Tests.Stubs;

/// <summary>
/// Minimal <see cref="IApplicationPaths"/> used to construct the plugin outside a server.
/// </summary>
/// <remarks>
/// Paths are rooted in the job temp directory and are only read by the plugin base
/// class; nothing here creates directories or files. A test that needs to watch what the
/// plugin writes passes its own root, so that the files it is watching are its own - the
/// default root is shared by every test that builds a plugin, and those run in parallel.
/// </remarks>
internal sealed class FakeApplicationPaths : IApplicationPaths
{
    private readonly string? _root;

    public FakeApplicationPaths(string? root = null)
    {
        _root = root;
    }

    public string ProgramDataPath => Root;

    public string WebPath => Path.Combine(Root, "web");

    public string ProgramSystemPath => Root;

    public string DataPath => Path.Combine(Root, "data");

    public string ImageCachePath => Path.Combine(Root, "cache", "images");

    public string PluginsPath => Path.Combine(Root, "plugins");

    public string PluginConfigurationsPath => Path.Combine(Root, "plugins", "configurations");

    public string LogDirectoryPath => Path.Combine(Root, "logs");

    public string ConfigurationDirectoryPath => Path.Combine(Root, "config");

    public string SystemConfigurationFilePath => Path.Combine(Root, "config", "system.xml");

    public string CachePath => Path.Combine(Root, "cache");

    public string TempDirectory => Path.Combine(Root, "cache", "temp");

    public string VirtualDataPath => "/anaglyfin-data";

    public string TrickplayPath => Path.Combine(Root, "cache", "trickplay");

    public string BackupPath => Path.Combine(Root, "backups");

    private string Root
        => _root ?? Path.Combine(Path.GetTempPath(), "anaglyfin-test", "application-paths");

    public void MakeSanityCheckOrThrow()
    {
    }

    public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
    {
    }
}

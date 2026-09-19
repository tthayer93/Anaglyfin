using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using Anaglyfin.Configuration;
using Anaglyfin.FFmpegWrapper;
using Anaglyfin.Profiles;
using Anaglyfin.Tests.Stubs;
using Anaglyfin.VersionItems;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;
using Xunit;

namespace Anaglyfin.Tests;

/// <summary>
/// Verifies the plugin scaffold exposes what the Jellyfin server needs from it.
/// </summary>
/// <remarks>
/// The settings-document tests watch files, and each of them watches a directory of its own:
/// several tests in this assembly build a plugin, they run in parallel, and the default fake
/// application paths point all of them at the same folder.
/// </remarks>
public class PluginTests : IDisposable
{
    private readonly List<string> _roots = new();

    [Fact]
    public void PluginIsAJellyfinPluginWithAFixedIdentity()
    {
        var plugin = CreatePlugin();

        Assert.IsAssignableFrom<IPlugin>(plugin);
        Assert.NotEqual(Guid.Empty, Plugin.PluginId);
        Assert.Equal(Plugin.PluginId, plugin.Id);
        Assert.Equal("Anaglyfin", plugin.Name);
    }

    [Fact]
    public void PluginIdentityIsStableAcrossInstances()
    {
        Assert.Equal(CreatePlugin().Id, CreatePlugin().Id);
    }

    [Fact]
    public void PluginInfoIsPopulatedForServerConsumers()
    {
        var plugin = CreatePlugin();

        var info = plugin.GetPluginInfo();

        Assert.Equal("Anaglyfin", info.Name);
        Assert.Equal(Plugin.PluginId, info.Id);
        Assert.Equal(plugin.Description, info.Description);
        Assert.Equal(typeof(Plugin).Assembly.GetName().Version, info.Version);

        // The server derives the settings file name from the plugin assembly name.
        var expectedConfigurationFile = Path.ChangeExtension($"{typeof(Plugin).Assembly.GetName().Name}.dll", ".xml");
        Assert.Equal(expectedConfigurationFile, info.ConfigurationFileName);
    }

    [Fact]
    public void PluginAssemblyTargetsTheNet10ServerLine()
    {
        var target = typeof(Plugin).Assembly.GetCustomAttribute<TargetFrameworkAttribute>();

        Assert.NotNull(target);
        Assert.Equal(".NETCoreApp,Version=v10.0", target!.FrameworkName);
    }

    [Fact]
    public void PluginAssemblyReferencesTheDeclaredJellyfinAbi()
    {
        var controller = typeof(Plugin).Assembly.GetReferencedAssemblies()
            .SingleOrDefault(assembly => string.Equals(assembly.Name, "MediaBrowser.Controller", StringComparison.Ordinal));

        Assert.NotNull(controller);
        Assert.Equal(12, controller!.Version?.Major ?? 0);
    }

    [Fact]
    public void SavingTheSettingsAsksForAPassOverTheLibrary()
    {
        var trigger = new RecordingTrigger();
        var plugin = new Plugin(new FakeApplicationPaths(), new RecordingXmlSerializer(), trigger);

        // Which profiles are enabled decides which version items the library should hold, so a save
        // that only rewrote the settings file would leave every MVC title offering yesterday's
        // versions until something else happened to notice.
        plugin.UpdateConfiguration(new PluginConfiguration
        {
            EnabledProfileIds = new List<string> { ProfileIds.SideBySideFull }
        });

        Assert.Equal(1, trigger.FullPassRequests);
        Assert.Empty(trigger.ItemRequests);
    }

    [Fact]
    public void EverySaveAsksForAPassAndTheQueueDecidesHowManyAreRun()
    {
        var trigger = new RecordingTrigger();
        var plugin = new Plugin(new FakeApplicationPaths(), new RecordingXmlSerializer(), trigger);

        for (var i = 0; i < 5; i++)
        {
            plugin.UpdateConfiguration(new PluginConfiguration());
        }

        // The plugin asks and walks away: the coalescing is the queue's job, and the save request's
        // answer must not depend on how large the library is.
        Assert.Equal(5, trigger.FullPassRequests);
    }

    [Fact]
    public void APluginWithoutAQueueStillSavesItsSettings()
    {
        // The trigger is optional by contract, and settings are the plugin's core job: a plugin that
        // could not resolve its own queue has to load and save anyway, with the versions appearing at
        // playback time instead of in the library.
        var plugin = new Plugin(new FakeApplicationPaths(), new RecordingXmlSerializer());

        Assert.Null(Record.Exception(() => plugin.UpdateConfiguration(new PluginConfiguration())));
    }

    [Fact]
    public void ASaveHandsTheWrapperWhatTheAdministratorAskedFor()
    {
        var paths = new FakeApplicationPaths(PrivateRoot());
        var plugin = new Plugin(paths, new RecordingXmlSerializer());

        plugin.UpdateConfiguration(new PluginConfiguration
        {
            SubtitleDepthEnabled = true,
            SubtitleDepthMode = SubtitleDepthMode.Plane,
            SubtitleDepthShift = 999,
            SubtitleDepthPlane = 6
        });

        // What arrives is the settings read through the rules rather than as stored: the wrapper is
        // not a second place the ranges get enforced, and the number this mode does not use does
        // not travel with the one it does.
        Assert.Equal(
            new SubtitleDepthSettings(true, SubtitleDepthMode.Plane, 0, 6),
            WrapperSettingsFile.Read(PluginSettingsTarget(paths)));
    }

    [Fact]
    public void ASwitchedOffRequestIsHandedOverAsSwitchedOff()
    {
        var paths = new FakeApplicationPaths(PrivateRoot());
        var plugin = new Plugin(paths, new RecordingXmlSerializer());

        plugin.UpdateConfiguration(new PluginConfiguration
        {
            SubtitleDepthEnabled = false,
            SubtitleDepthMode = SubtitleDepthMode.ConstantShift,
            SubtitleDepthShift = 30
        });

        // Unticking the box has to reach the wrapper, and it has to reach it as "off": numbers left
        // in the boxes are kept for the next time the mode is picked, not sent out as a request.
        Assert.Equal(
            SubtitleDepthSettings.Disabled,
            WrapperSettingsFile.Read(PluginSettingsTarget(paths)));
    }

    [Fact]
    public void AWrapperSettingsFileThatCannotBeWrittenDoesNotStopThePlugin()
    {
        // A deployment that pointed the settings document at a location it cannot write - a read
        // only mount, a path that is a file, a directory that cannot be created - loses the depth
        // setting and nothing else. The plugin still loads, the save the server asked for still
        // happens, and the version pass is still requested.
        var root = PrivateRoot();
        var paths = new FakeApplicationPaths(root);
        var target = PluginSettingsTarget(paths);

        // A directory standing in the document's place fails the wrapper write while leaving the
        // server's own settings save path free to behave normally.
        Directory.CreateDirectory(target);

        var trigger = new RecordingTrigger();
        Plugin? plugin = null;
        var loadFailure = Record.Exception(() => plugin = new Plugin(paths, new RecordingXmlSerializer(), trigger));

        Assert.Null(loadFailure);

        Assert.Null(Record.Exception(() => plugin!.UpdateConfiguration(new PluginConfiguration
        {
            SubtitleDepthEnabled = true,
            SubtitleDepthMode = SubtitleDepthMode.Automatic
        })));

        Assert.Equal(1, trigger.FullPassRequests);
    }

    private static Plugin CreatePlugin()
        => new(new FakeApplicationPaths(), new FakeXmlSerializer());

    /// <summary>
    /// Where a plugin built over these paths hands its settings to the wrapper, resolved through the
    /// same helper the plugin uses - including the deployment variable, so a test never asserts
    /// about a file the production code would not have written.
    /// </summary>
    private static string PluginSettingsTarget(FakeApplicationPaths paths)
        => WrapperSettingsFile.ResolveWritePath(Environment.GetEnvironmentVariable, paths.PluginConfigurationsPath);

    /// <summary>
    /// A directory of this test's own, so that the documents it watches are the ones it wrote: the
    /// default application paths are shared by every test that builds a plugin, and those run in
    /// parallel with each other.
    /// </summary>
    private string PrivateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "anaglyfin-plugin-tests", Guid.NewGuid().ToString("N"));

        _roots.Add(root);

        return root;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        _roots.Clear();
    }

    /// <summary>
    /// Records the reconcile requests the settings-save path makes.
    /// </summary>
    private sealed class RecordingTrigger : IProfileVersionReconcileTrigger
    {
        public List<Guid> ItemRequests { get; } = new();

        public int FullPassRequests { get; private set; }

        public void RequestItem(Guid itemId) => ItemRequests.Add(itemId);

        public void RequestFullPass() => FullPassRequests++;
    }

    /// <summary>
    /// The serializer the plugin base class insists on, with the writes this test performs going
    /// nowhere. A read still fails, which is how the base class decides to write defaults.
    /// </summary>
    private sealed class RecordingXmlSerializer : IXmlSerializer
    {
        public int SaveCount { get; private set; }

        public object DeserializeFromBytes(Type type, byte[] buffer)
            => throw new NotSupportedException("no settings file in this test");

        public object DeserializeFromFile(Type type, string file)
            => throw new NotSupportedException("no settings file in this test");

        public object DeserializeFromStream(Type type, Stream stream)
            => throw new NotSupportedException("no settings file in this test");

        public void SerializeToFile(object obj, string file) => SaveCount++;

        public void SerializeToStream(object obj, Stream stream) => SaveCount++;
    }
}

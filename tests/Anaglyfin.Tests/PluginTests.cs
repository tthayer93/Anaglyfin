using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Anaglyfin.Configuration;
using Anaglyfin.FFmpegWrapper;
using Anaglyfin.MediaSources;
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
    public void AFlatRequestIsHandedOverAsSwitchedOff()
    {
        var paths = new FakeApplicationPaths(PrivateRoot());
        var plugin = new Plugin(paths, new RecordingXmlSerializer());

        plugin.UpdateConfiguration(new PluginConfiguration
        {
            SubtitleDepthMode = SubtitleDepthMode.Flat,
            SubtitleDepthShift = 30
        });

        // The one off position of the dropdown has to reach the wrapper as "off": numbers left in the
        // boxes are kept for the next time a mode that carries them is picked, not sent out as a
        // request.
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
            SubtitleDepthMode = SubtitleDepthMode.Automatic
        })));

        Assert.Equal(1, trigger.FullPassRequests);
    }

    [Fact]
    public void ALegacySwitchedOffDepthBecomesFlatAndLosesTheSwitchFromTheFile()
    {
        // What an upgrading install has on disk when the feature was switched off: the retired
        // element next to a stored mode. Left alone the element would be dropped on read and the
        // mode would default to Automatic, silently turning depth on for somebody who turned it
        // off - so the constructor migrates it to Flat and rewrites the file without the element.
        var paths = new FakeApplicationPaths(PrivateRoot());
        var settingsPath = WritePluginConfigurationFile(paths, """
            <PluginConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <DefaultProfileId>sbs_half</DefaultProfileId>
              <SubtitleDepthEnabled>false</SubtitleDepthEnabled>
              <SubtitleDepthMode>Automatic</SubtitleDepthMode>
            </PluginConfiguration>
            """);

        var plugin = new Plugin(paths, new ServerLikeXmlSerializer());

        // The live settings carry the migration's answer, and a surviving setting rode through it.
        Assert.Equal(SubtitleDepthMode.Flat, plugin.Configuration.SubtitleDepthMode);
        Assert.Equal(ProfileIds.SideBySideHalf, plugin.Configuration.DefaultProfileId);

        // The file was rewritten through the settings path, so the retired element is gone and the
        // next load reads a clean current-schema file rather than deciding all over again.
        var rewritten = File.ReadAllText(settingsPath);
        Assert.DoesNotContain("SubtitleDepthEnabled", rewritten, StringComparison.Ordinal);
        Assert.Contains("<SubtitleDepthMode>Flat</SubtitleDepthMode>", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void ALegacySwitchedOnDepthKeepsTheModeItWasRunning()
    {
        // The switch was on and named Plane. The migration keeps the running mode and only drops
        // the retired element; it does not flatten somebody's working subtitle depth.
        var paths = new FakeApplicationPaths(PrivateRoot());
        var settingsPath = WritePluginConfigurationFile(paths, """
            <PluginConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <SubtitleDepthEnabled>true</SubtitleDepthEnabled>
              <SubtitleDepthMode>Plane</SubtitleDepthMode>
              <SubtitleDepthPlane>7</SubtitleDepthPlane>
            </PluginConfiguration>
            """);

        var plugin = new Plugin(paths, new ServerLikeXmlSerializer());

        Assert.Equal(SubtitleDepthMode.Plane, plugin.Configuration.SubtitleDepthMode);
        Assert.Equal(7, plugin.Configuration.SubtitleDepthPlane);

        var rewritten = File.ReadAllText(settingsPath);
        Assert.DoesNotContain("SubtitleDepthEnabled", rewritten, StringComparison.Ordinal);
        Assert.Contains("<SubtitleDepthMode>Plane</SubtitleDepthMode>", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void AFreshInstallationKeepsTheShippedDefaultAndWritesNothing()
    {
        // No file at all is a fresh install, not a legacy one. The constructor must neither read a
        // non-existent settings file nor write one: the shipped default (Automatic) is the right
        // answer, and touching the settings here would make construction create the file.
        var paths = new FakeApplicationPaths(PrivateRoot());
        var settingsPath = PluginConfigurationFile(paths);

        var plugin = new Plugin(paths, new ServerLikeXmlSerializer());

        // Construction wrote nothing - the migration saw no file and left it that way. (The settings
        // file appearing after the line below is the base class' lazy default on first read, not
        // this method; the assertion order is the whole of the distinction.)
        Assert.False(File.Exists(settingsPath));
        Assert.Equal(SubtitleDepthMode.Automatic, plugin.Configuration.SubtitleDepthMode);
    }

    [Fact]
    public void AFileThisBuildAlreadyWroteIsLeftAloneOnLaterBoots()
    {
        // A current-schema file - a mode, no legacy switch - is what this build writes on every
        // save. The migration must decide nothing about it and rewrite nothing, or it would churn
        // the settings file on every boot instead of being the one-time step it is.
        var paths = new FakeApplicationPaths(PrivateRoot());
        var settingsPath = WritePluginConfigurationFile(paths, """
            <PluginConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <SubtitleDepthMode>Plane</SubtitleDepthMode>
              <SubtitleDepthPlane>7</SubtitleDepthPlane>
            </PluginConfiguration>
            """);

        var asWritten = File.ReadAllText(settingsPath);

        // First boot over the migrated file: the stored mode is honoured, the file is untouched.
        var plugin = new Plugin(paths, new ServerLikeXmlSerializer());
        Assert.Equal(SubtitleDepthMode.Plane, plugin.Configuration.SubtitleDepthMode);
        Assert.Equal(asWritten, File.ReadAllText(settingsPath));

        // A second boot is the same constructor over the same file - and it still rewrites nothing.
        new Plugin(paths, new ServerLikeXmlSerializer());
        Assert.Equal(asWritten, File.ReadAllText(settingsPath));
    }

    [Fact]
    public void AnUnwritableSettingsFileDoesNotStopThePluginFromLoading()
    {
        // A settings file that can be read but not rewritten - a read-only mount, a locked file, a
        // full volume - is a deployment condition, not a reason the plugin cannot load. The
        // migration's write is best effort; the constructor has to come back with a working plugin.
        var paths = new FakeApplicationPaths(PrivateRoot());
        var settingsPath = WritePluginConfigurationFile(paths, """
            <PluginConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <SubtitleDepthEnabled>false</SubtitleDepthEnabled>
              <SubtitleDepthMode>Automatic</SubtitleDepthMode>
            </PluginConfiguration>
            """);

        // The read and the decision succeed; only the base settings write the migration attempts
        // next meets an unwritable file. (Where the file system lets a privileged process write
        // anyway, the write simply lands and the assertions below hold just the same.)
        File.SetAttributes(settingsPath, FileAttributes.ReadOnly);

        Plugin? plugin = null;
        try
        {
            var loadFailure = Record.Exception(() => plugin = new Plugin(paths, new ServerLikeXmlSerializer()));

            Assert.Null(loadFailure);
            Assert.NotNull(plugin);

            // The read and the decision still ran, so the live settings carry the migrated mode even
            // though the write may not have landed - which is what the wrapper hand-off runs on.
            Assert.Equal(SubtitleDepthMode.Flat, plugin!.Configuration.SubtitleDepthMode);
        }
        finally
        {
            File.SetAttributes(settingsPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task TheWrapperHandOffStillRunsOnTheMigratedSettings()
    {
        // The migration uses the non-publishing base save path on purpose, so the wrapper document
        // is written not by the constructor but by the startup hand-off that reads the live settings
        // afterwards. End to end, an upgrade that had depth switched off must hand the wrapper "off"
        // (Flat), never a depth nobody asked for.
        var paths = new FakeApplicationPaths(PrivateRoot());
        WritePluginConfigurationFile(paths, """
            <PluginConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <SubtitleDepthEnabled>false</SubtitleDepthEnabled>
              <SubtitleDepthMode>Automatic</SubtitleDepthMode>
            </PluginConfiguration>
            """);

        var plugin = new Plugin(paths, new ServerLikeXmlSerializer());

        // The container hands the hand-off the live settings by reference, exactly as
        // PluginConfigurationSource does, and the migration edited that same live object.
        var handoff = new WrapperSettingsPublicationService(new LiveSettingsSource(plugin.Configuration), paths);
        await handoff.StartAsync(CancellationToken.None);

        Assert.Equal(SubtitleDepthSettings.Disabled, WrapperSettingsFile.Read(PluginSettingsTarget(paths)));
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
    /// Where the plugin keeps its own settings file, resolved the way the plugin base class resolves
    /// it (settings directory plus the assembly name with an <c>.xml</c> extension), so a test places
    /// a settings file exactly where the constructor will go looking for one.
    /// </summary>
    private static string PluginConfigurationFile(FakeApplicationPaths paths)
        => Path.Combine(
            paths.PluginConfigurationsPath,
            Path.ChangeExtension(Path.GetFileName(typeof(Plugin).Assembly.Location), ".xml"));

    /// <summary>
    /// Places a settings file for a plugin built over these paths to find on load, in a directory of
    /// the caller's own, and returns where it put it.
    /// </summary>
    private static string WritePluginConfigurationFile(FakeApplicationPaths paths, string xml)
    {
        var path = PluginConfigurationFile(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, xml);

        return path;
    }

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

    /// <summary>
    /// The serializer the server actually runs - <see cref="XmlSerializer"/> against a real file - so
    /// a test can leave a settings file on disk, build the plugin over it, and read back exactly the
    /// file the plugin wrote. Unlike <see cref="RecordingXmlSerializer"/>, which is a no-write stub,
    /// this one round-trips, which is what makes the load-time migration observable end to end.
    /// </summary>
    private sealed class ServerLikeXmlSerializer : IXmlSerializer
    {
        public object DeserializeFromBytes(Type type, byte[] buffer)
        {
            using var stream = new MemoryStream(buffer, writable: false);
            return DeserializeFromStream(type, stream);
        }

        // A missing file opens as a failure, which is exactly how the base class is told to fall
        // back to writing its defaults.
        public object DeserializeFromFile(Type type, string file)
        {
            using var stream = File.OpenRead(file);
            return DeserializeFromStream(type, stream);
        }

        public object DeserializeFromStream(Type type, Stream stream)
            => new XmlSerializer(type).Deserialize(stream)!;

        public void SerializeToFile(object obj, string file)
        {
            using var stream = new FileStream(file, FileMode.Create, FileAccess.Write);
            SerializeToStream(obj, stream);
        }

        public void SerializeToStream(object obj, Stream stream)
            => new XmlSerializer(obj.GetType()).Serialize(stream, obj);
    }

    /// <summary>
    /// A settings source over one live settings object, the way the container hands the plugin's
    /// running settings to the startup hand-off (see <c>PluginConfigurationSource</c>, which returns
    /// the live object by reference so a migration's edit is what gets published).
    /// </summary>
    private sealed class LiveSettingsSource : IAnaglyfinConfigurationSource
    {
        private readonly PluginConfiguration _configuration;

        public LiveSettingsSource(PluginConfiguration configuration)
            => _configuration = configuration;

        public PluginConfiguration GetConfiguration() => _configuration;
    }
}

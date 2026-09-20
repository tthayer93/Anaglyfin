using System;
using System.Collections.Generic;
using System.IO;
using Anaglyfin.Configuration;
using Anaglyfin.FFmpegWrapper;
using Anaglyfin.VersionItems;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Anaglyfin;

/// <summary>
/// The Anaglyfin plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The fixed identity GUID of the plugin.
    /// </summary>
    /// <remarks>
    /// Jellyfin keys plugin identity, configuration and package updates on this
    /// value, so it must never change once a build has been published. It is
    /// duplicated in <c>Plugin.manifest.xml</c> (the packaging manifest) and a
    /// unit test asserts the two stay in sync.
    /// </remarks>
    public static readonly Guid PluginId = Guid.Parse("c7f4a1d9-3b58-4e2a-9d6c-84f0b1e5a723");

    /// <summary>
    /// The server's paths, kept for the settings-save path.
    /// </summary>
    /// <remarks>
    /// Nullable because a base-class settings path can call <see cref="SaveConfiguration"/> before
    /// this derived constructor has assigned its fields. At that moment the plugin has no path to
    /// write the wrapper document through, and a write attempted that early would only race the one
    /// startup publication and later save paths perform.
    /// </remarks>
    private readonly IApplicationPaths? _applicationPaths;

    private readonly IProfileVersionReconcileTrigger? _versionReconcileTrigger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="versionReconcileTrigger">
    /// The seam the settings-save path uses to ask for a version-item pass. Optional by contract: a
    /// plugin that cannot reach its own queue is still a plugin that loads, and a version pass that
    /// never starts costs a user nothing but a version list filled in at playback time instead.
    /// </param>
    /// <remarks>
    /// The constructor deliberately does not read <see cref="BasePlugin{PluginConfiguration}.Configuration"/>
    /// for a settings file that does not exist yet: the settings are loaded lazily, and touching them
    /// when there is nothing to read would make plugin construction write a default settings file. The
    /// wrapper's settings are handed over after construction by <see cref="WrapperSettingsPublicationService"/>,
    /// and again whenever a save reaches <see cref="SaveConfiguration"/>.
    /// <para>
    /// The one thing the constructor does reach for is a settings file that is already on disk and
    /// was written before the subtitle-depth switch was retired - see <see cref="MigrateSubtitleDepth"/>.
    /// Reading such a file is not the write-the-defaults case the paragraph above guards against: the
    /// file is there, and its contents are exactly what must not be lost by being read as a default.
    /// </para>
    /// </remarks>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IProfileVersionReconcileTrigger? versionReconcileTrigger = null)
        : base(applicationPaths, xmlSerializer)
    {
        _applicationPaths = applicationPaths;
        _versionReconcileTrigger = versionReconcileTrigger;

        MigrateSubtitleDepth();
    }

    /// <summary>
    /// Retires the subtitle-depth switch on a settings file that still carries it, once, on load.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The old switch and its mode cannot both survive into the new one-dropdown model: a stored file
    /// that says the feature was switched <i>off</i> would otherwise read back as the new shipped
    /// default, <c>Automatic</c>, and an upgrade would start moving captions nobody asked it to. Only
    /// the stored <i>file</i> still records that switch - the settings model has no field left for it -
    /// so the migration reads the file itself and lets <see cref="PluginConfigurationMigration"/>
    /// decide the mode the model should hold.
    /// </para>
    /// <para>
    /// A file that does not exist is a fresh installation and is left entirely alone: it has no switch
    /// to retire and its shipped default (Automatic) is the right answer for it, so nothing is read and
    /// nothing is written. A file this build already wrote carries a mode and no switch, the migration
    /// decides nothing about it, and nothing is written - which is what keeps this a one-time step
    /// rather than a rewrite on every load.
    /// </para>
    /// <para>
    /// Only when the file states a legacy switch, or predates the feature, is the decision applied to
    /// the live settings and written back through the base settings path: writing is what drops the
    /// now-retired element so the next load reads a clean file. A subtitle-depth change moves no
    /// profile, so it asks for no version pass and needs no wrapper hand-off of its own - startup
    /// publishes the wrapper document from the live settings a moment later, reading the migrated
    /// value this wrote.
    /// </para>
    /// <para>
    /// Every disk step is taken as best effort, in the same failure-is-the-answer shape the wrapper
    /// hand-off uses, so that an unwritable settings file - a read-only mount, a locked file, a full
    /// volume - costs an administrator a migration that does not persist and never a plugin that
    /// cannot load: the constructor has no business failing startup over a subtitle-depth change.
    /// The live settings still carry the migrated mode whenever the read and the decision succeeded,
    /// so the wrapper sees it this run, and a decision that did not reach disk is simply re-attempted
    /// on the next load. Where the read itself failed, the settings load is the authority on what an
    /// unreadable file means, and this method does not second-guess it.
    /// </para>
    /// </remarks>
    private void MigrateSubtitleDepth()
    {
        var path = ConfigurationFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            // A fresh installation has no stored switch to retire and nothing to read, so there is
            // nothing to write. Reading the live settings to check that would be exactly the
            // default-file write the constructor comment guards against, so the shipped default
            // (Automatic) is left to speak for itself.
            return;
        }

        // Read, decide, apply and persist are one best-effort step: any of them can meet an unwritable
        // or unreadable file (the read and the write both touch the disk), and none of them is worth
        // throwing out of a plugin constructor. Swallowing a deployment-shaped IO failure degrades to
        // "no migration", which is the honest answer and the one that keeps the plugin loading.
        try
        {
            var storedXml = File.ReadAllText(path);

            var migratedMode = PluginConfigurationMigration.ResolveStoredSubtitleDepthMode(storedXml);
            if (migratedMode is null)
            {
                // A file this build already wrote states a mode and carries no legacy switch: the
                // migration decides nothing about it and writes nothing, which is what keeps this a
                // one-time step rather than a rewrite on every boot.
                return;
            }

            var configuration = Configuration;
            configuration.SubtitleDepthMode = migratedMode.Value;

            // The base save path, not the overriding one: the migration changes no enabled profile, so
            // there is no version pass to ask for, and the wrapper document is published from the live
            // settings at startup. Writing the file is the whole of what this step owes the upgrade.
            base.SaveConfiguration(configuration);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            // Nothing to do: a migration this could not carry out leaves the live settings holding
            // whatever the read produced and is retried next load, exactly as the remarks describe.
        }
    }

    /// <inheritdoc />
    public override string Name => "Anaglyfin";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description
        => "Exposes 3D MVC sources to Jellyfin as selectable playback versions.";

    /// <inheritdoc />
    /// <remarks>
    /// One page: the admin settings page. The dashboard is told where to find it
    /// (<see cref="ConfigurationPage.HtmlResourceName"/>) and serves it unchanged, so the
    /// page reads and writes the settings through the server's plugin settings endpoint
    /// rather than through anything this assembly exposes.
    /// </remarks>
    public IEnumerable<PluginPageInfo> GetPages()
        => new[] { ConfigurationPage.CreatePageInfo() };

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The hook that makes the settings take effect on the library and not only on the next
    /// playback request. Which profiles are enabled decides which version items a library should
    /// hold: an administrator who enables "3D Half Side-by-Side" expects it in the versions
    /// selector of every MVC title, and one who disables it expects the stale item to be gone.
    /// </para>
    /// <para>
    /// Saving asks for one pass over the library rather than running one, whatever changed. The
    /// settings are what the pass reads when it gets there, so the save path carries no version
    /// decision of its own and cannot disagree with it - and an administrator who saves the page
    /// six times in a row costs one pass, because the requests coalesce.
    /// </para>
    /// <para>
    /// The same save also hands the settings to the wrapper, which cannot read them for itself;
    /// see <see cref="PublishWrapperSettings"/>. Doing it here rather than in a settings-changed
    /// event of its own is what makes the file and the stored settings move together: one write
    /// the server already ordered, one document describing it. Startup reaches the same helper from
    /// <see cref="WrapperSettingsPublicationService"/>, which runs after the plugin and its settings
    /// seam exist.
    /// </para>
    /// </remarks>
    public override void SaveConfiguration(PluginConfiguration config)
    {
        base.SaveConfiguration(config);

        PublishWrapperSettings(config);

        _versionReconcileTrigger?.RequestFullPass();
    }

    /// <summary>
    /// Writes the settings the FFmpeg wrapper is allowed to know.
    /// </summary>
    /// <param name="configuration">
    /// The settings being published; <c>null</c> is read as "not loaded yet" and published as the
    /// shipped defaults, which is the honest reading of a plugin whose settings file has not been
    /// read - and the same answer a fresh installation gives.
    /// </param>
    /// <remarks>
    /// <para>
    /// The wrapper is started by the server rather than by this plugin, so it runs outside the
    /// plugin's process and outside its container, and the environment it inherits was written by
    /// the deployment rather than by the administrator editing this page. The document
    /// <see cref="WrapperSettingsFile"/> writes is the one channel that reaches it, and it carries
    /// the two settings that page owns which have no other route - the subtitle depth request and
    /// the concurrency limit - and nothing else: not the profile list, not the colours, and no
    /// credential, because the settings hold none and the wrapper has no use for any of them.
    /// </para>
    /// <para>
    /// Both values are taken through the settings read side rather than as stored, so the wrapper is
    /// handed the number the plugin would itself act on. The deployment can still name a limit of
    /// its own, and when it does that one wins: the count of processes a server may start is
    /// ultimately the deployment's decision, and this write is the administrator's answer to the
    /// same question when the deployment has no opinion.
    /// </para>
    /// <para>
    /// <b>A failed write is not a failed save.</b> An unwritable directory, a full disk or a file
    /// another process holds open costs the deployment the settings that then do not take effect -
    /// the wrapper plays the flat way it played yesterday and counts the slots it counted yesterday -
    /// rather than a plugin that cannot load or a settings page that reports an error for a change
    /// the server did store. The administrator who wonders why a request did not arrive finds the
    /// answer in the file's absence, and the variable that names it is documented in the install
    /// guide.
    /// </para>
    /// </remarks>
    private void PublishWrapperSettings(PluginConfiguration? configuration)
    {
        // Nothing to write to until the constructor has the server's paths; see the field.
        if (_applicationPaths is null)
        {
            return;
        }

        var stored = configuration ?? new PluginConfiguration();

        var path = WrapperSettingsFile.ResolveWritePath(
            Environment.GetEnvironmentVariable,
            _applicationPaths.PluginConfigurationsPath);

        WrapperSettingsFile.TryWrite(
            path,
            stored.GetEffectiveSubtitleDepth(),
            stored.GetEffectiveMaxConcurrentTranscodes(),
            out _);
    }
}

using System;
using System.Collections.Generic;
using Anaglyfin.Configuration;
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

    private readonly IProfileVersionReconcileTrigger? _versionReconcileTrigger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="versionReconcileTrigger">
    /// The seam the settings-save path uses to ask for a version-item pass. Optional by contract:
    /// the base constructor saves the settings file (it writes a default one when there is none)
    /// before this field can be assigned, and a plugin whose first save crashed would never load.
    /// </param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IProfileVersionReconcileTrigger? versionReconcileTrigger = null)
        : base(applicationPaths, xmlSerializer)
    {
        _versionReconcileTrigger = versionReconcileTrigger;
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
    /// </remarks>
    public override void SaveConfiguration(PluginConfiguration config)
    {
        base.SaveConfiguration(config);

        _versionReconcileTrigger?.RequestFullPass();
    }
}

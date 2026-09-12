using System;
using Anaglyfin.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Anaglyfin;

/// <summary>
/// The Anaglyfin plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
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
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    /// <inheritdoc />
    public override string Name => "Anaglyfin";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description
        => "Exposes 3D MVC sources to Jellyfin as selectable playback versions.";
}

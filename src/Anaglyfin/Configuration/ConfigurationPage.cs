using MediaBrowser.Model.Plugins;

namespace Anaglyfin.Configuration;

/// <summary>
/// What the Jellyfin dashboard needs to know about the Anaglyfin admin page.
/// </summary>
/// <remarks>
/// <para>
/// A v12 server hands a plugin's pages to the dashboard through
/// <see cref="IHasWebPages"/>: each page is a <see cref="PluginPageInfo"/> that names an
/// embedded resource, and the server serves that resource verbatim. There is no page
/// model left to fill in and no page specific endpoint to add: the markup talks to the
/// ordinary plugin settings endpoint from the browser, which is why everything this page
/// can change is a property of <see cref="PluginConfiguration"/> and nothing else is.
/// </para>
/// <para>
/// The resource name is spelled out here instead of being derived from the file path so
/// that the build (which embeds the file), the page (which is served under this name)
/// and the tests (which read it back by name) all name one thing explicitly.
/// </para>
/// </remarks>
public static class ConfigurationPage
{
    /// <summary>
    /// The page name the dashboard uses to reach the page. Following the plugin template,
    /// this is the plugin's own name: a plugin with one page does not need a second
    /// identifier for it, and the name is what ends up in the dashboard link.
    /// </summary>
    public const string PageName = "Anaglyfin";

    /// <summary>The label the dashboard shows for the page.</summary>
    public const string DisplayName = "Anaglyfin";

    /// <summary>
    /// The embedded resource holding the page markup, served as is.
    /// </summary>
    /// <remarks>
    /// One resource per page: the CSS and the JS are part of the page document rather than
    /// separate requests, because the page channel serves exactly the resource a
    /// <see cref="PluginPageInfo"/> names.
    /// </remarks>
    public const string HtmlResourceName = "Anaglyfin.Configuration.configPage.html";

    /// <summary>
    /// Builds the page description the dashboard is asked for.
    /// </summary>
    /// <returns>The single page Anaglyfin contributes to the dashboard.</returns>
    /// <remarks>
    /// <see cref="PluginPageInfo.EnableInMainMenu"/> is deliberately left off: the page is
    /// reached from the plugin's own entry in the dashboard, which is where an
    /// administrator looks for a plugin's settings, and it does not need a second home in
    /// the server's main menu.
    /// </remarks>
    public static PluginPageInfo CreatePageInfo()
        => new()
        {
            Name = PageName,
            DisplayName = DisplayName,
            EmbeddedResourcePath = HtmlResourceName
        };
}

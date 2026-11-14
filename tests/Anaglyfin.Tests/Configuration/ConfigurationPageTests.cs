using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Anaglyfin.Configuration;
using Anaglyfin.FFmpegWrapper;
using Anaglyfin.Profiles;
using Anaglyfin.RuntimeStatus;
using Anaglyfin.Tests.Stubs;
using MediaBrowser.Model.Plugins;
using Xunit;

namespace Anaglyfin.Tests.Configuration;

/// <summary>
/// Verifies the admin page the dashboard serves: that it is present and wired up, that it
/// names the same profiles, settings and defaults the plugin does, and that it stays a
/// picker of allowlisted choices rather than a text box for conversion details.
/// </summary>
/// <remarks>
/// <para>
/// The page is one embedded HTML document with its stylesheet and script inline, because a
/// v12 dashboard page is exactly the resource a <see cref="PluginPageInfo"/> names. These
/// tests therefore read that document back out of the plugin assembly and compare it
/// against the model it is the only interface to: a profile added to the catalog or a setting
/// added to the model fails here instead of silently disappearing from, or silently appearing
/// on, the UI. What the page must never carry again - the names a deployment is arranged with -
/// is pinned the same way, because the only thing between that list and this page is a test.
/// </para>
/// <para>
/// What no test here can prove is the browser half: that the dashboard raises the event the
/// page loads on, that the web client exposes the plugin settings under the calls the page
/// makes, and that a page asking for a place in the settings menu is listed there once the
/// dashboard has it. Those are recorded on the page itself and need a real server.
/// </para>
/// </remarks>
public class ConfigurationPageTests
{
    /// <summary>
    /// Words an editable element may never be named after: the page picks profiles, colours
    /// and numbers, it does not accept a conversion description or a filesystem location.
    /// </summary>
    private static readonly string[] ForbiddenAttributeText =
    [
        "filter",
        "ffmpeg",
        "path",
        "args",
        "command",
        "binary",
        "shell",
        "executable"
    ];

    /// <summary>
    /// Spellings the settings half of the page may not carry at all: the names a deployment is
    /// arranged with. None of them is editable from this page, so naming one invites an edit that
    /// cannot be made - and each belongs to the install guide, where the person setting it up
    /// actually is.
    /// </summary>
    /// <remarks>
    /// The runtime status section is the one owner-approved exception, and it is excused by
    /// marker rather than by loosening this list: what the list forbids everywhere else, it still
    /// forbids inside the section too unless the name appears on
    /// <see cref="RuntimeStatusReportedVariables"/>, which is the set the status endpoint itself
    /// reports.
    /// </remarks>
    private static readonly string[] ForbiddenPageText =
    [
        "ANAGLYFIN_REAL_FFMPEG",
        "FFMPEG_MVC_PATH",
        "ANAGLYFIN_LOCK_DIR",
        "ANAGLYFIN_WRAPPER_SETTINGS",
        "ANAGLYFIN_MAX_CONCURRENT_TRANSCODES",
        "JELLYFIN_FFMPEG",
        "FFMPEG_PATH",
        "ANAGLYFIN_"
    ];

    /// <summary>
    /// The one part of the page that names the deployment on purpose, written between these two
    /// markers so the guard above can excuse exactly it - and only it - rather than the whole
    /// document. The markup and the script each carry their own pair.
    /// </summary>
    private const string RuntimeStatusRegionStart = "anaglyfin-runtime-status:begin";

    private const string RuntimeStatusRegionEnd = "anaglyfin-runtime-status:end";

    /// <summary>
    /// The deployment names the runtime status section is allowed to carry: exactly the variables
    /// the status endpoint reports, taken from the code that reports them rather than from a copy
    /// of a document, so the page cannot advertise a variable nothing reads.
    /// </summary>
    private static readonly string[] RuntimeStatusReportedVariables =
    [
        RuntimeStatusService.JellyfinFFmpegEnvironmentVariable,
        FFmpegWrapperOptions.RealFFmpegEnvironmentVariable,
        FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable,
        TranscodeSlotStore.LockDirectoryEnvironmentVariable,
        WrapperSettingsFile.EnvironmentVariable
    ];

    /// <summary>The columns the binary table owes each reported binary, in the order it shows them.</summary>
    private static readonly string[] RuntimeStatusBinaryColumns =
    [
        "Status",
        "Required",
        "Label",
        "Path",
        "Version"
    ];

    /// <summary>The columns the variable table owes each reported variable, in the order it shows them.</summary>
    private static readonly string[] RuntimeStatusVariableColumns =
    [
        "Status",
        "Required",
        "Variable",
        "Value",
        "Resolved path"
    ];

    /// <summary>
    /// The five column classes both tables name on their own <c>colgroup</c>, in order. The two
    /// tables are one grid rather than two tables that happen to look alike, so the pair is only
    /// aligned while the same five names appear in the same order on both of them.
    /// </summary>
    private static readonly string[] RuntimeStatusColumnClasses =
    [
        "anaglyfin-runtimeStatusColStatus",
        "anaglyfin-runtimeStatusColRequired",
        "anaglyfin-runtimeStatusColName",
        "anaglyfin-runtimeStatusColPath",
        "anaglyfin-runtimeStatusColVersion"
    ];

    /// <summary>The three row colours the page owns, and the only three a generated row may ever carry.</summary>
    private const string RuntimeStatusRowOkClass = "anaglyfin-runtimeStatusRowOk";

    private const string RuntimeStatusRowWarningClass = "anaglyfin-runtimeStatusRowWarning";

    private const string RuntimeStatusRowErrorClass = "anaglyfin-runtimeStatusRowError";

    /// <summary>
    /// The three row states as the page draws them: the class a row carries and the colour it is
    /// drawn in, which is the same family the summary line uses.
    /// </summary>
    /// <remarks>
    /// Both halves are pinned because the dashboard brings its own sheet to this page, and that
    /// sheet paints <c>td</c> elements of its own. A colour declared on the <c>tr</c> only reaches a
    /// cell by inheritance, and inheritance loses to any declaration the dashboard makes on the cell
    /// - so the three colours have to be declared on the cells to be the colours an administrator
    /// sees, which is what <see cref="TheRuntimeStatusRowsAreColouredByThePageAndNotByTheEndpoint"/>
    /// pins. The band behind them is pinned too: it is what makes a row read as one answer rather
    /// than five coloured accidents.
    /// </remarks>
    private static readonly (string StateClass, string Colour)[] RuntimeStatusRowStates =
    [
        (RuntimeStatusRowOkClass, "#6dae5a"),
        (RuntimeStatusRowWarningClass, "#e2a400"),
        (RuntimeStatusRowErrorClass, "#cf4e52")
    ];

    /// <summary>
    /// The words the removed summary paragraph opened with, and the two sentences it closed with.
    /// The section is allowed one short line under its heading; what it is not allowed is to
    /// re-grow the paragraph that used to list every binary and every variable the endpoint might
    /// report, which is the paragraph that made this page a copy of the install guide.
    /// </summary>
    private static readonly string[] RemovedRuntimeStatusParagraph =
    [
        @"What\s+the\s+plugin\s+itself\s+can\s+see\s+of\s+this\s+installation",
        @"the\s+directory\s+the\s+service\s+account\s+has\s+to\s+be\s+able\s+to\s+write",
        @"This\s+section\s+reads\s+and\s+never\s+writes"
    ];

    /// <summary>The words the summary shows for the three overall states, in the endpoint's order.</summary>
    private static readonly string[] RuntimeStatusOverallTexts =
    [
        "Installation complete",
        "Needs attention",
        "Installation incomplete"
    ];

    /// <summary>The words a row shows for each item state, in the endpoint's order.</summary>
    private static readonly string[] RuntimeStatusItemTexts =
    [
        "OK",
        "Missing",
        "Not readable",
        "Not executable",
        "Not writable",
        "Probe failed",
        "Version mismatch",
        "Warning",
        "Unknown"
    ];

    /// <summary>
    /// Words no setting may be named after, because a setting is a choice the page can make and
    /// save: the runtime status is read, never stored, so nothing that belongs to it may arrive in
    /// the bound field set - which is what the settings endpoint would happily persist.
    /// </summary>
    private static readonly string[] ForbiddenBoundFieldNameText =
    [
        "runtime",
        "status",
        "state",
        "path",
        "environment",
        "variable",
        "binary",
        "wrapper",
        "ffmpeg"
    ];

    [Fact]
    public void ThePluginHandsTheAdminPageToTheDashboard()
    {
        var plugin = new Plugin(new FakeApplicationPaths(), new FakeXmlSerializer());

        var hasWebPages = Assert.IsAssignableFrom<IHasWebPages>(plugin);

        var page = Assert.Single(hasWebPages.GetPages());
        Assert.Equal(ConfigurationPage.PageName, page.Name);
        Assert.Equal(ConfigurationPage.DisplayName, page.DisplayName);
        Assert.Equal(ConfigurationPage.HtmlResourceName, page.EmbeddedResourcePath);

        // A v12 dashboard lists a plugin page in its own settings menu only when the page
        // asks to be listed, and this page is the only way to configure Anaglyfin. A build
        // that quietly drops the flag ships a server whose settings cannot be reached.
        Assert.True(page.EnableInMainMenu);
    }

    [Fact]
    public void TheAdminPageStillCarriesTheFormThatSavesIt()
    {
        // The menu entry is only worth having if the page it leads to can still write the
        // settings: the form, the control that submits it, and the handler that answers that
        // submission are one chain, and a page that lost any link of it opens read-only.
        var html = ReadPageHtml();

        var opened = html.IndexOf("<form id=\"AnaglyfinConfigForm\"", StringComparison.Ordinal);
        Assert.True(opened >= 0, "The admin page no longer carries the form the settings are saved from.");

        var closed = html.IndexOf("</form>", opened, StringComparison.Ordinal);
        Assert.True(closed > opened, "The admin settings form is never closed.");

        var form = html[opened..closed];

        Assert.Matches("<button[^>]*type=\"submit\"[^>]*>", form);
        Assert.Contains("<div id=\"AnaglyfinMessage\"", form, StringComparison.Ordinal);

        // And the submission is answered by the page itself, rather than by a browser POST
        // to an endpoint that does not take one.
        Assert.Contains("form.addEventListener('submit', saveConfiguration);", html, StringComparison.Ordinal);
        Assert.Contains("window.ApiClient.updatePluginConfiguration(", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdminPageSaysWhereItIsEditedWhenItWasOpenedOutsideTheDashboard()
    {
        var html = ReadPageHtml();

        // Opened directly rather than from the dashboard, this document has no signed-in
        // client to read the stored settings from and none to save them back through. It has
        // to say so - and name where the page is edited from - rather than presenting a form
        // whose only possible answer is a refusal.
        var pageOpened = html.IndexOf("<div id=\"AnaglyfinConfigPage\"", StringComparison.Ordinal);
        var notice = html.IndexOf("<div id=\"AnaglyfinDashboardNotice\" class=\"anaglyfin-readonly\" hidden>", StringComparison.Ordinal);
        var formOpened = html.IndexOf("<form id=\"AnaglyfinConfigForm\"", StringComparison.Ordinal);

        Assert.True(pageOpened >= 0, "The admin page no longer opens with its page element.");
        Assert.True(
            notice > pageOpened && notice < formOpened,
            "The page no longer warns above the form that cannot save without the dashboard.");

        // Naming the menu the page asks to be listed in is the point of the warning: whoever
        // reached a raw copy of the page needs the way back to the dashboard one.
        Assert.Contains("dashboard settings menu", html, StringComparison.Ordinal);
        Assert.Contains("Dashboard -> Plugins -> Anaglyfin", html, StringComparison.Ordinal);

        // The warning belongs to exactly the two places the page declines to act - it cannot
        // read and it cannot save - and appears nowhere else, because a page opened from the
        // dashboard has nothing to warn about.
        Assert.Equal(2, Regex.Matches(html, "revealDashboardNotice\\(\\);").Count);
        Assert.Matches(@"if \(!dashboardAvailable\(\)\) \{\s*configurationLoading = false;\s*revealDashboardNotice\(\);", html);

        // And declining is the whole of it: the page does not improvise a transport of its own
        // for the settings endpoint, because a copy of the page opened on its own has no
        // credential to write server settings with.
        Assert.DoesNotContain("fetch(", html, StringComparison.Ordinal);
        Assert.DoesNotContain("XMLHttpRequest", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdminPageMarkupTravelsInThePluginAssembly()
    {
        Assert.Contains(ConfigurationPage.HtmlResourceName, typeof(Plugin).Assembly.GetManifestResourceNames());

        var html = ReadPageHtml();

        Assert.Contains("<form id=\"AnaglyfinConfigForm\"", html, StringComparison.Ordinal);
        Assert.Contains("data-anaglyfin-field", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdminPageIsOneSelfContainedDocument()
    {
        var html = ReadPageHtml();

        // The dashboard serves this page and nothing beside it, so a stylesheet or script
        // reference would be a request for a file that is never going to arrive.
        Assert.DoesNotContain("<script src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<style>", html, StringComparison.Ordinal);
        Assert.Contains("<script type=\"text/javascript\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdminPageCarriesItsStylesheetInsideThePageElement()
    {
        var html = ReadPageHtml();

        // The dashboard fetches this document, parses it as a fragment and keeps only the
        // element carrying data-role="page". Anything beside that element - a stylesheet in
        // the head included - is parsed away before a browser is ever shown the page, so the
        // page has to carry its own stylesheet inside itself.
        var pageOpened = html.IndexOf("<div id=\"AnaglyfinConfigPage\"", StringComparison.Ordinal);
        Assert.True(pageOpened >= 0, "The admin page no longer opens with its page element.");

        var pageClosed = html.LastIndexOf("</div>", StringComparison.Ordinal);
        Assert.True(pageClosed > pageOpened, "The admin page element is never closed.");

        var stylesheet = html.IndexOf("<style>", StringComparison.Ordinal);
        Assert.True(
            stylesheet > pageOpened && stylesheet < pageClosed,
            "The stylesheet sits outside the page element, which is the only half of this document the dashboard keeps.");

        var headClosed = html.IndexOf("</head>", StringComparison.Ordinal);
        Assert.True(
            headClosed > 0 && stylesheet > headClosed,
            "The page still styles itself from the head, which the dashboard parses away.");
    }

    [Fact]
    public void TheAdminPageNamesThePluginItConfigures()
    {
        var match = Regex.Match(ReadPageHtml(), @"pluginId:\s*'(?<id>[^']+)'");

        Assert.True(match.Success, "The admin page no longer carries the plugin id it asks the settings endpoint for.");
        Assert.Equal(Plugin.PluginId, Guid.Parse(match.Groups["id"].Value));
    }

    [Fact]
    public void TheAdminPageOffersExactlyTheCatalogProfilesInCatalogOrder()
    {
        var expected = new ProfileCatalog().Profiles;

        var listed = ReadProfileList();

        Assert.Equal(expected.Count, listed.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Id, listed[i].Id);
            Assert.Equal(expected[i].DisplayName, listed[i].DisplayName);
        }
    }

    [Fact]
    public void TheAdminPageTicksTheProfilesTheShippedSettingsEnable()
    {
        var enabledByDefault = ReadProfileList()
            .Where(profile => profile.EnabledByDefault)
            .Select(profile => profile.Id)
            .ToArray();

        // One tick on the whole page, and it is the profile a fresh install offers. Named
        // before the comparison below so a page that ticks the wrong single profile is
        // reported as the wrong profile rather than as a set mismatch.
        Assert.Equal(ProfileIds.AnaglyphRedCyanDubois, Assert.Single(enabledByDefault));

        // Compared as a set: the page lists profiles in catalog order while the shipped set
        // is declared in preference order, and the profile catalog resolves either way to
        // the catalog order it offers clients in.
        Assert.Equal(
            ProfileIds.DefaultEnabledProfileIds.OrderBy(id => id, StringComparer.Ordinal),
            enabledByDefault.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void TheAdminPageListsTheSideBySideAndTwoDProfilesUnTicked()
    {
        // The three profiles an earlier build shipped enabled are listed unticked now: a
        // fresh install shows one ticked version, and an administrator who wants full SBS,
        // half SBS or the plain base view switches them on here.
        var listed = ReadProfileList();

        foreach (var profileId in new[] { ProfileIds.SideBySideFull, ProfileIds.SideBySideHalf, ProfileIds.TwoDBase })
        {
            var profile = listed.Single(entry => entry.Id == profileId);

            Assert.False(profile.EnabledByDefault);
        }
    }

    [Fact]
    public void TheAdminPageListsTheTopAndBottomPairUnTicked()
    {
        // The over-under pair joined the catalog without joining the shipped enabled set,
        // and the page has to say exactly that: both listed, both unticked, so a fresh
        // install still shows the one enabled version it showed before the pair existed
        // while an administrator can still switch the new ones on.
        var listed = ReadProfileList();

        var full = listed.Single(profile => profile.Id == ProfileIds.TopAndBottomFull);
        var half = listed.Single(profile => profile.Id == ProfileIds.TopAndBottomHalf);

        Assert.False(full.EnabledByDefault);
        Assert.False(half.EnabledByDefault);

        // Beside their side-by-side siblings, in catalog order.
        var ids = listed.Select(profile => profile.Id).ToArray();
        Assert.Equal(Array.IndexOf(ids, ProfileIds.SideBySideFull) + 1, Array.IndexOf(ids, ProfileIds.TopAndBottomFull));
        Assert.Equal(Array.IndexOf(ids, ProfileIds.SideBySideHalf) - 1, Array.IndexOf(ids, ProfileIds.TopAndBottomHalf));
    }

    [Fact]
    public void TheAdminPageBindsEverySettingAndNothingElse()
    {
        var bound = ReadBoundFieldNames();

        Assert.Equal(
            SettingsPropertyNames().OrderBy(name => name, StringComparer.Ordinal),
            bound.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void TheAdminPageDisplaysTheDefaultsTheSettingsModelShips()
    {
        var configuration = new PluginConfiguration();

        var displayed = ReadFieldAttributes("data-anaglyfin-default").ToDictionary(field => field.Key, field => field.Value);

        // Every choice the page offers has to say what the model stores when nobody has
        // configured anything, or the page would present a decision as a saved setting.
        Assert.Contains(nameof(PluginConfiguration.DefaultProfileId), displayed.Keys);
        Assert.Contains(nameof(PluginConfiguration.MaxConcurrentTranscodes), displayed.Keys);

        foreach (var field in displayed)
        {
            var property = SettingsProperty(field.Key);
            var stored = property.GetValue(configuration)
                ?? throw new InvalidOperationException($"The default of {nameof(PluginConfiguration)}.{field.Key} is null.");

            // Text is the only form both sides can be compared in: enum names rather than
            // ordinals, and the canonical spelling of a colour.
            var expected = stored is Enum enumeration
                ? enumeration.ToString()
                : Convert.ToString(stored, CultureInfo.InvariantCulture);

            Assert.Equal(expected, field.Value);
        }
    }

    [Fact]
    public void TheAdminPageCanSendEverySettingBack()
    {
        // The page sends one property per bound field, spelled the way the settings model spells
        // it, and the payload is read back with the settings endpoint's own case sensitive
        // matching. A tolerant reader here would accept the camelCase body the endpoint answers
        // with success and no binding at all, and the one mistake this contract exists to catch
        // is precisely that one.
        var payload = BuildPayloadThePageSends();

        var reloaded = JsonSerializer.Deserialize<PluginConfiguration>(
            payload,
            new JsonSerializerOptions
            {
                Converters = { new JsonStringEnumConverter() }
            });

        // Spelled the way the model names them is the whole of the contract: a payload carrying
        // the same settings under other names binds nothing and reports nothing.
        var sentNames = JsonNode.Parse(payload)!.AsObject().Select(pair => pair.Key).ToArray();
        Assert.Equal(
            ReadBoundFieldNames().OrderBy(name => name, StringComparer.Ordinal),
            sentNames.OrderBy(name => name, StringComparer.Ordinal));

        var configuration = Assert.IsAssignableFrom<PluginConfiguration>(reloaded);

        Assert.Equal("sbs_half", configuration.DefaultProfileId);
        Assert.Equal(3, configuration.MaxConcurrentTranscodes);
        Assert.Equal("#00FF00", configuration.CustomLeftEyeColor);
        Assert.Equal("#0000FF", configuration.CustomRightEyeColor);
        Assert.Equal(new[] { "sbs_full", "two_d_base" }, configuration.EnabledProfileIds);

        // The payload spells an enum with the last name the enumeration declares, which is the
        // last position of the one dropdown - Flat. A mode the endpoint could not bind would come
        // back as the shipped Automatic here and pass a weaker assertion.
        Assert.Equal(SubtitleDepthMode.Flat, configuration.SubtitleDepthMode);
        Assert.Equal(3, configuration.SubtitleDepthShift);
        Assert.Equal(3, configuration.SubtitleDepthPlane);

        // And the same check written against the field list instead of the individual
        // settings, so a setting added to the model cannot join the page half bound.
        foreach (var fieldName in ReadBoundFieldNames())
        {
            var property = SettingsProperty(fieldName);

            Assert.NotEqual(
                AsJson(property.GetValue(new PluginConfiguration())),
                AsJson(property.GetValue(configuration)));
        }
    }

    [Fact]
    public void TheAdminPageSavesInTheSpellingTheSettingsEndpointBinds()
    {
        // The settings endpoint reads a body by the settings model's own spelling and ignores
        // any other one, and it ignores it with a success code: an administrator who edited a
        // setting in the wrong spelling is told the save worked and reads back the shipped
        // default. Nothing but the spelling of the name tells the two saves apart, so the
        // spelling is pinned here rather than left to a browser to discover.
        var writeBody = FunctionBody(ReadPageHtml(), "writeProperty");

        // The value goes under the name the markup binds the field by, which the markup spells
        // as the settings model spells it.
        Assert.Matches(@"config\[propertyName\]\s*=\s*value;", writeBody);
        Assert.DoesNotContain("config[camelCase] = value", writeBody, StringComparison.Ordinal);

        // And the camelCase twin left in the same object by a read that asked for that spelling
        // is dropped rather than posted beside it: one setting spelled two ways in one body is a
        // save whose outcome belongs to whatever the endpoint happens to read first.
        Assert.Contains("delete config[camelCase];", writeBody, StringComparison.Ordinal);
        Assert.DoesNotContain("delete config[propertyName];", writeBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdminPageStillReadsAStoredSettingInEitherSpelling()
    {
        // Only the spelling this page writes is its own decision. The settings endpoint answers a
        // plain read PascalCase and a read made by a client that asked for the camelCase profile
        // in camelCase, and the page cannot choose which of the two the dashboard's client asks
        // for - so both spellings stay readable.
        var readBody = FunctionBody(ReadPageHtml(), "readProperty");

        Assert.Contains("hasOwnProperty.call(config, camelCase)", readBody, StringComparison.Ordinal);
        Assert.Contains("hasOwnProperty.call(config, propertyName)", readBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdminPageCarriesNoDeviceDefaultControlsEndpointsOrScript()
    {
        // Exact-device defaults never shipped, and the feature was taken out of the product
        // before release: the global default is the only default control this page has.
        // Nothing device-shaped may survive the removal in any layer the page can carry it
        // in - the markup, the stylesheet, the script, or a read of the server's device
        // registry - because a leftover control would advertise a matching tier the
        // settings model, the catalog and the provider have all dropped, and a save would
        // post it under a name the endpoint silently ignores.
        var html = ReadPageHtml();

        Assert.DoesNotContain("device", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AnaglyfinDeviceDefault", html, StringComparison.Ordinal);
        Assert.DoesNotContain("getUrl('/Devices'", html, StringComparison.Ordinal);
        Assert.DoesNotContain("deviceDefault", html, StringComparison.OrdinalIgnoreCase);

        // The free-text device id box was the page's only text input; with the feature
        // gone the page has no free text at all to re-grow a row template for.
        Assert.DoesNotContain("<template", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheEyeColourPickersStoreCanonicalHexTriplets()
    {
        var colours = ReadFieldAttributes("data-anaglyfin-default")
            .Where(field => IsColourField(field.Key))
            .ToDictionary(field => field.Key, field => field.Value);

        Assert.Equal(
            new[] { nameof(PluginConfiguration.CustomLeftEyeColor), nameof(PluginConfiguration.CustomRightEyeColor) }
                .OrderBy(name => name, StringComparer.Ordinal),
            colours.Keys.OrderBy(name => name, StringComparer.Ordinal));

        Assert.Equal(ProfileCatalog.DefaultCustomLeftEyeColor.ToHexString(), colours[nameof(PluginConfiguration.CustomLeftEyeColor)]);
        Assert.Equal(ProfileCatalog.DefaultCustomRightEyeColor.ToHexString(), colours[nameof(PluginConfiguration.CustomRightEyeColor)]);

        foreach (var stored in colours.Values)
        {
            // The picker's own default is what a browser hands back after a save, so it has
            // to be the text the profile catalog canonicalises a colour to - and nothing a
            // browser could widen into a name or a shorthand.
            Assert.Matches("^#[0-9A-F]{6}$", stored);
            Assert.Equal(stored, RgbColor.Parse(stored).ToHexString());
        }
    }

    [Fact]
    public void TheConcurrencyFieldIsLimitedToWhatTheModelCanHonour()
    {
        var attributes = ReadAttributes(ReadFieldTag(nameof(PluginConfiguration.MaxConcurrentTranscodes)));

        Assert.Equal("number", attributes["type"]);
        Assert.Equal("1", attributes["min"]);
        Assert.Equal(
            PluginConfiguration.DefaultMaxConcurrentTranscodes.ToString(CultureInfo.InvariantCulture),
            attributes["data-anaglyfin-default"]);
    }

    [Fact]
    public void TheSubtitleDepthFeatureIsOneDropdownWithNoEnableSwitch()
    {
        var html = ReadPageHtml();

        // The switch and the mode picker are one dropdown now. Flat is a position on the mode
        // select - the one that asks for nothing - and there is no separate enable box left behind
        // to tick a request the mode dropdown cannot see, or to disagree with it.
        Assert.Contains(
            "Flat",
            ReadOptionValues(nameof(PluginConfiguration.SubtitleDepthMode)).Select(option => option.Value).ToArray(),
            StringComparer.Ordinal);

        Assert.DoesNotContain("data-anaglyfin-field=\"SubtitleDepthEnabled\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-anaglyfin-kind=\"boolean\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Enable FFmpeg-mvc subtitle depth", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdminPageCopyNoLongerNamesTheRetiredSettings()
    {
        // The page was rewritten around fewer decisions, and the prose is part of that change: a
        // label left behind for a setting that is gone tells an administrator to look for a control
        // that no longer exists. The structural tests already prove the controls are absent; this
        // one proves the copy stopped promising them. ("fallback" survives only as a name inside the
        // page's own helpers, never as text a reader sees, so the labels are what is pinned here.)
        var html = ReadPageHtml();

        Assert.DoesNotContain("Fallback profile", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Video encoder policy", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Hardware only", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Software only", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSubtitleDepthModeChoicesAreTheModeEnumeration()
    {
        var options = ReadOptionValues(nameof(PluginConfiguration.SubtitleDepthMode));

        Assert.Equal(Enum.GetNames<SubtitleDepthMode>(), options.Select(option => option.Value).ToArray());
        Assert.All(options, option => Assert.NotEmpty(option.Text));

        // The names are what the settings endpoint binds, so a value no property declares would
        // save as nothing at all - and it would report that it had succeeded.
        Assert.Equal("Automatic", ReadAttributes(ReadFieldTag(nameof(PluginConfiguration.SubtitleDepthMode)))["data-anaglyfin-default"]);
    }

    [Theory]
    [InlineData(nameof(PluginConfiguration.SubtitleDepthShift), SubtitleDepthSettings.MinShiftPixels, SubtitleDepthSettings.MaxShiftPixels)]
    [InlineData(nameof(PluginConfiguration.SubtitleDepthPlane), SubtitleDepthSettings.MinPlane, SubtitleDepthSettings.MaxPlane)]
    public void TheSubtitleDepthNumbersAreLimitedToWhatTheModelCanHonour(string fieldName, int minimum, int maximum)
    {
        var attributes = ReadAttributes(ReadFieldTag(fieldName));

        Assert.Equal("number", attributes["type"]);
        Assert.Equal("1", attributes["step"]);
        Assert.Equal(minimum.ToString(CultureInfo.InvariantCulture), attributes["min"]);
        Assert.Equal(maximum.ToString(CultureInfo.InvariantCulture), attributes["max"]);
        Assert.Equal("boundedInteger", attributes["data-anaglyfin-kind"]);
        Assert.Equal("0", attributes["data-anaglyfin-default"]);
    }

    [Fact]
    public void TheSubtitleDepthFieldsAreLaidOutByTheModeThatWasPicked()
    {
        var html = ReadPageHtml();

        // One field per reading of the depth, and the page shows the one the picked mode asks
        // for: an administrator who chose a constant shift has no use for a plane index, and a
        // form that asked for both is a form whose answers contradict each other.
        foreach (var field in new[] { "mode", "shift", "plane" })
        {
            Assert.Contains($"data-anaglyfin-depth-field=\"{field}\"", html, StringComparison.Ordinal);
        }

        var sync = FunctionBody(html, "syncSubtitleDepthFields");

        // Decided from the one control the page has for the feature now, since there is no view
        // model to be told and no switch left to consult.
        Assert.Contains("#AnaglyfinSubtitleDepthMode", sync, StringComparison.Ordinal);
        Assert.Contains("data-anaglyfin-depth-field", sync, StringComparison.Ordinal);
        Assert.DoesNotContain("#AnaglyfinSubtitleDepthEnabled", sync, StringComparison.Ordinal);

        // Hidden rather than emptied: the field of the mode that was not picked keeps its value,
        // because a save reads it and the settings keep the shape the server stored.
        Assert.Matches(@"fields\[i\]\.hidden\s*=\s*!visible;", sync);
        Assert.DoesNotContain("value = string.Empty", sync, StringComparison.Ordinal);

        // Laid out from the shipped defaults and again once the stored settings arrive, and every
        // time the administrator changes the one depth control the page now has.
        Assert.Contains("syncSubtitleDepthFields();", FunctionBody(html, "applyConfiguration"), StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(html, "syncSubtitleDepthFields\\(\\);").Count);
        Assert.Single(Regex.Matches(html, "addEventListener\\('change', syncSubtitleDepthFields\\)"));

        // The hidden attribute has to be backed by the page's own stylesheet, for the same reason
        // the dashboard notice is: the dashboard's sheet has an opinion about these elements.
        Assert.Matches(@"\.anaglyfin-depthField\[hidden\]\s*\{\s*display:\s*none;", html);
    }

    [Fact]
    public void TheAdminPageCarriesTheOriginalMvcVersionSwitch()
    {
        var html = ReadPageHtml();

        // The switch is a setting like any other, so the structural tests above already prove it is
        // bound, displayed, sent and read back. What is pinned here is the half a user sees: the
        // caption the box is named by, the position it ships in, and what it says turning it off
        // does - because the honest answer ("only the raw file, and only from the picker") is the
        // difference between a switch an administrator dares to touch and one they leave alone.
        var attributes = ReadAttributes(ReadFieldTag(nameof(PluginConfiguration.OfferOriginalMVCVersion)));

        Assert.Equal("checkbox", attributes["type"]);
        Assert.Equal("emby-checkbox", attributes["is"]);
        Assert.Equal("checkbox", attributes["data-anaglyfin-kind"]);

        // Shipped ticked: offering the original file is what every build before this setting did,
        // and hiding it is the decision that has to be made here. Compared against the settings
        // model rather than against a literal of this test, so the two cannot drift.
        Assert.Equal(
            Convert.ToString(new PluginConfiguration().OfferOriginalMVCVersion, CultureInfo.InvariantCulture),
            attributes["data-anaglyfin-default"]);
        Assert.Equal("True", attributes["data-anaglyfin-default"]);

        // The dashboard's checkbox field, in the one arrangement it uses for a checkbox that comes
        // with help text: a container holding a label - the input and its caption span, nothing else
        // - and the help text beside that label. Both halves of that shape are load bearing. The
        // upgrade to emby-checkbox happens while the input is parsed, adopts the label holding it,
        // and takes that label's first span as the caption, so a box outside a label is a bare
        // browser checkbox with nothing to name it. The label it adopts is then laid out as one
        // checkbox, which is what a paragraph inside it is measured against - and a label answers a
        // click anywhere inside itself by toggling, so help text inside the label is help text whose
        // every sentence is a way to flip the switch.
        var field = CheckboxContainerContaining(html, nameof(PluginConfiguration.OfferOriginalMVCVersion));
        var label = CheckboxLabelContaining(field, nameof(PluginConfiguration.OfferOriginalMVCVersion));
        var description = CheckboxDescriptionContaining(field, nameof(PluginConfiguration.OfferOriginalMVCVersion));

        Assert.Matches(@"<div\b[^>]*checkboxContainer checkboxContainer-withDescription[^>]*>", field);
        Assert.Matches(@"<label\b[^>]*>\s*<input\b[^>]*is=""emby-checkbox""[^>]*>\s*<span>", field);

        // The caption is the label's own, which is the half the dashboard's upgrade reads.
        Assert.Contains("<span>Offer original 3D MVC version</span>", label, StringComparison.Ordinal);

        // And the help text is the label's neighbour rather than its contents: it is still inside the
        // container, so the pair is laid out and read together, and outside the label, so neither the
        // label's height nor the label's click reaches it.
        Assert.DoesNotContain("fieldDescription", label, StringComparison.Ordinal);
        Assert.Contains("fieldDescription checkboxFieldDescription", description, StringComparison.Ordinal);
        Assert.True(
            field.IndexOf("fieldDescription", StringComparison.Ordinal) > field.IndexOf("</label>", StringComparison.Ordinal),
            "The help text is back inside the checkbox label, where it is both clipped and clickable.");

        // What the help text owes the administrator: that only the raw file is hidden, that the
        // converted versions are what is left, that the picker is where it is hidden from, and
        // that turning it back is reversible. Nothing here promises a deletion, and nothing here
        // can, because the setting does not delete anything.
        Assert.Contains("version pickers", description, StringComparison.Ordinal);
        Assert.Contains("converted versions", description, StringComparison.Ordinal);
        Assert.Contains("Nothing is deleted", description, StringComparison.Ordinal);
        Assert.Contains("turning it back on", description, StringComparison.Ordinal);

        // And what it must not promise: no scan, no rescan, no relink - the words of every lever
        // that would have had to touch the library.
        Assert.DoesNotContain("scan the library", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rescan", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unlink", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheOriginalMvcVersionSwitchIsReadFromTheBoxItself()
    {
        var html = ReadPageHtml();

        // The read is the box's own checked state and the write is that same state: a switch whose
        // read consulted anything else (a text value, a stored string) would post what the markup
        // shipped rather than what the administrator left it on.
        Assert.Matches(@"checkbox:\s*\{\s*read:\s*function\s*\(element\)\s*\{\s*return element\.checked === true;", html);
        Assert.Contains("element.checked = booleanValue(value, fieldDefault(element));", html, StringComparison.Ordinal);

        // And the default that position comes from is the one the markup declares, which is what
        // keeps the shipped answer in the page rather than in this test or in the script.
        var booleanValue = FunctionBody(html, "booleanValue");

        Assert.Contains("parseBoolean(value)", booleanValue, StringComparison.Ordinal);
        Assert.Contains("parseBoolean(fallback)", booleanValue, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSubtitleDepthModeNamesChooseTheFieldsTheyReveal()
    {
        var html = ReadPageHtml();

        // The layout keys (`shift`, `plane`) are not the spelling the mode option stores, so a
        // field has to name the mode that reveals it. Inferring the pairing by lower-casing the
        // enum name makes `ConstantShift` look for a field called `constantshift`, which hides a
        // number the selected mode needs even though its field is present.
        var expectedFieldByMode = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(SubtitleDepthMode.Automatic)] = string.Empty,
            [nameof(SubtitleDepthMode.ConstantShift)] = "shift",
            [nameof(SubtitleDepthMode.Plane)] = "plane",

            // Flat asks for nothing, so like Automatic it reveals no number field.
            [nameof(SubtitleDepthMode.Flat)] = string.Empty
        };

        Assert.Equal(
            Enum.GetNames<SubtitleDepthMode>().OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            expectedFieldByMode.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray());

        // The modes that reveal no field are seeded in, rather than discovered from the markup,
        // because there is nothing in the markup for them to be discovered from.
        var actualFieldByMode = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(SubtitleDepthMode.Automatic)] = string.Empty,
            [nameof(SubtitleDepthMode.Flat)] = string.Empty
        };

        foreach (Match field in Regex.Matches(html, @"<div\b[^>]*data-anaglyfin-depth-field[^>]*>"))
        {
            var attributes = ReadAttributes(field.Value);
            var fieldKey = attributes.GetValueOrDefault("data-anaglyfin-depth-field", string.Empty);
            var modeName = attributes.GetValueOrDefault("data-anaglyfin-depth-mode", string.Empty);

            if (fieldKey == "mode")
            {
                Assert.False(
                    attributes.ContainsKey("data-anaglyfin-depth-mode"),
                    "The mode selector reveals itself for every mode, so it is not keyed to one mode name.");

                continue;
            }

            Assert.True(
                expectedFieldByMode.TryGetValue(modeName, out var expectedField) && expectedField.Length > 0,
                $"The depth field '{fieldKey}' names a mode no settings enum declares: '{modeName}'.");

            Assert.Equal(expectedField, fieldKey);
            Assert.True(actualFieldByMode.TryAdd(modeName, fieldKey), $"The mode '{modeName}' revealed more than one field.");
        }

        Assert.Equal(expectedFieldByMode, actualFieldByMode);

        var sync = FunctionBody(html, "syncSubtitleDepthFields");

        // The function compares the mode option's stored spelling to that attribute, and uses that
        // answer for both visibility and whether the control can be typed into. That is the half
        // that turns picking "One constant shift" into an editable shift box - and picking Flat or
        // Automatic into no number box at all.
        Assert.Contains("#AnaglyfinSubtitleDepthMode", sync, StringComparison.Ordinal);
        Assert.Contains("data-anaglyfin-depth-mode", sync, StringComparison.Ordinal);
        Assert.Contains("modeForField === wantedMode", sync, StringComparison.Ordinal);
        Assert.DoesNotContain("#AnaglyfinSubtitleDepthEnabled", sync, StringComparison.Ordinal);
        Assert.Matches(
            @"var visible\s*=\s*when\s*===\s*'mode'\s*\|\|\s*\(modeForField\s*!==\s*''\s*&&\s*modeForField\s*===\s*wantedMode\);",
            sync);
        Assert.Matches(@"fields\[i\]\.hidden\s*=\s*!visible;", sync);
        Assert.Matches(@"input\.disabled\s*=\s*!visible;", sync);
        Assert.DoesNotContain("wanted.toLowerCase", sync, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdminPageCarriesNoFreeFormConversionInput()
    {
        var html = ReadPageHtml();

        Assert.DoesNotContain("<textarea", html, StringComparison.OrdinalIgnoreCase);

        foreach (var editable in ReadEditableTags(html))
        {
            var attributes = ReadAttributes(editable);

            if (string.Equals(TagName(editable), "input", StringComparison.OrdinalIgnoreCase))
            {
                var type = attributes.GetValueOrDefault("type", string.Empty);

                // Every value the page can carry is chosen, not typed: switches, colours and
                // numbers. The free-text id box the exact-device rows fell back to was the
                // page's only text input and it left with the feature, so nothing may type
                // an identifier at all now.
                Assert.Contains(type, new[] { "checkbox", "color", "number" }, StringComparer.OrdinalIgnoreCase);
            }

            foreach (var attribute in attributes)
            {
                foreach (var forbidden in ForbiddenAttributeText)
                {
                    Assert.False(
                        attribute.Value.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                        $"'{attribute.Key}={attribute.Value}' names a {forbidden}. Only profile ids, enum names, colours and numbers may be edited.");
                }
            }
        }
    }

    [Fact]
    public void TheScriptDrawsEveryFieldKindTheMarkupDeclares()
    {
        var html = ReadPageHtml();

        // The markup binds a field to a kind and the script draws that kind. A kind nobody
        // draws renders as an empty box that a save then drops, so the pairing is worth a
        // test: it is the one thing about the page that neither the model nor the endpoint
        // can complain about later.
        var kinds = Regex.Matches(html, "data-anaglyfin-kind=\"(?<kind>[^\"]+)\"")
            .Cast<Match>()
            .Select(match => match.Groups["kind"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(kinds);

        foreach (var kind in kinds)
        {
            Assert.Matches($@"{Regex.Escape(kind)}: \{{\s*read:", html);
        }

        // And the page paints the shipped defaults as it is built, rather than leaving an
        // administrator staring at a blank form until the settings endpoint answers.
        Assert.Contains("applyShippedDefaults();", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEnabledProfileBoxesAreBuiltInTheDashboardCheckboxShape()
    {
        var html = ReadPageHtml();

        // The dashboard upgrades a checkbox while the element is created and takes its
        // caption from the span beside the input, so the boxes have to arrive as parsed
        // markup of exactly this shape. An input assembled in code and given its is=
        // attribute afterwards is never upgraded: it stays a bare browser checkbox, with
        // no caption element for the upgrade to name.
        Assert.Matches(
            @"<label\s+class=""checkboxContainer checkboxContainer-withDescription""\s*>\s*"
            + @"<input\s+type=""checkbox""\s+is=""emby-checkbox""\s*>\s*<span>",
            html);

        Assert.DoesNotContain("setAttribute('is'", html, StringComparison.Ordinal);
        Assert.DoesNotContain("setAttribute(\"is\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdminPageReadsItsFieldsFromItsOwnPageElement()
    {
        var html = ReadPageHtml();

        // The dashboard keeps more than one plugin page in the same document, so a query
        // against the document would read another plugin's fields as this page's own and
        // save them back under Anaglyfin's settings.
        Assert.DoesNotContain("document.querySelectorAll('[data-anaglyfin-field]')", html, StringComparison.Ordinal);
        Assert.DoesNotContain("document.querySelector('[data-anaglyfin-field]')", html, StringComparison.Ordinal);

        // And the element the fields are queried from is this page's own: looked up by an
        // id the markup carries, rather than assumed.
        Assert.Matches(@"page\.querySelectorAll\('\[data-anaglyfin-field\]'\)", html);

        var pageId = Regex.Match(html, "var pageId = '(?<id>[^']+)'");
        Assert.True(pageId.Success, "The script no longer names the page element it reads its fields from.");
        Assert.Contains("<div id=\"" + pageId.Groups["id"].Value + "\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdminPageCarriesNoDeploymentInternals()
    {
        // Everything on this page is a choice the administrator can make there. What the deployment
        // has to arrange - which binary the server runs, where the running jobs are counted, which
        // file carries the settings across the process boundary - is not editable here, was never
        // the administrator's to guess at, and belongs in the install guide. A variable name on this
        // page is a thing an administrator can do nothing about while looking at it.
        //
        // The runtime status section is the one exception the owner asked for, and it is a read-only
        // answer rather than an edit: the page shows what the deployment named, because an
        // administrator who cannot tell a missing binary from a wrong one was sent to a shell for it.
        // The exception is bounded rather than general - it is the marked section and its script,
        // and the names it may carry are the ones the status endpoint itself reports, which
        // TheRuntimeStatusSectionNamesNoVariableTheStatusEndpointDoesNotReport pins. It no longer
        // needs any of them in its own copy: the answer carries the names, and the paragraph that
        // recited them before the endpoint had answered is gone. Everything the settings half of the
        // page has to stay free of, it still stays free of.
        var (outside, regions) = ReadPageWithoutRuntimeStatus();

        Assert.DoesNotContain("wrapper", outside, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environment", outside, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("variable", outside, StringComparison.OrdinalIgnoreCase);

        foreach (var internalName in ForbiddenPageText)
        {
            Assert.DoesNotContain(internalName, outside, StringComparison.OrdinalIgnoreCase);
        }

        // And inside the section, every deployment name that does appear is one of those: anything
        // else an editor pastes into that markup is reported here rather than shipped.
        foreach (var region in regions)
        {
            var scrubbed = region;

            foreach (var reported in RuntimeStatusReportedVariables)
            {
                scrubbed = scrubbed.Replace(reported, string.Empty, StringComparison.OrdinalIgnoreCase);
            }

            foreach (var internalName in ForbiddenPageText)
            {
                Assert.DoesNotContain(internalName, scrubbed, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void TheAdminPageCarriesTheRuntimeStatusSectionBelowTheDeploymentGuidance()
    {
        // The section belongs where the deployment guidance already is: an administrator reading
        // what the server has to be running is then one glance away from what it is actually
        // running, with nothing to type and nothing to save in between.
        var html = ReadPageHtml();
        var (markup, _) = ReadRuntimeStatusRegions();

        var guidance = html.IndexOf("Requires a Jellyfin-compatible FFmpeg-mvc build", StringComparison.Ordinal);
        var section = html.IndexOf("<div class=\"section anaglyfin-section\" id=\"AnaglyfinRuntimeStatus\"", StringComparison.Ordinal);
        var save = html.IndexOf("<button is=\"emby-button\" type=\"submit\"", StringComparison.Ordinal);

        Assert.True(guidance >= 0, "The page no longer states the deployment requirement it sits beside.");
        Assert.True(section > guidance, "The runtime status section no longer follows the deployment guidance.");
        Assert.True(save > section, "The runtime status section no longer sits above the Save button.");

        Assert.Contains("<h2 class=\"sectionTitle\">Runtime status</h2>", html, StringComparison.Ordinal);
        Assert.Contains("<caption>Runtime binaries</caption>", markup, StringComparison.Ordinal);
        Assert.Contains("<caption>Deployment variables</caption>", markup, StringComparison.Ordinal);

        // One answer the endpoint gives per column the page has, and the page is not asking for a
        // column the endpoint has nothing for. Read as the whole heading row of each table rather
        // than as a list of headings to find somewhere in the section: a column added back at either
        // end is a column the page has stopped being able to fill.
        Assert.Equal(
            RuntimeStatusBinaryColumns,
            ReadColumnHeadings(RuntimeStatusTable(markup, "Runtime binaries")));
        Assert.Equal(
            RuntimeStatusVariableColumns,
            ReadColumnHeadings(RuntimeStatusTable(markup, "Deployment variables")));

        // The two columns that used to sit at the right-hand end of these tables are gone by the
        // owner's request, and they are gone rather than merely unnamed: nothing in the section
        // still holds a heading, a cell or a caption that calls itself one of them.
        foreach (var retired in new[] { "Detail", "Source" })
        {
            Assert.DoesNotContain($">{retired}<", markup, StringComparison.Ordinal);
            Assert.DoesNotContain($">{retired} ", markup, StringComparison.Ordinal);
            Assert.DoesNotContain($"<th scope=\"col\">{retired}</th>", markup, StringComparison.Ordinal);
        }

        // The summary, the two tables and the warning are all there before the endpoint has
        // answered, so an administrator reads "not read yet" rather than a silent half of a page.
        foreach (var elementId in new[]
                {
                    "AnaglyfinRuntimeStatusOverall",
                    "AnaglyfinRuntimeStatusVersion",
                    "AnaglyfinRuntimeStatusChecked",
                    "AnaglyfinRuntimeStatusWarning",
                    "AnaglyfinRuntimeStatusBinaries",
                    "AnaglyfinRuntimeStatusEnvironment",
                    "AnaglyfinRuntimeStatusRefresh"
                })
        {
            Assert.Contains($"id=\"{elementId}\"", markup, StringComparison.Ordinal);
        }

        Assert.Contains("Runtime status has not been read yet.", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRuntimeStatusTablesAreOneGridOfFiveAlignedColumns()
    {
        // The two tables are read one under the other, so an administrator's eye is already trying
        // to line them up. They are aligned by declaring the same grid rather than by hoping the
        // same content arrives in both: five columns, the same five classes on each table's own
        // colgroup, those widths said once in the stylesheet, and a fixed layout so the widths are
        // what the page chose rather than what the longest path in the answer negotiated.
        var html = ReadPageHtml();
        var (markup, script) = ReadRuntimeStatusRegions();

        var binaries = RuntimeStatusTable(markup, "Runtime binaries");
        var variables = RuntimeStatusTable(markup, "Deployment variables");

        foreach (var table in new[] { binaries, variables })
        {
            var columns = ReadColumnClasses(table);

            Assert.Equal(RuntimeStatusColumnClasses, columns);
            Assert.Equal(5, ReadColumnHeadings(table).Length);

            // An empty table still spans the grid it belongs to, rather than five columns' worth of
            // colspan that would leave the "nothing reported" row sitting over the wrong columns.
            Assert.Contains("<td colspan=\"5\">", table, StringComparison.Ordinal);
            Assert.DoesNotContain("colspan=\"6\"", table, StringComparison.Ordinal);
            Assert.DoesNotContain("colspan=\"7\"", table, StringComparison.Ordinal);
        }

        // The same five names in the same order on both tables is the alignment, so a table that
        // renamed its own third column is reported even when both still have five columns.
        Assert.Equal(ReadColumnClasses(binaries), ReadColumnClasses(variables));

        // Fixed layout, and each of the five widths said once, in the stylesheet rather than in the
        // markup, so the two tables cannot drift apart.
        var tableRule = StyleRule(html, @"\.anaglyfin-runtimeStatusTable");
        var cellRule = StyleRule(html, @"\.anaglyfin-runtimeStatusTable\s+th,\s*\.anaglyfin-runtimeStatusTable\s+td");
        var headingRule = StyleRule(html, @"\.anaglyfin-runtimeStatusTable\s+th");

        Assert.Matches(@"table-layout:\s*fixed", tableRule);

        foreach (var column in RuntimeStatusColumnClasses)
        {
            Assert.Matches(@"width:\s*\d+%", StyleRule(html, @"\." + column));
        }

        // Lines that are actually visible: a rule on the table, a rule on every cell, and a heavier
        // one under the headings, so the header row separates from the rows it names.
        Assert.Matches(@"border:\s*[\d.]+em\s+solid", tableRule);
        Assert.Matches(@"border:\s*[\d.]+em\s+solid", cellRule);
        Assert.Matches(@"border-bottom:\s*[\d.]+em\s+solid", headingRule);

        // And the long path that is the only reason a table ever needed to negotiate its own widths:
        // it breaks inside its cell rather than pushing a column wider and sliding the tables apart.
        Assert.Matches(@"overflow-wrap:\s*(break-word|anywhere)", cellRule);

        // The script counts the same grid the markup declares, so a column added to one half and not
        // the other is reported rather than rendered as a table with a cell missing.
        Assert.Equal("5", Regex.Match(script, @"var runtimeStatusColumnCount = (?<count>\d+);").Groups["count"].Value);
        Assert.Equal(
            2,
            Regex.Matches(script, @"runtimeStatusFillRows\(nodes\.(?:binaries|entries), [^,]*, [^,]*, runtimeStatusColumnCount,").Count);
    }

    [Fact]
    public void TheRuntimeStatusSectionIsNotAParagraphAboutTheDeployment()
    {
        // The section used to open with a paragraph naming every binary and every deployment
        // variable the endpoint might report. It is gone by the owner's request: what the endpoint
        // reports is the endpoint's decision, and a page that recites a list it cannot verify is a
        // page an administrator trusts while it is stale. One short line of copy is left under the
        // heading, and everything the section still owes the administrator - the overall answer, the
        // version and the checked stamp, the Refresh button, the warning and the two captions - stays.
        var html = ReadPageHtml();
        var (markup, _) = ReadRuntimeStatusRegions();

        foreach (var removed in RemovedRuntimeStatusParagraph)
        {
            Assert.Empty(Regex.Matches(html, removed));
        }

        var copy = RuntimeStatusHeadingCopy(markup);

        Assert.False(string.IsNullOrWhiteSpace(copy), "The runtime status heading has lost its one line of copy.");
        Assert.True(
            copy.Length <= 200,
            $"The runtime status heading is followed by a paragraph again ({copy.Length} characters): {copy}");

        // What removing the paragraph must not have taken with it.
        Assert.Contains("Runtime status has not been read yet.", markup, StringComparison.Ordinal);
        Assert.Contains("<span>Refresh</span>", markup, StringComparison.Ordinal);
        Assert.Contains("id=\"AnaglyfinRuntimeStatusWarning\"", markup, StringComparison.Ordinal);
        Assert.Contains("Plugin version: not read.", markup, StringComparison.Ordinal);
        Assert.Contains("Checked: not read.", markup, StringComparison.Ordinal);
        Assert.Contains("<caption>Runtime binaries</caption>", markup, StringComparison.Ordinal);
        Assert.Contains("<caption>Deployment variables</caption>", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRuntimeStatusSectionAsksThePluginForItsStatusThroughTheDashboardClient()
    {
        // The endpoint is the plugin's own and the transport is the dashboard's own signed-in
        // client, which is the same rule the settings follow and for the same reason: a copy of this
        // page opened outside the dashboard has no credential for an admin-only endpoint, and a page
        // that improvised one would be a page making authenticated calls nobody asked it to make.
        var html = ReadPageHtml();
        var (_, script) = ReadRuntimeStatusRegions();

        Assert.Contains("var runtimeStatusEndpoint = '/Anaglyfin/RuntimeStatus';", script, StringComparison.Ordinal);

        // The client is asked the way the dashboard client it ships against can actually be asked:
        // getUrl, for the address of the server the dashboard is signed in to, and getJSON, for the
        // JSON answer at that address. Both are demanded of the client by name, because a client
        // carrying only one of them cannot complete the ask - and because a check asking for a call
        // the client does not answer to was the check that reported a signed-in dashboard as a page
        // with no client at all.
        var available = FunctionBody(html, "runtimeStatusClientAvailable");

        Assert.Contains("typeof window.ApiClient.getJSON === 'function'", available, StringComparison.Ordinal);
        Assert.Contains("typeof window.ApiClient.getUrl === 'function'", available, StringComparison.Ordinal);

        // The two refusals the section is allowed to make are made in the section's own words: a
        // page with no client to ask, and a client that would not take the ask.
        Assert.Contains("if (!runtimeStatusClientAvailable()) {", script, StringComparison.Ordinal);
        Assert.Contains(
            "The runtime status was not read: this copy of the page has no dashboard client to ask with.",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "The runtime status was not read: the dashboard client could not be asked.",
            script,
            StringComparison.Ordinal);
        Assert.Contains("The Anaglyfin runtime status endpoint did not answer.", script, StringComparison.Ordinal);

        // And the ask is made through those two calls only, after the check above - never through a
        // call the dashboard client does not answer to, which is the difference between this section
        // working on the dashboard and this section giving up on it.
        var request = FunctionBody(html, "requestRuntimeStatus");

        Assert.Contains("window.ApiClient.getUrl(", request, StringComparison.Ordinal);
        Assert.Matches(@"window\.ApiClient\.getJSON\(\s*url\s*\)", request);
        Assert.True(
            request.IndexOf("runtimeStatusClientAvailable()", StringComparison.Ordinal)
                < request.IndexOf("window.ApiClient.getUrl(", StringComparison.Ordinal),
            "The runtime status section asks the dashboard client before it has checked that the client can be asked.");

        // The settings half's rule that no transport may belong to the page itself still holds over
        // the section's head, and it now holds over this section's head as well: no request of the
        // page's own, and no request invented for the client.
        Assert.DoesNotContain("fetch(", html, StringComparison.Ordinal);
        Assert.DoesNotContain("XMLHttpRequest", html, StringComparison.Ordinal);
        AssertPageCarriesNoClientRequestCall(html);

        // No second way to be refused by the absence of a dashboard: the section declines with its
        // own warning rather than by revealing the notice that belongs to the settings.
        Assert.Contains("function dashboardAvailable()", html, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(html, "revealDashboardNotice\\(\\);").Count);

        // Asked once when the page opens, and after that only on a click: nothing on this page
        // re-probes the server on a timer of its own.
        Assert.DoesNotContain("setInterval", html, StringComparison.Ordinal);
        Assert.DoesNotContain("setTimeout", html, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(html, "requestRuntimeStatus\\(false\\);"));
        Assert.Single(Regex.Matches(html, "requestRuntimeStatus\\(true\\);"));
    }

    [Fact]
    public void TheRuntimeStatusSectionAsksTheClientTheDashboardActuallyShips()
    {
        // The pinned evidence for the failure this section had: the dashboard's ApiClient answers to
        // getUrl and to getJSON, and it has no request to ask at all. A section whose availability
        // test looked for `request` could therefore never find a client, and it showed an
        // administrator a working dashboard as one without a client.
        var html = ReadPageHtml();
        var (_, script) = ReadRuntimeStatusRegions();

        AssertPageCarriesNoClientRequestCall(html);

        // The ask is a read of the address the client built, and nothing is handed to the client
        // beside that address: the method and the shape of the expected answer belong to the client's
        // own call, not to a request description this page writes for it.
        var request = FunctionBody(html, "requestRuntimeStatus");

        Assert.DoesNotContain("type:", request, StringComparison.Ordinal);
        Assert.DoesNotContain("dataType:", request, StringComparison.Ordinal);
        Assert.DoesNotContain("post(", request, StringComparison.Ordinal);

        // The availability check demands both calls by the client's own spelling - one of them is
        // enough to complete nothing, and a check that drifted back to a single call or to a made up
        // one is reported here rather than shipped.
        var available = FunctionBody(html, "runtimeStatusClientAvailable");

        Assert.Equal(2, Regex.Matches(available, @"typeof window\.ApiClient\.").Count);
        Assert.Contains("typeof window.ApiClient.getJSON === 'function'", available, StringComparison.Ordinal);
        Assert.Contains("typeof window.ApiClient.getUrl === 'function'", available, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(script, @"if \(!runtimeStatusClientAvailable\(\)\) \{"));
    }

    [Fact]
    public void TheRuntimeStatusRefreshButtonAsksForAFreshPassAndGivesItselfBack()
    {
        // Refresh is the only way a second pass happens, and it has to be legible while it is
        // under way: the button goes out for the duration of the request and comes back whether the
        // endpoint answered, answered with nothing, refused, or could not be asked at all.
        var html = ReadPageHtml();
        var (markup, _) = ReadRuntimeStatusRegions();

        var refreshButton = Assert.Single(Regex.Matches(markup, @"<button\b[^>]*>")).Value;
        Assert.Contains("id=\"AnaglyfinRuntimeStatusRefresh\"", refreshButton, StringComparison.Ordinal);
        Assert.Contains(@"type=""button""", refreshButton, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"submit\"", refreshButton, StringComparison.Ordinal);
        Assert.Contains("<span>Refresh</span>", markup, StringComparison.Ordinal);

        var request = FunctionBody(html, "requestRuntimeStatus");

        Assert.Contains("var runtimeStatusRefreshQuery = '?refresh=true';", html, StringComparison.Ordinal);

        // A fresh pass is the same address the page opens with, plus the client's own query for it,
        // built by the client itself so it lands on the server the dashboard is signed in to.
        Assert.Matches(
            @"window\.ApiClient\.getUrl\(\s*runtimeStatusEndpoint\s*\+\s*\(\s*refresh\s*\?\s*runtimeStatusRefreshQuery\s*:\s*''\s*\)\s*\)",
            request);
        Assert.Matches(@"window\.ApiClient\.getJSON\(\s*url\s*\)", request);
        Assert.Contains("setRuntimeStatusRefreshEnabled(nodes, false);", request, StringComparison.Ordinal);
        Assert.Contains("setRuntimeStatusRefreshEnabled(nodes, true);", request, StringComparison.Ordinal);
        Assert.Contains("if (runtimeStatusLoading) {", request, StringComparison.Ordinal);

        // Every way out of the request gives the button back: no dashboard client, a client that
        // would not answer, an answer with nothing in it, and a refusal.
        Assert.Equal(4, Regex.Matches(request, "finished\\(\\);").Count);

        var refresh = FunctionBody(html, "refreshRuntimeStatus");
        Assert.Contains("requestRuntimeStatus(true);", refresh, StringComparison.Ordinal);
        Assert.Contains("event.preventDefault();", refresh, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(html, "addEventListener\\('click', refreshRuntimeStatus\\)"));
    }

    [Fact]
    public void TheRuntimeStatusSectionIsBuiltOutOfTextAndNothingElse()
    {
        // Nothing in the section is editable, and nothing the server said is ever put into the
        // document as markup. Paths, labels and variable values are deployment choices the page
        // cannot vouch for, so they arrive as text and stay text: read, never run, never submitted.
        var (markup, script) = ReadRuntimeStatusRegions();

        foreach (var region in new[] { markup, script })
        {
            foreach (var editable in new[] { "<input", "<select", "<textarea", "data-anaglyfin-field", "data-anaglyfin-kind" })
            {
                Assert.DoesNotContain(editable, region, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.DoesNotContain("innerHTML", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("insertAdjacentHTML", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("document.write", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outerHTML", script, StringComparison.OrdinalIgnoreCase);

        // Cells are made one element at a time and filled through textContent, so there is no road
        // from an endpoint answer to the document other than text.
        Assert.Contains("document.createElement('td')", script, StringComparison.Ordinal);
        Assert.Contains("document.createElement('tr')", script, StringComparison.Ordinal);
        Assert.Contains("cell.textContent = text;", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRuntimeStatusSectionShowsItsOwnWordsForEveryState()
    {
        // The states are the endpoint's enumerations and the page never repeats them back: what an
        // administrator reads is this page's wording for that state, chosen by position out of the
        // list beside the names, with Unknown as the answer for a state this build has no words for.
        var html = ReadPageHtml();
        var (_, script) = ReadRuntimeStatusRegions();

        Assert.Equal(RuntimeStatusOverallTexts, ReadScriptList(script, "runtimeStatusOverallTexts"));
        Assert.Equal(RuntimeStatusItemTexts, ReadScriptList(script, "runtimeStatusItemTexts"));

        Assert.Equal(RuntimeStatusOverallTexts.Length, ReadScriptList(script, "runtimeStatusOverallNames").Length);
        Assert.Equal(RuntimeStatusItemTexts.Length, ReadScriptList(script, "runtimeStatusItemNames").Length);
        Assert.Equal(RuntimeStatusOverallTexts.Length, ReadScriptList(script, "runtimeStatusOverallClasses").Length);
        Assert.Equal(RuntimeStatusItemTexts.Length, ReadScriptList(script, "runtimeStatusItemClasses").Length);

        // The names are the endpoint's enumeration, spelled as it spells it and in the order it
        // stores it: every mapping beside them is chosen by position, so a state renamed or reorders
        // in the model has to be seen here rather than as a row showing the wrong sentence.
        Assert.Equal(Enum.GetNames<RuntimeItemState>(), ReadScriptList(script, "runtimeStatusItemNames"));

        // The state is only ever shown through the mapping. A row that dropped the raw answer into
        // a cell would show an administrator a word they were never meant to read.
        var binaryRow = FunctionBody(html, "runtimeStatusBinaryRow");
        var variableRow = FunctionBody(html, "runtimeStatusEnvironmentRow");

        Assert.Contains("runtimeStatusItemText(readProperty(binary, 'State'))", binaryRow, StringComparison.Ordinal);
        Assert.Contains("runtimeStatusItemText(readProperty(entry, 'State'))", variableRow, StringComparison.Ordinal);
        Assert.DoesNotContain("readProperty(binary, 'State')]", binaryRow, StringComparison.Ordinal);
        Assert.DoesNotContain("readProperty(entry, 'State')]", variableRow, StringComparison.Ordinal);

        // And the words themselves, so a state cannot quietly lose its sentence.
        foreach (var text in RuntimeStatusOverallTexts.Concat(RuntimeStatusItemTexts))
        {
            Assert.Contains($"'{text}'", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheRuntimeStatusRowsAreColouredByThePageAndNotByTheEndpoint()
    {
        // A coloured row is the one thing this section decides for itself out of an endpoint answer,
        // and the decision has to stay the page's: the answer only ever chooses a position in a list
        // the page wrote, and what reaches the row is one of the three class names the page styles.
        // Nothing that arrives over the network is allowed to become a class name, which is the same
        // rule that keeps it out of the document as markup - a state the server invented would style
        // this page as easily as it could fill a cell.
        var html = ReadPageHtml();
        var (_, script) = ReadRuntimeStatusRegions();

        // The nine states, aligned with the endpoint's own enumeration, each mapped onto one of the
        // three classes the page owns: green for the one that passed, amber for the one that is
        // present but should be looked at, and red for every state that costs a command something -
        // including Unknown, which is not a pass.
        var itemClasses = ReadScriptList(script, "runtimeStatusItemClasses");
        var states = Enum.GetValues<RuntimeItemState>();

        Assert.Equal(states.Length, itemClasses.Length);

        for (var i = 0; i < states.Length; i++)
        {
            Assert.Equal(ExpectedRuntimeStatusRowClass(states[i]), itemClasses[i]);
        }

        // All three families are in use, and they are the page's own names, styled by the page.
        foreach (var (stateClass, _) in RuntimeStatusRowStates)
        {
            Assert.Contains(stateClass, itemClasses, StringComparer.Ordinal);
        }

        // And styled where it counts. The dashboard arrives with its own sheet that paints td
        // elements, and a colour the row hands down only reaches a cell by inheritance - which any
        // declaration the dashboard makes on the cell overrules. A page that coloured the tr alone
        // was a page whose three states all arrived the same grey, so the rule has to be the one on
        // the cells, wearing the page's colour and the band that makes a row read as one answer.
        foreach (var (stateClass, colour) in RuntimeStatusRowStates)
        {
            var stateCells = StyleRule(html, @"\." + stateClass + @"\s+td");

            Assert.Matches(@"color:\s*" + Regex.Escape(colour), stateCells);
            Assert.Matches(@"background-color:\s*rgba\(", stateCells);

            // And it has to be that rule rather than one on the row: a colour carried on the tr
            // alone is one a dashboard sheet can answer from above, so neither the colour nor the
            // band may live there.
            Assert.DoesNotMatch(@"\." + stateClass + @"\s*\{[^}]*(color|background)", html);
        }

        // The row takes its class from its caller alone, and the caller takes it from the list above
        // and nothing else: no cell text, no reported value, no string built out of an answer.
        var rowBuilder = FunctionBody(html, "runtimeStatusRow");

        Assert.Contains("row.className = stateClass;", rowBuilder, StringComparison.Ordinal);
        Assert.DoesNotContain("classList", rowBuilder, StringComparison.Ordinal);
        Assert.DoesNotContain("setAttribute('class'", rowBuilder, StringComparison.Ordinal);
        Assert.DoesNotContain("readProperty", rowBuilder, StringComparison.Ordinal);

        var classMapper = FunctionBody(html, "runtimeStatusItemClass");

        Assert.Contains("runtimeStatusItemClasses[", classMapper, StringComparison.Ordinal);
        Assert.DoesNotContain("readProperty", classMapper, StringComparison.Ordinal);
        Assert.DoesNotContain("runtimeStatusText", classMapper, StringComparison.Ordinal);
        Assert.DoesNotContain("String(", classMapper, StringComparison.Ordinal);

        // And neither table hands its row a class from anywhere else, nor a raw state as a class.
        foreach (var (name, raw) in new[]
                {
                    ("runtimeStatusBinaryRow", "readProperty(binary, 'State')"),
                    ("runtimeStatusEnvironmentRow", "readProperty(entry, 'State')")
                })
        {
            var body = FunctionBody(html, name);

            Assert.Contains($"runtimeStatusItemClass({raw})", body, StringComparison.Ordinal);
            Assert.DoesNotContain($", {raw})", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheRuntimeStatusRowsCarryNoDetailOrSourceColumn()
    {
        // The two columns the owner asked off the page were the two that recited the endpoint's own
        // words back: Detail was the probe's sentence and Source was the variable that supplied a
        // value. They are off the generated rows as well as off the headings, so a row cannot grow
        // the column back by reading the property again - and the helper that spelled the source out
        // is gone rather than left uncalled, because a helper nobody calls is a helper somebody
        // re-calls.
        var html = ReadPageHtml();
        var (markup, script) = ReadRuntimeStatusRegions();

        var binaryValues = RowValueExpressions(FunctionBody(html, "runtimeStatusBinaryRow"));
        var variableValues = RowValueExpressions(FunctionBody(html, "runtimeStatusEnvironmentRow"));

        Assert.Equal(RuntimeStatusBinaryColumns.Length, binaryValues.Length);
        Assert.Equal(RuntimeStatusVariableColumns.Length, variableValues.Length);

        Assert.Equal(
            new[]
            {
                "runtimeStatusItemText(readProperty(binary, 'State'))",
                "runtimeStatusYesNo(readProperty(binary, 'Required'))",
                "readProperty(binary, 'Label')",
                "path",
                "readProperty(binary, 'Version')"
            },
            binaryValues);

        Assert.Equal(
            new[]
            {
                "runtimeStatusItemText(readProperty(entry, 'State'))",
                "runtimeStatusYesNo(readProperty(entry, 'Required'))",
                "readProperty(entry, 'Name')",
                "readProperty(entry, 'Value')",
                "readProperty(entry, 'ResolvedPath')"
            },
            variableValues);

        // The properties those two columns were built from are not read anywhere in the section at
        // all, and neither is the flag the source column used to fall back on.
        foreach (var region in new[] { markup, script })
        {
            Assert.DoesNotContain("Detail", region, StringComparison.Ordinal);
            Assert.DoesNotContain("SourceVariable", region, StringComparison.Ordinal);
            Assert.DoesNotContain("'IsSet'", region, StringComparison.Ordinal);
            Assert.DoesNotContain("runtimeStatusSourceText", region, StringComparison.Ordinal);
            Assert.DoesNotContain("'Set directly'", region, StringComparison.Ordinal);
            Assert.DoesNotContain("'Not set'", region, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheRuntimeStatusTablesPutEveryRequiredRowAboveTheRest()
    {
        // What has to be there is read first. The two tables are the section's answer to "what is
        // wrong with this installation", and a table that showed a row the deployment can do without
        // above a row it cannot made the administrator read past the problem to find it. The order is
        // a partition rather than a ranking: the page has no basis for ordering two missing binaries
        // against each other, so the endpoint's own order is kept inside each half and the only thing
        // that moves is which half of the table a row is shown in.
        var html = ReadPageHtml();
        var (_, script) = ReadRuntimeStatusRegions();

        Assert.Matches(@"function runtimeStatusOrderByRequired\(\s*entries\s*\)\s*\{", script);

        var ordering = FunctionBody(html, "runtimeStatusOrderByRequired");

        // Required-ness is asked of the reported item by the one property the Required column of
        // that same row shows, and read the same way that column reads it: a row cannot sit in the
        // half of the table its own column contradicts. Anything that is not a yes - another answer,
        // or no answer at all - belongs to the half that can be done without, which is the honest
        // reading of a missing one rather than a guess in the page's favour.
        Assert.Contains("parseBoolean(readProperty(entry, 'Required')) === true", ordering, StringComparison.Ordinal);

        // The two halves are collected apart and handed back required-then-not, so the half that has
        // to be there is above the half that only ought to be.
        Assert.Contains("var required = [];", ordering, StringComparison.Ordinal);
        Assert.Contains("var optional = [];", ordering, StringComparison.Ordinal);
        Assert.Contains("required.push(entry);", ordering, StringComparison.Ordinal);
        Assert.Contains("optional.push(entry);", ordering, StringComparison.Ordinal);
        Assert.True(
            ordering.IndexOf("required.push(entry);", StringComparison.Ordinal)
                < ordering.IndexOf("optional.push(entry);", StringComparison.Ordinal),
            "The runtime status helper fills the half a row can do without before the half it cannot.");
        Assert.Matches(@"return\s+required\.concat\(\s*optional\s*\)\s*;", ordering);

        // A partition and not a sort: one walk from the front of the answer to the back, no
        // comparator and no second key, so two rows the endpoint put in a row stay in that row.
        Assert.Matches(@"for\s*\(\s*var i = 0;\s*i < \w+\.length;\s*i\+\+\s*\)", ordering);
        Assert.DoesNotContain(".sort", ordering, StringComparison.Ordinal);
        Assert.DoesNotContain("localeCompare", ordering, StringComparison.Ordinal);

        // And the answer the caller is holding is left as it arrived: the helper builds its own two
        // lists and returns a third, rather than rearranging the one it was handed.
        foreach (var mutation in new[]
                {
                    "entries[",
                    "entries =",
                    "entries.push",
                    "entries.pop",
                    "entries.shift",
                    "entries.unshift",
                    "entries.reverse",
                    "entries.splice",
                    "entries.sort"
                })
        {
            Assert.DoesNotContain(mutation, ordering, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("return entries", ordering, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRuntimeStatusTablesAreBothFilledThroughThatOneOrder()
    {
        // One order, decided once, for both tables. The binaries and the deployment rows are one
        // grid answering one question, and an order applied where one table is filled and not where
        // the other is filled is two rules an administrator has to guess the difference between.
        var html = ReadPageHtml();
        var (_, script) = ReadRuntimeStatusRegions();

        var fill = FunctionBody(html, "runtimeStatusFillRows");

        Assert.Contains("runtimeStatusOrderByRequired(entries)", fill, StringComparison.Ordinal);

        // The order is taken before a single row is built, and every row is built out of the ordered
        // list: a row appended straight from the unordered one would land back wherever the endpoint
        // left it, which is the difference between ordering the table and ordering part of it.
        var ordered = fill.IndexOf("runtimeStatusOrderByRequired(entries)", StringComparison.Ordinal);
        var appended = fill.IndexOf("body.appendChild(buildRow(", StringComparison.Ordinal);

        Assert.True(
            appended > ordered,
            "The runtime status tables build their rows before those rows are put in their order.");
        Assert.DoesNotContain("buildRow(entries[", fill, StringComparison.Ordinal);

        // Both tables still arrive through the one shared fill, so the order above is the order of
        // the binaries and of the deployment rows alike.
        Assert.Contains("runtimeStatusFillRows(nodes.binaries, binaries,", script, StringComparison.Ordinal);
        Assert.Contains("runtimeStatusFillRows(nodes.entries, entries,", script, StringComparison.Ordinal);
        Assert.Equal(
            2,
            Regex.Matches(script, @"runtimeStatusFillRows\(nodes\.(?:binaries|entries), [^,]*, [^,]*, runtimeStatusColumnCount,").Count);
    }

    [Fact]
    public void TheRuntimeStatusSectionNamesNoVariableTheStatusEndpointDoesNotReport()
    {
        // The section is the one part of the page allowed to name the deployment, which is exactly
        // why the set it may name is pinned against the code that reports it: a page advertising a
        // variable nothing reads is a page an administrator will go and set.
        //
        // What the page no longer does is recite that set before the endpoint has answered. The list
        // of variables is the endpoint's decision and its answer carries the names, so the copy that
        // used to spell all five out under the heading is gone - and the guard that mattered stays
        // two-sided: whatever the section does name must be something the endpoint reports, and the
        // endpoint must still report all five, read out of the code that reports them rather than out
        // of a copy of this page. TheAdminPageCarriesNoDeploymentInternals keeps every one of these
        // names off the settings half of the document entirely.
        var (markup, script) = ReadRuntimeStatusRegions();

        var reported = RuntimeStatusReportedVariables
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // The endpoint still reports exactly the five, spelled as the code that reports them spells
        // them - the settings layer's variable, the wrapper's two, the lock directory and the
        // wrapper settings document.
        Assert.Equal(5, reported.Length);
        Assert.Contains(RuntimeStatusService.JellyfinFFmpegEnvironmentVariable, reported, StringComparer.Ordinal);
        Assert.Contains(FFmpegWrapperOptions.RealFFmpegEnvironmentVariable, reported, StringComparer.Ordinal);
        Assert.Contains(FFmpegWrapperOptions.ServerFFmpegEnvironmentVariable, reported, StringComparer.Ordinal);
        Assert.Contains(TranscodeSlotStore.LockDirectoryEnvironmentVariable, reported, StringComparer.Ordinal);
        Assert.Contains(WrapperSettingsFile.EnvironmentVariable, reported, StringComparer.Ordinal);

        // And nothing the section carries is a deployment name outside that set, whether it arrived
        // with an answer or was typed into the markup by hand.
        foreach (var region in new[] { markup, script })
        {
            foreach (Match name in Regex.Matches(region, @"\b(?:ANAGLYFIN|FFMPEG|JELLYFIN)[A-Z0-9_]*\b"))
            {
                Assert.True(
                    reported.Contains(name.Value, StringComparer.Ordinal),
                    $"The runtime status section names {name.Value}, which the status endpoint does not report.");
            }
        }
    }

    [Fact]
    public void TheRuntimeStatusSectionLeavesTheSettingsExactlyAsTheyWere()
    {
        // The section reads. The proof is that nothing about the settings half moved: the same
        // transport, the same save path, and no field a save could persist. A status value that
        // looked like a setting would be written to the server by the next Save and read back as one
        // forever, which is the mistake this page has a standing guard against.
        var html = ReadPageHtml();
        var (markup, script) = ReadRuntimeStatusRegions();

        Assert.DoesNotContain("data-anaglyfin-field", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("data-anaglyfin-field", script, StringComparison.Ordinal);
        Assert.DoesNotContain("data-anaglyfin-kind", script, StringComparison.Ordinal);
        Assert.DoesNotContain("updatePluginConfiguration", script, StringComparison.Ordinal);
        Assert.DoesNotContain("getPluginConfiguration", script, StringComparison.Ordinal);
        Assert.DoesNotContain("writeConfiguration", script, StringComparison.Ordinal);
        Assert.DoesNotContain("writeProperty", script, StringComparison.Ordinal);

        foreach (var fieldName in ReadBoundFieldNames())
        {
            foreach (var forbidden in ForbiddenBoundFieldNameText)
            {
                Assert.False(
                    fieldName.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"The settings model gained a bound field named {fieldName}, which is runtime status vocabulary and must stay read-only.");
            }
        }

        // The shipped defaults, the round-trip and the layout of the form are the tests above; what
        // is checked here is that the section did not disturb the page's own boot order.
        Assert.Contains("applyShippedDefaults();", html, StringComparison.Ordinal);
        Assert.Contains("form.addEventListener('submit', saveConfiguration);", html, StringComparison.Ordinal);
        Assert.Contains("page.addEventListener('pageshow', loadConfiguration);", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdminPageStatesTheDeploymentRequirementOnce()
    {
        // Removing the internals left the page saying nothing about what the server has to be running
        // for any of this to happen, which is worse in the other direction. One blurb says it, and it
        // says it once: a page that repeated the requirement in three places would be the same page
        // that used to list the variables.
        var html = ReadPageHtml();

        Assert.Contains("Requires a Jellyfin-compatible FFmpeg-mvc build", html, StringComparison.Ordinal);
        Assert.Contains("FFmpeg entry point supplied with this plugin", html, StringComparison.Ordinal);

        Assert.Single(Regex.Matches(html, @"Installation\s+details\s+are\s+in\s+the\s+Anaglyfin\s+documentation"));
        Assert.Single(Regex.Matches(html, @"Requires\s+a\s+Jellyfin-compatible\s+FFmpeg-mvc\s+build"));
    }

    [Fact]
    public void TheAdminPageStaysOutOfCachingUntilItIsDesigned()
    {
        // Caching is explicitly not part of this product yet. A setting appearing here
        // before a cache model exists would promise a feature nothing implements.
        Assert.DoesNotContain("cach", ReadPageHtml(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the page markup out of the plugin assembly: the same stream the dashboard gets.
    /// </summary>
    private static string ReadPageHtml()
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(ConfigurationPage.HtmlResourceName)
            ?? throw new FileNotFoundException(
                $"The admin page '{ConfigurationPage.HtmlResourceName}' is not embedded in the plugin assembly.");

        using (stream)
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    /// <summary>
    /// Reads the two marked runtime status regions out of the page: the markup, then the script.
    /// </summary>
    /// <remarks>
    /// There have to be exactly two, because the guard against the deployment's names is applied to
    /// everything outside them: a section that lost its markers would be excused from that guard
    /// while still carrying the names, which is the one way this arrangement could fail quietly.
    /// </remarks>
    private static (string Markup, string Script) ReadRuntimeStatusRegions()
    {
        var regions = RuntimeStatusRegions(ReadPageHtml());

        Assert.Equal(2, regions.Count);

        return (regions[0], regions[1]);
    }

    /// <summary>
    /// Reads the page with the runtime status regions taken out: the settings half of the document,
    /// which still has to carry none of the names a deployment is arranged with.
    /// </summary>
    private static (string Outside, List<string> Regions) ReadPageWithoutRuntimeStatus()
    {
        var html = ReadPageHtml();
        var regions = RuntimeStatusRegions(html);

        Assert.Equal(2, regions.Count);

        var outside = Regex.Replace(
            html,
            Regex.Escape(RuntimeStatusRegionStart) + @"[\s\S]*?" + Regex.Escape(RuntimeStatusRegionEnd),
            string.Empty);

        return (outside, regions);
    }

    private static List<string> RuntimeStatusRegions(string html)
        => Regex.Matches(
                html,
                Regex.Escape(RuntimeStatusRegionStart) + @"(?<region>[\s\S]*?)" + Regex.Escape(RuntimeStatusRegionEnd))
            .Cast<Match>()
            .Select(match => match.Groups["region"].Value)
            .ToList();

    /// <summary>
    /// The colour the page owes one reported state: green for the state that passed, amber for the
    /// state that is present but has to be looked at, and red for every state that costs an Anaglyfin
    /// command something - Unknown included, because a state the plugin could not observe is not a
    /// state that passed.
    /// </summary>
    /// <remarks>
    /// Written against <see cref="RuntimeItemState"/> rather than against a list, so the mapping is
    /// checked for a state this enumeration gains without anyone remembering to add it to a test.
    /// </remarks>
    private static string ExpectedRuntimeStatusRowClass(RuntimeItemState state)
        => state switch
        {
            RuntimeItemState.Ok => RuntimeStatusRowOkClass,
            RuntimeItemState.Warning => RuntimeStatusRowWarningClass,
            _ => RuntimeStatusRowErrorClass
        };

    /// <summary>
    /// Reads one runtime table out of the section markup by its caption, from its <c>table</c> tag to
    /// its close, so a test can ask what that table alone declares rather than what the section
    /// somewhere contains.
    /// </summary>
    private static string RuntimeStatusTable(string markup, string caption)
    {
        var captioned = markup.IndexOf($"<caption>{caption}</caption>", StringComparison.Ordinal);
        Assert.True(captioned >= 0, $"The runtime status section no longer carries a table captioned {caption}.");

        var opened = markup.LastIndexOf("<table", captioned, StringComparison.Ordinal);
        var closed = markup.IndexOf("</table>", captioned, StringComparison.Ordinal);

        Assert.True(
            opened >= 0 && closed > captioned,
            $"The table captioned {caption} is not a table of its own.");

        return markup[opened..(closed + "</table>".Length)];
    }

    /// <summary>
    /// Reads the column headings of one table, in the order they are declared, so a column can only
    /// be present as a heading of that table and cannot be smuggled in somewhere else.
    /// </summary>
    private static string[] ReadColumnHeadings(string table)
        => Regex.Matches(table, @"<th[^>]*\bscope=""col"">\s*(?<text>[^<]*?)\s*</th>")
            .Cast<Match>()
            .Select(match => match.Groups["text"].Value)
            .ToArray();

    /// <summary>
    /// Reads the class of every <c>col</c> of one table's <c>colgroup</c>, in order: the pair of
    /// classes two tables share is what puts their columns above each other, so the list is compared
    /// as a sequence rather than as a set.
    /// </summary>
    private static string[] ReadColumnClasses(string table)
    {
        var group = Regex.Match(table, @"<colgroup>(?<body>[\s\S]*?)</colgroup>");

        Assert.True(
            group.Success && group.Groups["body"].Value.IndexOf("<table", StringComparison.Ordinal) < 0,
            "A runtime status table no longer declares its columns with a colgroup.");

        return Regex.Matches(group.Groups["body"].Value, "<col\\b[^>]*\\bclass=\"(?<classes>[^\"]*)\"")
            .Cast<Match>()
            .Select(match => match.Groups["classes"].Value.Trim())
            .ToArray();
    }

    /// <summary>
    /// Reads the declarations of one stylesheet rule of the page, so a test can pin how the page is
    /// drawn without pinning the sheet around it.
    /// </summary>
    /// <param name="html">The whole page document.</param>
    /// <param name="selector">The rule's selector, as a regular expression.</param>
    private static string StyleRule(string html, string selector)
    {
        var rule = Regex.Match(html, selector + @"\s*\{(?<body>[^}]*)\}");

        Assert.True(rule.Success, $"The admin page stylesheet no longer carries a rule for {selector}.");

        return rule.Groups["body"].Value;
    }

    /// <summary>
    /// Reads the copy the runtime status section puts under its own heading, tags taken out: the one
    /// line the section is allowed as its own introduction, weighed by length rather than by whether
    /// it happens to name the right things.
    /// </summary>
    private static string RuntimeStatusHeadingCopy(string markup)
    {
        const string Heading = "<h2 class=\"sectionTitle\">Runtime status</h2>";

        var heading = markup.IndexOf(Heading, StringComparison.Ordinal);
        Assert.True(heading >= 0, "The runtime status section no longer opens with its heading.");

        var opened = markup.IndexOf("<div class=\"fieldDescription\"", heading + Heading.Length, StringComparison.Ordinal);
        Assert.True(opened > heading, "The runtime status heading has no copy under it at all.");

        var closed = markup.IndexOf("</div>", opened, StringComparison.Ordinal);
        Assert.True(closed > opened, "The copy under the runtime status heading is never closed.");

        return string.Join(
            ' ',
            Regex.Replace(markup[opened..closed], "<[^>]*>", " ").Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Reads the values one row builder hands to <c>runtimeStatusRow</c>, split at its top-level
    /// commas so a test can count the columns a row is built with and name each one. A split on
    /// every comma would cut <c>readProperty(entry, 'Name')</c> in two, which is the only reason this
    /// has to track brackets and quotes at all.
    /// </summary>
    private static string[] RowValueExpressions(string rowBuilderBody)
    {
        var values = Regex.Match(rowBuilderBody, @"runtimeStatusRow\(\s*\[(?<entries>[\s\S]*?)\]\s*,");

        Assert.True(
            values.Success,
            "The runtime status row builder no longer builds its row from a list of values and a state class.");

        var entries = values.Groups["entries"].Value;
        var parts = new List<string>();
        var start = 0;
        var depth = 0;
        var quote = '\0';

        for (var i = 0; i < entries.Length; i++)
        {
            var character = entries[i];

            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character is '(' or '[')
            {
                depth++;
            }
            else if (character is ')' or ']')
            {
                depth--;
            }
            else if (character == ',' && depth == 0)
            {
                parts.Add(entries[start..i].Trim());
                start = i + 1;
            }
        }

        parts.Add(entries[start..].Trim());

        return parts.Where(part => part.Length > 0).ToArray();
    }

    /// <summary>
    /// Asserts the page asks the dashboard client only through calls that client actually answers
    /// to, so that the runtime status section cannot go back to reporting a signed-in dashboard as a
    /// page without a client.
    /// </summary>
    /// <remarks>
    /// The dashboard's ApiClient answers to <c>getUrl</c> and <c>getJSON</c>; it carries no
    /// <c>request</c> at all, so a page that asked for one found no client on any dashboard, and it
    /// is the availability test - not the server - that said so. The check is by shape rather than by
    /// name alone, because the same mistake spelled as <c>window.ApiClient['request']</c> would be
    /// the same mistake.
    /// </remarks>
    private static void AssertPageCarriesNoClientRequestCall(string html)
    {
        Assert.DoesNotContain("ApiClient.request", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiClient['request']", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiClient[\"request\"]", html, StringComparison.Ordinal);

        Assert.False(
            Regex.IsMatch(html, @"\.\s*request\b"),
            "The admin page asks the dashboard client through a request method the dashboard client does not answer to.");
    }

    /// <summary>
    /// Reads one of the page script's own string lists, so a test can compare what the page shows
    /// against what it owes the administrator without matching a declaration character for character.
    /// </summary>
    private static string[] ReadScriptList(string script, string name)
    {
        var declaration = Regex.Match(
            script,
            @"var " + Regex.Escape(name) + @" = \[(?<entries>[^\]]*)\];");

        Assert.True(declaration.Success, $"The runtime status script no longer declares {name}.");

        return Regex.Matches(declaration.Groups["entries"].Value, "'(?<entry>[^']*)'")
            .Cast<Match>()
            .Select(match => match.Groups["entry"].Value)
            .ToArray();
    }

    private static List<(string Id, string DisplayName, bool EnabledByDefault)> ReadProfileList()
    {
        var declaration = Regex.Match(ReadPageHtml(), @"var AnaglyfinProfiles = (?<list>\[[^\]]*\]);");
        Assert.True(declaration.Success, "The admin page no longer declares its profile list as AnaglyfinProfiles.");

        var listed = new List<(string, string, bool)>();
        foreach (var profile in JsonNode.Parse(declaration.Groups["list"].Value)!.AsArray())
        {
            listed.Add((
                profile!["id"]!.GetValue<string>(),
                profile["name"]!.GetValue<string>(),
                profile["enabledByDefault"]!.GetValue<bool>()));
        }

        return listed;
    }

    private static List<string> ReadBoundFieldNames()
    {
        var names = new List<string>();

        foreach (Match match in Regex.Matches(ReadPageHtml(), "data-anaglyfin-field=\"(?<name>[^\"]+)\""))
        {
            var name = match.Groups["name"].Value;
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static List<(string Key, string Value)> ReadFieldAttributes(string attributeName)
    {
        var pairs = new List<(string, string)>();

        foreach (var editable in ReadEditableTags(ReadPageHtml()))
        {
            var attributes = ReadAttributes(editable);
            if (attributes.TryGetValue("data-anaglyfin-field", out var fieldName)
                && attributes.TryGetValue(attributeName, out var attributeValue))
            {
                pairs.Add((fieldName, attributeValue));
            }
        }

        return pairs;
    }

    private static string ReadFieldTag(string fieldName)
    {
        var tag = ReadEditableTags(ReadPageHtml())
            .SingleOrDefault(candidate => ReadAttributes(candidate).GetValueOrDefault("data-anaglyfin-field") == fieldName);

        Assert.True(tag is not null, $"The admin page has no editable element bound to {fieldName}.");
        return tag!;
    }

    /// <summary>
    /// Reads the whole checkbox field a bound switch sits in, from the dashboard's checkbox container
    /// down to and including the help text beside the label.
    /// </summary>
    /// <remarks>
    /// The container and not just the input, because the container is the element the dashboard's
    /// stylesheet lays the box and its help text out against - and because the question this answers
    /// ("is the description beside the label, or inside it?") cannot be asked of either half alone.
    /// The div is read by matching its own close against the closes of whatever it opens inside
    /// itself, so nothing here has to know how many elements a checkbox field carries.
    /// </remarks>
    private static string CheckboxContainerContaining(string html, string fieldName)
    {
        var field = html.IndexOf($"data-anaglyfin-field=\"{fieldName}\"", StringComparison.Ordinal);
        Assert.True(field >= 0, $"The admin page has no field named {fieldName}.");

        var opened = html.LastIndexOf("<div", field, StringComparison.Ordinal);

        Assert.True(opened >= 0, $"The {fieldName} switch is not inside a checkbox container of its own.");

        var containerTag = html[opened..(html.IndexOf('>', opened) + 1)];
        Assert.Contains("checkboxContainer", containerTag, StringComparison.Ordinal);

        var depth = 0;
        var cursor = opened;

        while (true)
        {
            var nextOpen = html.IndexOf("<div", cursor + "<div".Length, StringComparison.Ordinal);
            var nextClose = html.IndexOf("</div>", cursor + "</div>".Length, StringComparison.Ordinal);

            Assert.True(nextClose > 0, $"The checkbox container of {fieldName} is never closed.");

            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                depth++;
                cursor = nextOpen;
                continue;
            }

            if (depth == 0)
            {
                return html[opened..(nextClose + "</div>".Length)];
            }

            depth--;
            cursor = nextClose;
        }
    }

    /// <summary>
    /// Reads the checkbox label a bound switch sits in: the label, its input, and the caption the
    /// dashboard's upgrade takes from it - and nothing else, which is the point of reading it on its
    /// own.
    /// </summary>
    /// <remarks>
    /// The label is the element the dashboard adopts and lays out as one checkbox, so what is inside
    /// it is what the administrator can click to toggle the switch. Anything that belongs to the
    /// field but not to the switch - the help text above all - has to be read outside it.
    /// </remarks>
    private static string CheckboxLabelContaining(string html, string fieldName)
    {
        var field = html.IndexOf($"data-anaglyfin-field=\"{fieldName}\"", StringComparison.Ordinal);
        Assert.True(field >= 0, $"The admin page has no field named {fieldName}.");

        var opened = html.LastIndexOf("<label", field, StringComparison.Ordinal);
        var closed = html.IndexOf("</label>", field, StringComparison.Ordinal);

        Assert.True(
            opened >= 0 && closed > field,
            $"The {fieldName} switch is not inside a checkbox label of its own.");

        return html[opened..(closed + "</label>".Length)];
    }

    /// <summary>
    /// Reads the help text of a bound switch: the description element the dashboard lays out under
    /// the checkbox, in its own element rather than inside the label.
    /// </summary>
    private static string CheckboxDescriptionContaining(string html, string fieldName)
    {
        var field = html.IndexOf($"data-anaglyfin-field=\"{fieldName}\"", StringComparison.Ordinal);
        Assert.True(field >= 0, $"The admin page has no field named {fieldName}.");

        var labelClosed = html.IndexOf("</label>", field, StringComparison.Ordinal);
        Assert.True(labelClosed > 0, $"The {fieldName} switch is not inside a checkbox label of its own.");

        var opened = html.IndexOf("<div class=\"fieldDescription", labelClosed + "</label>".Length, StringComparison.Ordinal);

        Assert.True(
            opened > labelClosed,
            $"The {fieldName} switch has no help text beside its label.");

        var closed = html.IndexOf("</div>", opened, StringComparison.Ordinal);
        Assert.True(closed > opened, $"The help text of the {fieldName} switch is never closed.");

        return html[opened..(closed + "</div>".Length)];
    }

    private static List<(string Value, string Text)> ReadOptionValues(string fieldName)
    {
        var html = ReadPageHtml();
        var field = $"data-anaglyfin-field=\"{fieldName}\"";

        var declared = html.IndexOf(field, StringComparison.Ordinal);
        Assert.True(declared >= 0, $"The admin page has no field named {fieldName}.");

        var opened = html.IndexOf('>', declared);
        var closed = html.IndexOf("</select>", opened, StringComparison.Ordinal);
        Assert.True(opened > 0 && closed > opened, $"The {fieldName} field is not a select carrying its own options.");

        var options = new List<(string, string)>();
        foreach (Match match in Regex.Matches(html[(opened + 1)..closed], "<option[^>]*value=\"(?<value>[^\"]*)\"[^>]*>(?<text>[^<]*)</option>"))
        {
            options.Add((match.Groups["value"].Value, match.Groups["text"].Value.Trim()));
        }

        return options;
    }

    private static IEnumerable<string> ReadEditableTags(string html)
        => Regex.Matches(html, "<(?<tag>input|select|textarea)\\b(?<attributes>[^>]*)>")
            .Cast<Match>()
            .Select(match => match.Value);

    private static Dictionary<string, string> ReadAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Regex.Matches(tag, "(?<name>[a-zA-Z][a-zA-Z0-9-]*)\\s*=\\s*\"(?<value>[^\"]*)\""))
        {
            attributes[match.Groups["name"].Value] = match.Groups["value"].Value;
        }

        return attributes;
    }

    private static string TagName(string tag)
        => Regex.Match(tag, "^<(?<tag>[a-zA-Z]+)").Groups["tag"].Value;

    private static bool IsColourField(string fieldName)
        => string.Equals(fieldName, nameof(PluginConfiguration.CustomLeftEyeColor), StringComparison.Ordinal)
            || string.Equals(fieldName, nameof(PluginConfiguration.CustomRightEyeColor), StringComparison.Ordinal);

    private static IEnumerable<string> SettingsPropertyNames()
        => typeof(PluginConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite && property.DeclaringType == typeof(PluginConfiguration))
            .Select(property => property.Name);

    private static PropertyInfo SettingsProperty(string fieldName)
        => typeof(PluginConfiguration).GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"The admin page binds {fieldName}, which the settings model does not have.");

    /// <summary>
    /// Builds the JSON a browser saves from this page: one property per field the page binds,
    /// each named the way that setting is named on the settings model, which is the only spelling
    /// the settings endpoint binds.
    /// </summary>
    private static string BuildPayloadThePageSends()
    {
        var payload = new JsonObject();

        foreach (var fieldName in ReadBoundFieldNames())
        {
            payload[fieldName] = PayloadValueFor(fieldName, SettingsProperty(fieldName).PropertyType);
        }

        return payload.ToJsonString();
    }

    private static JsonNode PayloadValueFor(string fieldName, Type propertyType)
    {
        if (propertyType == typeof(string))
        {
            return JsonValue.Create(StringPayloadFor(fieldName))!;
        }

        if (propertyType == typeof(int))
        {
            return JsonValue.Create(3)!;
        }

        if (propertyType == typeof(bool))
        {
            // The opposite of whatever the model ships, which is the one answer that makes a
            // missed binding visible either way: a switch the endpoint did not bind comes back as
            // the shipped default, and a payload that happened to send that default would let it
            // pass unnoticed. Ticking a box that ships ticked proves nothing about the box.
            var shipped = SettingsProperty(fieldName).GetValue(new PluginConfiguration())
                ?? throw new InvalidOperationException($"The default of {nameof(PluginConfiguration)}.{fieldName} is null.");

            return JsonValue.Create(!(bool)shipped)!;
        }

        if (propertyType.IsEnum)
        {
            // The settings endpoint spells an enum as its name, which is what the page sends.
            return JsonValue.Create(Enum.GetNames(propertyType)[^1])!;
        }

        if (propertyType == typeof(List<string>))
        {
            return new JsonArray { JsonValue.Create("sbs_full"), JsonValue.Create("two_d_base") };
        }

        throw new InvalidOperationException(
            $"The settings model gained a {propertyType.Name} setting ({fieldName}) that this test cannot send yet. "
            + "Teach this test the shape so the admin page contract stays covered.");
    }

    /// <summary>
    /// Picks a value for a string setting that differs from the shipped default, so that a
    /// setting the payload failed to bind is visible as a failure.
    /// </summary>
    private static string StringPayloadFor(string fieldName)
        => fieldName switch
        {
            nameof(PluginConfiguration.DefaultProfileId) => "sbs_half",
            nameof(PluginConfiguration.CustomLeftEyeColor) => "#00FF00",
            nameof(PluginConfiguration.CustomRightEyeColor) => "#0000FF",
            _ => ProfileIds.TwoDBase
        };

    private static string AsJson(object? value)
        => JsonSerializer.Serialize(value ?? "null");

    /// <summary>
    /// Reads the body of one function of the page script, so a test can pin what that function
    /// does to a saved setting without pinning the whole script around it.
    /// </summary>
    private static string FunctionBody(string html, string functionName)
    {
        // Every function of the page script opens and closes at the same indentation, and a
        // block nested inside one closes deeper than that, so the first brace back at the
        // function's own indentation is the end of its body.
        var body = Regex.Match(
            html,
            @"function " + Regex.Escape(functionName) + @"\([^)]*\)\s*\{(?<body>[\s\S]*?)\n {16}\}");

        Assert.True(body.Success, $"The admin page script no longer declares {functionName} where its tests can find it.");

        return body.Groups["body"].Value;
    }
}

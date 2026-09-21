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
using Anaglyfin.Profiles;
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
    /// Spellings the page document may not carry at all: the names a deployment is arranged with.
    /// None of them is editable from this page, so naming one invites an edit that cannot be made -
    /// and each belongs to the install guide, where the person setting it up actually is.
    /// </summary>
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

        // Compared as a set: the page lists profiles in catalog order while the shipped set
        // is declared in preference order, and the profile catalog resolves either way to
        // the catalog order it offers clients in.
        Assert.Equal(
            ProfileIds.DefaultEnabledProfileIds.OrderBy(id => id, StringComparer.Ordinal),
            enabledByDefault.OrderBy(id => id, StringComparer.Ordinal));
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

        // The dashboard's checkbox shape, for the same reason the profile boxes use it: the
        // upgrade to emby-checkbox happens while the element is parsed and adopts the first span
        // of the label as the caption. A box outside that shape is a bare browser checkbox with
        // nothing beside it.
        var label = CheckboxLabelContaining(html, nameof(PluginConfiguration.OfferOriginalMVCVersion));

        Assert.Matches(@"<label\b[^>]*checkboxContainer[^>]*>", label);
        Assert.Contains("<span>Offer original 3D MVC version</span>", label, StringComparison.Ordinal);

        // What the help text owes the administrator: that only the raw file is hidden, that the
        // converted versions are what is left, that the picker is where it is hidden from, and
        // that turning it back is reversible. Nothing here promises a deletion, and nothing here
        // can, because the setting does not delete anything.
        Assert.Contains("version pickers", label, StringComparison.Ordinal);
        Assert.Contains("converted versions", label, StringComparison.Ordinal);
        Assert.Contains("Nothing is deleted", label, StringComparison.Ordinal);
        Assert.Contains("turning it back on", label, StringComparison.Ordinal);

        // And what it must not promise: no scan, no rescan, no relink - the words of every lever
        // that would have had to touch the library.
        Assert.DoesNotContain("scan the library", label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rescan", label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unlink", label, StringComparison.OrdinalIgnoreCase);
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
        var html = ReadPageHtml();

        Assert.DoesNotContain("wrapper", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environment", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("variable", html, StringComparison.OrdinalIgnoreCase);

        foreach (var internalName in ForbiddenPageText)
        {
            Assert.DoesNotContain(internalName, html, StringComparison.OrdinalIgnoreCase);
        }
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
    /// Reads the whole of the checkbox element a bound switch sits inside: the label, its input,
    /// the caption the dashboard takes from it and the description beside them.
    /// </summary>
    /// <remarks>
    /// The element and not just the input, because the element is what the dashboard upgrades and
    /// what the administrator reads - and the input alone can be a perfectly bound checkbox with
    /// nothing anywhere to say what it switches.
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

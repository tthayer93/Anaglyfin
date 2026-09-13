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
/// against the model it is the only interface to: a profile added to the catalog, a setting
/// added to the model or an environment variable renamed by the wrapper fails here instead
/// of silently disappearing from, or silently appearing on, the UI.
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
        Assert.Contains(nameof(PluginConfiguration.FallbackProfileId), displayed.Keys);
        Assert.Contains(nameof(PluginConfiguration.EncoderPolicy), displayed.Keys);
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
        // The page sends one camelCase property per bound field, which is the shape the
        // settings endpoint binds. Round-tripping that payload proves the names on the page
        // are the names on the model and that every shape is one the endpoint can read: the
        // two halves of a contract a browser would otherwise reveal by silently dropping a
        // setting on save.
        var payload = BuildPayloadThePageSends();

        var reloaded = JsonSerializer.Deserialize<PluginConfiguration>(
            payload,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            });

        var configuration = Assert.IsAssignableFrom<PluginConfiguration>(reloaded);

        Assert.Equal("sbs_half", configuration.DefaultProfileId);
        Assert.Equal("sbs_full", configuration.FallbackProfileId);
        Assert.Equal(3, configuration.MaxConcurrentTranscodes);
        Assert.Equal(VideoEncoderPolicy.SoftwareOnly, configuration.EncoderPolicy);
        Assert.Equal("#00FF00", configuration.CustomLeftEyeColor);
        Assert.Equal("#0000FF", configuration.CustomRightEyeColor);
        Assert.Equal(new[] { "sbs_full", "two_d_base" }, configuration.EnabledProfileIds);

        var deviceDefault = Assert.Single(configuration.DeviceDefaultProfiles);
        Assert.Equal("living-room-tv", deviceDefault.DeviceId);
        Assert.Equal("AndroidTV", deviceDefault.ClientName);
        Assert.Equal("custom_grayscale", deviceDefault.ProfileId);

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
    public void TheEncoderPolicyChoicesAreThePolicyEnumeration()
    {
        var options = ReadOptionValues(nameof(PluginConfiguration.EncoderPolicy));

        Assert.Equal(Enum.GetNames<VideoEncoderPolicy>(), options.Select(option => option.Value).ToArray());
        Assert.All(options, option => Assert.NotEmpty(option.Text));
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

                Assert.Contains(type, new[] { "checkbox", "color", "number", "text" }, StringComparer.OrdinalIgnoreCase);

                if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    // The only free text on the page identifies a device or a client, which
                    // the settings model matches by name and never executes.
                    Assert.True(
                        attributes.ContainsKey("data-anaglyfin-device-field"),
                        $"A free text field appeared on the admin page: {editable}");

                    Assert.True(
                        int.TryParse(attributes.GetValueOrDefault("maxlength"), out var maxLength) && maxLength is > 0 and <= 256,
                        "The device and client identification fields have to stay short.");
                }
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
    public void TheAdminPageExplainsTheWrapperEnvironmentItCannotWrite()
    {
        var html = ReadPageHtml();

        // Naming the variables the wrapper reads is the whole of the page's deployment
        // guidance: the wrapper is started by the server and cannot see plugin settings.
        Assert.Contains(FFmpegWrapperOptions.RealFFmpegEnvironmentVariable, html, StringComparison.Ordinal);
        Assert.Contains(FFmpegWrapperOptions.RealFFmpegAlternateEnvironmentVariable, html, StringComparison.Ordinal);
        Assert.Contains(FFmpegWrapperOptions.MaxConcurrentTranscodesEnvironmentVariable, html, StringComparison.Ordinal);
        Assert.Contains(FFmpegWrapperOptions.LockDirectoryEnvironmentVariable, html, StringComparison.Ordinal);
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
    /// Builds the JSON a browser saves from this page: one camelCase property per field the
    /// page binds, each spelled the way that field's setting is stored.
    /// </summary>
    private static string BuildPayloadThePageSends()
    {
        var payload = new JsonObject();

        foreach (var fieldName in ReadBoundFieldNames())
        {
            payload[CamelCase(fieldName)] = PayloadValueFor(fieldName, SettingsProperty(fieldName).PropertyType);
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

        if (propertyType.IsEnum)
        {
            // The settings endpoint spells an enum as its name, which is what the page sends.
            return JsonValue.Create(Enum.GetNames(propertyType)[^1])!;
        }

        if (propertyType == typeof(List<string>))
        {
            return new JsonArray { JsonValue.Create("sbs_full"), JsonValue.Create("two_d_base") };
        }

        if (propertyType == typeof(List<DeviceProfileDefault>))
        {
            return new JsonArray(new JsonObject
            {
                ["deviceId"] = "living-room-tv",
                ["clientName"] = "AndroidTV",
                ["profileId"] = "custom_grayscale"
            });
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
            nameof(PluginConfiguration.FallbackProfileId) => "sbs_full",
            nameof(PluginConfiguration.CustomLeftEyeColor) => "#00FF00",
            nameof(PluginConfiguration.CustomRightEyeColor) => "#0000FF",
            _ => ProfileIds.TwoDBase
        };

    private static string AsJson(object? value)
        => JsonSerializer.Serialize(value ?? "null");

    private static string CamelCase(string name)
        => name.Length == 0
            ? name
            : char.ToLowerInvariant(name[0]) + name[1..];
}

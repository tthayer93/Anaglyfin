using System;
using System.Xml;
using System.Xml.Linq;

namespace Anaglyfin.Configuration;

/// <summary>
/// Reads the one thing a settings file can say that this build's settings model can no longer
/// store, and turns it into the mode the model should hold.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The subtitle depth used to be a switch and a mode: a
/// <c>SubtitleDepthEnabled</c> box and one of <c>Automatic</c>, <c>ConstantShift</c> or
/// <c>Plane</c>. The switch is gone - <see cref="SubtitleDepthMode.Flat"/> is now the position
/// that asks for nothing - but a settings file written before that change still has the switch
/// in it, and the settings model no longer has anywhere to put it. Loading such a file through
/// the ordinary serialiser drops <c>SubtitleDepthEnabled</c> on the floor and keeps
/// <c>SubtitleDepthMode</c>, so a server whose depth was switched <i>off</i> and whose mode was
/// left on <c>Automatic</c> would read as an upgrade that quietly turns depth on. Nothing in
/// the stored model tells the two apart; only the stored <i>file</i> still does.
/// </para>
/// <para>
/// <b>What it decides, from the stored file alone.</b> A legacy switch present and on keeps the
/// reading stored beside it. A legacy switch present and off, or a file that predates the depth
/// feature entirely (no switch and no mode), becomes <see cref="SubtitleDepthMode.Flat"/> - an
/// upgrade must not start moving captions nobody asked it to. And a file already written by this
/// build states a mode with no legacy switch, which is left exactly as it is: this decides
/// nothing about settings that have nothing legacy in them.
/// </para>
/// <para>
/// It returns the mode to store rather than editing a settings object, so the decision is a
/// total function over the one input that carries the information, and it is testable without a
/// settings file, a plugin, or a server. The caller (<see cref="Plugin"/>) applies it to the
/// live settings and persists once, on load, when it has a settings file to read.
/// </para>
/// </remarks>
public static class PluginConfigurationMigration
{
    private const string SettingsRootName = "PluginConfiguration";
    private const string SubtitleDepthEnabledElement = "SubtitleDepthEnabled";
    private const string SubtitleDepthModeElement = "SubtitleDepthMode";

    /// <summary>
    /// Reads the subtitle depth mode a stored settings file asks the new model to hold.
    /// </summary>
    /// <param name="storedXml">The stored settings text.</param>
    /// <returns>
    /// The mode to store when the file carries a legacy switch or predates the depth feature,
    /// and <c>null</c> when it is already written in the current schema - or when it is not a
    /// settings file this build can read at all, which is never a reason to change anything.
    /// </returns>
    public static SubtitleDepthMode? ResolveStoredSubtitleDepthMode(string? storedXml)
    {
        if (string.IsNullOrWhiteSpace(storedXml))
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(storedXml);
        }
        catch (XmlException)
        {
            // Not a settings file this build can read: nothing to migrate, and the ordinary
            // settings load is the place that decides what an unreadable file means.
            return null;
        }

        var root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, SettingsRootName, StringComparison.Ordinal))
        {
            return null;
        }

        var enabled = root.Element(SubtitleDepthEnabledElement);
        var mode = root.Element(SubtitleDepthModeElement);

        // No legacy switch on the file. A file this build wrote always states a mode and carries
        // no switch, so that shape is the current schema and is left exactly as it is. A file
        // with neither the switch nor a mode predates the feature entirely: it never asked for
        // depth, and the mode that asks for nothing is the one it should land on.
        if (enabled is null)
        {
            return mode is null ? SubtitleDepthMode.Flat : (SubtitleDepthMode?)null;
        }

        // A legacy switch is present, so this is the old depth schema. Switched off - or switched
        // to something nobody can read - is the position that asks for nothing.
        if (!IsTrue(enabled.Value))
        {
            return SubtitleDepthMode.Flat;
        }

        // Switched on: keep the reading that was stored beside the switch. A mode name this build
        // does not recognise reads back as the shipped default it would load as anyway.
        return TryReadModeName(mode?.Value, out var stored) ? stored : SubtitleDepthMode.Automatic;
    }

    /// <summary>
    /// Whether a legacy switch, spelled the way either serialiser spells it, is on.
    /// </summary>
    private static bool IsTrue(string? value)
    {
        var stated = value?.Trim();

        return string.Equals(stated, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(stated, "1", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads a stored mode name, refusing an ordinal or a name no declaration carries.
    /// </summary>
    private static bool TryReadModeName(string? value, out SubtitleDepthMode mode)
    {
        mode = default;

        var name = value?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (!Enum.TryParse(name, ignoreCase: true, out mode))
        {
            return false;
        }

        // Enum.TryParse accepts a number no member declares; a stored mode has to name one, so a
        // future-build ordinal reads as "no mode" rather than as a member it is not.
        if (!Enum.IsDefined(mode))
        {
            mode = default;
            return false;
        }

        return true;
    }
}

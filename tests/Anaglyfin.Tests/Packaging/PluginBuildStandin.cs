using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Anaglyfin.Tests.Packaging;

/// <summary>
/// A real plugin build, as a packaging test can point at one.
/// </summary>
/// <remarks>
/// <para>
/// The packager refuses a DLL whose assembly name, assembly version, or embedded identity
/// manifest disagrees with the manifest file it is being packaged with, so the inputs a test
/// needs have to be a genuine build. <c>Anaglyfin.dll</c> is already next to the tests - the
/// test project references the plugin for its own assertions - and the manifest it carries is
/// the same document the packaging job is meant to be handed. Copying both into a throwaway
/// directory therefore describes the build the CI step will hand the job, without the test
/// having to know where it is running.
/// </para>
/// <para>
/// A refusal is then a single field of that manifest changed, which is what makes each test
/// below name the one thing that went wrong.
/// </para>
/// </remarks>
internal static class PluginBuildStandin
{
    /// <summary>The manifest resource the plugin project embeds.</summary>
    internal const string ManifestResourceName = "Anaglyfin.Plugin.manifest.xml";

    /// <summary>Gets the path of the plugin assembly the tests were built beside.</summary>
    internal static string AssemblyPath => typeof(Plugin).Assembly.Location;

    /// <summary>
    /// Reads the identity manifest the plugin assembly carries.
    /// </summary>
    /// <returns>The manifest text a build produced.</returns>
    internal static string ManifestText()
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(ManifestResourceName)
            ?? throw new FileNotFoundException(
                $"The plugin assembly does not carry '{ManifestResourceName}'",
                ManifestResourceName);

        using (stream)
        {
            using var reader = new StreamReader(stream);

            return reader.ReadToEnd();
        }
    }

    /// <summary>
    /// Copies the plugin assembly into a test directory under the name the manifest declares.
    /// </summary>
    /// <param name="directory">The directory to file it in.</param>
    /// <param name="name">The file name, when a test wants a different one.</param>
    /// <returns>The copied path.</returns>
    internal static string CopyAssemblyInto(TemporaryPackageDirectory directory, string? name = null)
        => directory.CopyIn(AssemblyPath, name ?? "Anaglyfin.dll");

    /// <summary>
    /// Writes the carried manifest, with one element replaced.
    /// </summary>
    /// <param name="directory">The directory to write it into.</param>
    /// <param name="field">The manifest element to change.</param>
    /// <param name="value">The text to declare instead, or empty to drop the element.</param>
    /// <returns>The written path.</returns>
    internal static string WriteManifestWith(TemporaryPackageDirectory directory, string field, string value)
        => directory.WriteText("Plugin.manifest.xml", ManifestTextWith(field, value));

    /// <summary>
    /// Replaces one element of the carried manifest.
    /// </summary>
    /// <param name="field">The manifest element to change.</param>
    /// <param name="value">The text to declare instead, or empty to drop the element.</param>
    /// <returns>The manifest text.</returns>
    internal static string ManifestTextWith(string field, string value)
    {
        if (!Regex.IsMatch(field, @"^[A-Za-z][A-Za-z0-9]*$"))
        {
            throw new InvalidOperationException($"'{field}' is not a manifest element name.");
        }

        var replacement = value.Length == 0
            ? string.Empty
            : $"<{field}>{value}</{field}>";

        var text = ManifestText();
        var replaced = Regex.Replace(text, $@"<{field}>[^<]*</{field}>", replacement);

        if (replaced == text)
        {
            throw new InvalidOperationException($"The identity manifest carries no <{field}> element to change.");
        }

        return replaced;
    }
}

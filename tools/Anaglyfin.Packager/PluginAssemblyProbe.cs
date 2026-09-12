using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Xml;

namespace Anaglyfin.Packager;

/// <summary>
/// What a built plugin assembly says about itself: its assembly name and version, and the
/// identity manifest it carries inside it.
/// </summary>
/// <remarks>
/// <para>
/// A plugin is installed by name and loaded by identifier, so the two facts that decide
/// whether an archive is installable are both properties of the DLL rather than of the build
/// that produced it. Reading them from the assembly - instead of trusting the file name the
/// build happened to leave behind - is what lets the job refuse a DLL that was not built from
/// the manifest it is being packaged with.
/// </para>
/// <para>
/// The identity manifest is embedded by the plugin project (see <c>Anaglyfin.csproj</c>), so a
/// DLL packaged with a manifest it does not carry is a stale build or a swapped artifact, and
/// neither belongs on a server.
/// </para>
/// </remarks>
public sealed class PluginAssemblyProbe
{
    /// <summary>The suffix the plugin's embedded identity manifest is recognised by.</summary>
    public const string ManifestResourceSuffix = ".manifest.xml";

    private PluginAssemblyProbe(string path, string simpleName, Version? version, PluginManifest? embeddedManifest)
    {
        AssemblyPath = path;
        SimpleName = simpleName;
        Version = version;
        EmbeddedManifest = embeddedManifest;
    }

    /// <summary>Gets the assembly path the probe read.</summary>
    public string AssemblyPath { get; }

    /// <summary>Gets the assembly's own name, without extension.</summary>
    public string SimpleName { get; }

    /// <summary>Gets the assembly version, or <see langword="null"/> when the assembly declares none.</summary>
    public Version? Version { get; }

    /// <summary>
    /// Gets the identity manifest carried inside the assembly, or <see langword="null"/> when
    /// the assembly carries none.
    /// </summary>
    public PluginManifest? EmbeddedManifest { get; }

    /// <summary>Gets the assembly file name the server will look for.</summary>
    public string AssemblyFileName => SimpleName + ".dll";

    /// <summary>
    /// Reads a built plugin assembly without running any of its code.
    /// </summary>
    /// <param name="path">The assembly to inspect.</param>
    /// <returns>What the assembly declares about itself.</returns>
    /// <exception cref="PackagingError">
    /// The file is missing, is not a managed assembly, or carries an identity manifest that
    /// cannot be read.
    /// </exception>
    public static PluginAssemblyProbe Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            throw new PackagingError(
                $"The plugin assembly was not found at '{path}'. Build the solution in Release before packaging.");
        }

        AssemblyName assemblyName;

        try
        {
            assemblyName = AssemblyName.GetAssemblyName(fullPath);
        }
        catch (BadImageFormatException exception)
        {
            throw new PackagingError(
                $"The plugin assembly at '{path}' is not a managed .NET assembly, so it is not a Jellyfin plugin.",
                exception);
        }

        var context = new ProbeLoadContext();

        try
        {
            var assembly = context.LoadFromAssemblyPath(fullPath);
            return new PluginAssemblyProbe(
                fullPath,
                assemblyName.Name ?? string.Empty,
                assemblyName.Version,
                ReadEmbeddedManifest(assembly, path));
        }
        catch (Exception exception) when (exception is FileLoadException or BadImageFormatException or XmlException)
        {
            throw new PackagingError(
                $"The plugin assembly at '{path}' could not be read: {exception.Message}",
                exception);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// Reads the manifest resource an assembly carries.
    /// </summary>
    /// <param name="assembly">The loaded plugin assembly.</param>
    /// <param name="origin">Where the assembly was found, for the refusal message.</param>
    /// <returns>The identity the assembly carries, or <see langword="null"/> when it carries none.</returns>
    private static PluginManifest? ReadEmbeddedManifest(Assembly assembly, string origin)
    {
        // Reading resources touches nothing but this assembly's own metadata, which is why the
        // plugin's server-side references never have to resolve for the job to read it.
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(ManifestResourceSuffix, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            return null;
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new PackagingError($"The plugin assembly at '{origin}' names a manifest resource it does not contain.");

        using var reader = new StreamReader(stream);

        return PluginManifest.Parse(reader.ReadToEnd(), $"the manifest embedded in '{origin}'");
    }

    /// <summary>
    /// The throwaway load context the probe reads through, so the packaged assembly is never
    /// bound into the process that is checking it.
    /// </summary>
    private sealed class ProbeLoadContext : AssemblyLoadContext
    {
        public ProbeLoadContext()
            : base("anaglyfin-packager-probe", isCollectible: true)
        {
        }
    }
}

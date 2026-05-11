// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;

namespace Kuestenlogik.Bowire.Semantics.Extensions;

/// <summary>
/// Helpers for loading bundle / stylesheet assets that an
/// <see cref="IBowireUiExtension"/> ships as embedded resources of its
/// declaring assembly.
/// </summary>
/// <remarks>
/// Centralises the resource-name resolution rules so each extension does
/// not have to hand-roll the same fallback ladder (full logical name
/// → dotted form → on-disk fallback). The on-disk fallback matters in
/// development: the Razor SDK does not always pack <c>wwwroot/**</c> as an
/// embedded resource until the next build, so a freshly-edited file would
/// otherwise 404 against the dev host.
/// </remarks>
public static class EmbeddedExtensionAsset
{
    /// <summary>
    /// Open a read-only stream for the named asset attached to
    /// <paramref name="extension"/>. The lookup tries, in order:
    /// <list type="number">
    /// <item><description>The literal <paramref name="resourceName"/> against <paramref name="assembly"/>.</description></item>
    /// <item><description>The dotted form <c>{AssemblyDefaultNamespace}.{path.with.dots}</c> — matches the standard MSBuild
    /// <c>&lt;EmbeddedResource&gt;</c> naming rule for slash-pathed includes.</description></item>
    /// <item><description>A literal file lookup next to the assembly (development scenario).</description></item>
    /// </list>
    /// Returns <c>null</c> when none of the strategies finds the asset.
    /// </summary>
    public static Stream? OpenRead(
        Assembly assembly,
        IBowireUiExtension extension,
        string resourceName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(resourceName);

        // 1) Literal name as-supplied — supports descriptors that pre-compute
        //    the full resource id themselves.
        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is not null) return stream;

        // 2) Dotted form. Convert wwwroot/js/widgets/map.js →
        //    {AsmDefault}.wwwroot.js.widgets.map.js — the form MSBuild
        //    emits by default for slash-separated <EmbeddedResource> paths.
        var asmName = assembly.GetName().Name;
        if (!string.IsNullOrEmpty(asmName))
        {
            var dotted = resourceName.Replace('/', '.').Replace('\\', '.');
            stream = assembly.GetManifestResourceStream($"{asmName}.{dotted}");
            if (stream is not null) return stream;
        }

        // 3) On-disk fallback next to the assembly — dev scenario where
        //    the file exists in source but the embedded-resource pack
        //    is stale. Treats resourceName as a relative path under the
        //    assembly's directory.
        try
        {
            var dir = Path.GetDirectoryName(assembly.Location);
            if (!string.IsNullOrEmpty(dir))
            {
                var diskPath = Path.Combine(dir, resourceName.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(diskPath))
                {
                    return File.OpenRead(diskPath);
                }
            }
        }
        catch (Exception)
        {
            // Single-file-published assemblies have an empty Location;
            // the on-disk fallback simply skips in that case.
        }

        return null;
    }

    /// <summary>
    /// Best-effort MIME-type lookup for a small set of static-asset
    /// extensions the workbench serves: JS, CSS, JSON, PNG, SVG, WASM.
    /// Anything else falls back to <c>application/octet-stream</c>.
    /// </summary>
    public static string GuessContentType(string resourceName)
    {
        ArgumentNullException.ThrowIfNull(resourceName);
        var ext = Path.GetExtension(resourceName);
        // Compare case-insensitively without the CA1308 ToLowerInvariant
        // alarm — extensions only ever come from caller-controlled
        // descriptors, but the analyzer can't see that.
        if (ext.Equals(".js", StringComparison.OrdinalIgnoreCase)) return "application/javascript";
        if (ext.Equals(".css", StringComparison.OrdinalIgnoreCase)) return "text/css";
        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase)) return "application/json";
        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase)) return "image/png";
        if (ext.Equals(".svg", StringComparison.OrdinalIgnoreCase)) return "image/svg+xml";
        if (ext.Equals(".wasm", StringComparison.OrdinalIgnoreCase)) return "application/wasm";
        if (ext.Equals(".map", StringComparison.OrdinalIgnoreCase)) return "application/json";
        return "application/octet-stream";
    }
}

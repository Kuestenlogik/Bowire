// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;

namespace Kuestenlogik.Bowire.Monitoring;

/// <summary>
/// Discovers the installed <see cref="ISignalerFactory"/> contributions and
/// resolves a <c>--signal &lt;scheme&gt;:&lt;arg&gt;</c> spec to a live
/// <see cref="ISignaler"/>. Signaler packages are opt-in siblings; a scheme with
/// no installed package resolves to a clear "install …" error rather than a
/// crash. Same assembly-scan shape the protocol / CLI-command registries use.
/// </summary>
public sealed class SignalerRegistry
{
    private readonly Dictionary<string, ISignalerFactory> _byScheme;

    private SignalerRegistry(Dictionary<string, ISignalerFactory> byScheme) => _byScheme = byScheme;

    /// <summary>The schemes with an installed factory (e.g. <c>slack</c>, <c>pagerduty</c>).</summary>
    public IReadOnlyCollection<string> Schemes => _byScheme.Keys;

    /// <summary>
    /// Scan for signaler factories. Loads the opt-in sibling assemblies
    /// (<c>Kuestenlogik.Bowire.Monitoring.*.dll</c>) next to the running app so a
    /// package that's present but not yet referenced still contributes, then walks
    /// every loaded <c>Bowire</c> assembly for concrete
    /// <see cref="ISignalerFactory"/> types.
    /// </summary>
    public static SignalerRegistry Discover()
    {
        LoadSiblingAssemblies();

        var byScheme = new Dictionary<string, ISignalerFactory>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(a => a.FullName?.Contains("Bowire", StringComparison.Ordinal) == true))
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!typeof(ISignalerFactory).IsAssignableFrom(type)) continue;
                if (Activator.CreateInstance(type) is ISignalerFactory factory)
                {
                    // Last-write-wins on scheme collision (matches the rail registry).
                    byScheme[factory.Scheme] = factory;
                }
            }
        }
        return new SignalerRegistry(byScheme);
    }

    /// <summary>
    /// Resolve one <c>scheme:arg</c> spec. Returns the signaler, or <c>null</c>
    /// with a human-readable <paramref name="error"/> when the scheme has no
    /// installed factory or the argument is invalid.
    /// </summary>
    public ISignaler? Resolve(string spec, out string? error)
    {
        error = null;
        var colon = spec?.IndexOf(':', StringComparison.Ordinal) ?? -1;
        if (spec is null || colon <= 0)
        {
            error = $"Invalid --signal '{spec}': expected '<scheme>:<argument>' (e.g. slack:https://hooks.slack.com/...).";
            return null;
        }

        var scheme = spec[..colon];
        var argument = spec[(colon + 1)..];
        if (!_byScheme.TryGetValue(scheme, out var factory))
        {
            error = $"No signaler installed for '{scheme}'. Install the matching package (e.g. `bowire plugin install Kuestenlogik.Bowire.Monitoring.{Capitalise(scheme)}`).";
            return null;
        }

        try
        {
            return factory.Create(argument);
        }
        catch (SignalerConfigException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static void LoadSiblingAssemblies()
    {
        string dir;
        try
        {
            dir = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(dir)) return;
            foreach (var path in Directory.EnumerateFiles(dir, "Kuestenlogik.Bowire.Monitoring.*.dll"))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.EndsWith(".Tests", StringComparison.Ordinal)) continue;
                try { Assembly.LoadFrom(path); }
                catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
                {
                    // A non-.NET dll or a locked file — not a signaler package; skip.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Base directory unreadable (single-file publish edge, ACLs) — the
            // loaded-assembly scan below still finds anything already referenced.
        }
    }

    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null).ToArray()!; }
    }

    private static string Capitalise(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Plugins;

/// <summary>
/// Registry of discovered <see cref="IBowireModuleContribution"/> descriptors.
/// Same discovery / override semantics as <see cref="BowireRailRegistry"/>,
/// but scoped to cross-cutting modules (AI, Assistant, var-resolver, …).
/// </summary>
public sealed class BowireModuleRegistry
{
    private readonly Dictionary<string, IBowireModuleContribution> _byId =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All registered modules, sorted by id for deterministic order.</summary>
    public IReadOnlyList<IBowireModuleContribution> Modules
        => [.. _byId.Values.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)];

    public void Register(IBowireModuleContribution module)
    {
        ArgumentNullException.ThrowIfNull(module);
        _byId[module.Id] = module;
    }

    public IBowireModuleContribution? GetById(string id)
        => _byId.TryGetValue(id, out var module) ? module : null;

    /// <summary>
    /// Auto-discover module contributions from every loaded
    /// <c>Kuestenlogik.Bowire*</c> assembly. Modules are an opt-in
    /// surface — when none are referenced, the registry is empty and
    /// the JS bundle simply doesn't render any module-specific hooks.
    /// </summary>
    public static BowireModuleRegistry Discover(ILogger? logger = null)
    {
        var registry = new BowireModuleRegistry();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.FullName?.Contains("Bowire") == true))
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                logger?.LogWarning(ex,
                    "Skipped Bowire assembly during module-contribution scan: {Assembly}",
                    assembly.FullName);
                continue;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!typeof(IBowireModuleContribution).IsAssignableFrom(type)) continue;
                try
                {
                    if (Activator.CreateInstance(type) is IBowireModuleContribution module)
                    {
                        registry.Register(module);
                    }
                }
#pragma warning disable CA1031
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    logger?.LogWarning(ex,
                        "Failed to instantiate module contribution {Type}", type.FullName);
                }
            }
        }
        return registry;
    }

    /// <summary>
    /// Render the registry as the JSON literal seeded into
    /// <c>__BOWIRE_CONFIG__.modules</c>.
    /// </summary>
    public string ToJson()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('[');
        var first = true;
        foreach (var module in Modules)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('{');
            sb.Append("\"id\":\"").Append(EscapeJson(module.Id)).Append("\",");
            sb.Append("\"label\":\"").Append(EscapeJson(module.DisplayName)).Append("\",");
            sb.Append("\"description\":\"").Append(EscapeJson(module.Description)).Append("\",");
            sb.Append("\"defaultEnabled\":").Append(module.DefaultEnabled ? "true" : "false");
            sb.Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string EscapeJson(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
}

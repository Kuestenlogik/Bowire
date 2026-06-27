// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Plugins;

/// <summary>
/// Registry of discovered <see cref="IBowireRailContribution"/> descriptors.
/// Mirrors <see cref="BowireProtocolRegistry"/>'s shape — scans loaded
/// assemblies whose name starts with <c>Kuestenlogik.Bowire</c> for rail
/// contributions, instantiates each (default ctor required), and exposes
/// the sorted catalogue to <c>BowireHtmlGenerator</c> for transport to the
/// JS bundle's <c>__BOWIRE_CONFIG__.rails</c> seed.
/// </summary>
/// <remarks>
/// <para>
/// Auto-discovered descriptors mix freely with descriptors registered by
/// hand through <c>services.AddBowireRail&lt;TRail&gt;()</c>; the registry
/// de-duplicates by <see cref="IBowireRailContribution.Id"/> so a host that
/// wants to override a built-in rail's metadata can do so by registering a
/// replacement descriptor with the same id (last write wins).
/// </para>
/// </remarks>
public sealed class BowireRailRegistry
{
    private readonly Dictionary<string, IBowireRailContribution> _byId =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// All registered rails, sorted by
    /// <see cref="IBowireRailContribution.SortIndex"/>. Stable secondary
    /// sort by <see cref="IBowireRailContribution.Id"/> so two rails with
    /// the same sort index render in a deterministic order across
    /// reloads.
    /// </summary>
    public IReadOnlyList<IBowireRailContribution> Rails
        => [.. _byId.Values
            .OrderBy(r => r.SortIndex)
            .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Register a rail descriptor. If a descriptor with the same
    /// <see cref="IBowireRailContribution.Id"/> is already registered,
    /// the new one replaces it — this lets hosts override a built-in
    /// rail's metadata (label, icon, sort) without having to fork the
    /// contributing package.
    /// </summary>
    public void Register(IBowireRailContribution rail)
    {
        ArgumentNullException.ThrowIfNull(rail);
        _byId[rail.Id] = rail;
    }

    /// <summary>Lookup by id, or <c>null</c> when no rail with that id is registered.</summary>
    public IBowireRailContribution? GetById(string id)
        => _byId.TryGetValue(id, out var rail) ? rail : null;

    /// <summary>
    /// Auto-discover rail contributions from every loaded
    /// <c>Kuestenlogik.Bowire*</c> assembly. The registry returned
    /// already carries the built-in rail set: hosts that don't reference
    /// a rail package simply don't see that rail in the catalogue.
    /// </summary>
    public static BowireRailRegistry Discover(ILogger? logger = null)
    {
        var registry = new BowireRailRegistry();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.FullName?.Contains("Bowire") == true))
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            // Same defensive catch as BowireProtocolRegistry.Discover — a
            // single bad plugin assembly must not abort the scan.
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031
            {
                logger?.LogWarning(ex,
                    "Skipped Bowire assembly during rail-contribution scan: {Assembly}",
                    assembly.FullName);
                continue;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!typeof(IBowireRailContribution).IsAssignableFrom(type)) continue;
                try
                {
                    if (Activator.CreateInstance(type) is IBowireRailContribution rail)
                    {
                        registry.Register(rail);
                    }
                }
#pragma warning disable CA1031
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    logger?.LogWarning(ex,
                        "Failed to instantiate rail contribution {Type}", type.FullName);
                }
            }
        }
        return registry;
    }

    /// <summary>
    /// Render the registry as the JSON literal seeded into
    /// <c>__BOWIRE_CONFIG__.rails</c>. Each rail becomes an object
    /// matching the shape <c>render-sidebar.js</c> expects of an entry
    /// in <c>_railModes</c>: <c>{ id, label, icon, group, sort,
    /// sidebar:{ kind }, hideFromRail, alwaysOn }</c>.
    /// </summary>
    public string ToJson()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('[');
        var first = true;
        foreach (var rail in Rails)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('{');
            sb.Append("\"id\":\"").Append(EscapeJson(rail.Id)).Append("\",");
            sb.Append("\"label\":\"").Append(EscapeJson(rail.DisplayName)).Append("\",");
            sb.Append("\"icon\":\"").Append(EscapeJson(rail.IconKey)).Append("\",");
            sb.Append("\"group\":\"").Append(EscapeJson(rail.Group)).Append("\",");
            sb.Append("\"sort\":").Append(rail.SortIndex).Append(',');
            sb.Append("\"sidebar\":{\"kind\":\"").Append(EscapeJson(rail.SidebarKind)).Append("\"},");
            sb.Append("\"hideFromRail\":").Append(rail.HideFromRail ? "true" : "false").Append(',');
            sb.Append("\"alwaysOn\":").Append(rail.AlwaysOn ? "true" : "false").Append(',');
            sb.Append("\"defaultEnabled\":").Append(rail.DefaultEnabled ? "true" : "false").Append(',');
            // The JS bundle reads this from __BOWIRE_CONFIG__.rails on
            // every rail click — when set + no active workspace, the
            // click redirects to Home and fires a "create a workspace
            // first" toast instead of switching into the rail's
            // (necessarily-empty) view.
            sb.Append("\"requiresWorkspace\":").Append(rail.RequiresWorkspace ? "true" : "false");
            // #314 — per-rail renderer keys. Emitted only when set so
            // the JSON stays terse for the (still common) case where a
            // rail relies on core's hardcoded dispatcher arm. The JS
            // dispatcher's lookup is null-safe: a missing key falls
            // through to the legacy switch arm in render-sidebar.js /
            // render-main.js, which is exactly the migration shape this
            // issue's hook was built for.
            if (!string.IsNullOrEmpty(rail.SidebarRendererKey))
            {
                sb.Append(",\"sidebarRendererKey\":\"")
                    .Append(EscapeJson(rail.SidebarRendererKey))
                    .Append('"');
            }
            if (!string.IsNullOrEmpty(rail.MainPaneRendererKey))
            {
                sb.Append(",\"mainPaneRendererKey\":\"")
                    .Append(EscapeJson(rail.MainPaneRendererKey))
                    .Append('"');
            }
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

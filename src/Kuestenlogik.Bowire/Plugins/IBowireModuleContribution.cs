// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Plugins;

/// <summary>
/// Descriptor contributed by a package that wants to add a cross-cutting
/// module (AI assistant, MCP bridge, variable resolver, guided tour, …) to
/// the Bowire workbench.
/// </summary>
/// <remarks>
/// <para>
/// Modules differ from rails in that they don't own a left-strip icon: they
/// hook into the workbench across multiple surfaces (the AI module wires a
/// chat pane into every rail, the variable-resolver module patches the URL
/// bar &amp; request-builder, the assistant module hooks the topbar overflow).
/// Hosts that don't ship the package shouldn't see any trace of the module
/// in the UI — that's the whole point of pulling them through descriptors.
/// </para>
/// <para>
/// Like rails, modules are auto-discovered by
/// <see cref="BowireModuleRegistry.Discover"/> via assembly scanning. They
/// can also be registered explicitly via
/// <c>services.AddBowireModule&lt;TModule&gt;()</c>.
/// </para>
/// </remarks>
public interface IBowireModuleContribution
{
    /// <summary>
    /// Stable identifier (e.g. <c>"ai"</c>, <c>"assistant"</c>,
    /// <c>"var-resolver"</c>). Surfaced to the JS bundle so module-aware
    /// render paths can opt into the module's hooks only when it's
    /// loaded.
    /// </summary>
    string Id { get; }

    /// <summary>Human-readable label shown in Settings → Modules.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether the module defaults to ON when the package is referenced.
    /// Most modules should default to <c>true</c> — operators expect the
    /// thing they explicitly installed to work. Set <c>false</c> only
    /// for modules whose footprint is so heavy (network, disk, perf)
    /// that opt-in is the right default.
    /// </summary>
    bool DefaultEnabled => true;
}

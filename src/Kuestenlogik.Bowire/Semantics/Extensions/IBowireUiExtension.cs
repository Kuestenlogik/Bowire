// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Semantics.Extensions;

/// <summary>
/// Server-side descriptor for a Bowire UI extension (viewer and/or
/// editor). Discovered via assembly scan over types tagged with
/// <see cref="BowireExtensionAttribute"/>; surfaced to the workbench
/// through the <c>/api/ui/extensions</c> JSON endpoint, which the JS
/// loader iterates at boot to know which bundles to fetch and which
/// kinds the extension claims.
/// </summary>
/// <remarks>
/// <para>
/// The contract is deliberately minimal in v1.0 — anything beyond
/// id / kinds / bundle resource names risks locking the API surface
/// into decisions that haven't been validated yet. Permissions,
/// dependent kinds, and per-extension capabilities widening land
/// additively in v1.x minor versions.
/// </para>
/// <para>
/// JS-side registration happens through
/// <c>window.BowireExtensions.register({...})</c>. The C# descriptor
/// here exists so the workbench can locate + serve the JS bundle (and
/// stylesheet) from the local Bowire host without an external CDN
/// reference — the offline-safe guarantee the ADR pins.
/// </para>
/// </remarks>
public interface IBowireUiExtension
{
    /// <summary>
    /// Stable extension identifier — typically
    /// <c>{vendor}.{name}</c>. Same string the JS-side
    /// <c>register({ id })</c> call uses, so the workbench can pair the
    /// server-side descriptor with the JS-side registration record.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Semver range the extension declares compatibility with — e.g.
    /// <c>"1.x"</c> for any v1 of the Bowire API. The workbench compares
    /// this against its own Bowire API version on load; a mismatch shows
    /// the extension in a disabled state with a "needs Bowire {x}.x"
    /// badge instead of mounting it.
    /// </summary>
    string BowireApiRange { get; }

    /// <summary>
    /// Semantic kinds this extension can mount against (e.g.
    /// <c>["coordinate.wgs84"]</c> for the built-in map widget). One
    /// extension can claim several kinds — a future MIL-symbol package
    /// might claim <c>mil.symbol-code</c> + <c>mil.echelon</c> in the
    /// same registration.
    /// </summary>
    IReadOnlyList<string> Kinds { get; }

    /// <summary>
    /// Capability bitmask — which mounting roles the extension is
    /// prepared to fill. Drives the workbench's tab placement (Viewer
    /// → response pane, Editor → request pane).
    /// </summary>
    ExtensionCapabilities Capabilities { get; }

    /// <summary>
    /// Embedded-resource name of the JS bundle that calls
    /// <c>window.BowireExtensions.register({...})</c>. Resolved against
    /// the declaring assembly via
    /// <see cref="System.Reflection.Assembly.GetManifestResourceStream(string)"/>
    /// (prefixed with the assembly's default namespace by
    /// <see cref="EmbeddedExtensionAsset.OpenRead"/>). Served at
    /// <c>/api/ui/extensions/{Id}/{Name}</c>.
    /// </summary>
    string BundleResourceName { get; }

    /// <summary>
    /// Optional embedded-resource name of a stylesheet shipped alongside
    /// the bundle. <c>null</c> when the extension renders without any
    /// dedicated CSS (most do).
    /// </summary>
    string? StylesResourceName { get; }
}

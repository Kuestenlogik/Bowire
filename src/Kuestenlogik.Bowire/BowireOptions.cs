// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire;

/// <summary>
/// Runtime configuration for a Bowire instance.
/// </summary>
/// <remarks>
/// <para>
/// Passed to <see cref="BowireEndpointRouteBuilderExtensions.MapBowire(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, string, System.Action{BowireOptions})"/>
/// via the <c>configure</c> callback. Defaults are chosen so that a zero-config
/// <c>app.MapBowire()</c> produces a working embedded UI at <c>/bowire</c>.
/// </para>
/// <para>
/// All protocol-specific settings (gRPC proto sources, REST base URLs, SignalR
/// hub mappings, …) live either in protocol-plugin options classes or on the
/// plugin's own <see cref="BowirePluginSetting"/> contributions — not here.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// app.MapBowire(options =>
/// {
///     options.Title        = "Payments API workbench";
///     options.Description  = "Internal staging environment";
///     options.Theme        = BowireTheme.Dark;
///     options.ServerUrls.Add("https://payments.staging:443");
///     options.ServerUrls.Add("https://notifications.staging:443");
///     options.ShowInternalServices = false;
/// });
/// </code>
/// </example>
public sealed class BowireOptions
{
    /// <summary>
    /// Title shown in the browser tab and the top-left of the workbench header.
    /// Defaults to <c>"Bowire"</c>.
    /// </summary>
    public string Title { get; set; } = "Bowire";

    /// <summary>
    /// Short tagline rendered below the <see cref="Title"/> in the header.
    /// Defaults to <c>"gRPC API Browser"</c> for historical reasons; set it
    /// to something meaningful for your service (e.g. the service name or
    /// environment).
    /// </summary>
    public string Description { get; set; } = "gRPC API Browser";

    /// <summary>
    /// Initial UI theme. Users can flip theme from the header toggle at any
    /// time; the choice is persisted in <c>localStorage</c>, so this setting
    /// only affects the first visit from a given browser.
    /// </summary>
    public BowireTheme Theme { get; set; } = BowireTheme.Dark;

    /// <summary>
    /// URL path prefix at which the workbench is mounted (without leading
    /// slash). Overridden by the <c>pattern</c> parameter of
    /// <see cref="BowireEndpointRouteBuilderExtensions.MapBowire(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, string, System.Action{BowireOptions})"/>
    /// — setting <c>RoutePrefix</c> in the configure callback has no effect.
    /// Default: <c>"bowire"</c>.
    /// </summary>
    public string RoutePrefix { get; set; } = "bowire";

    /// <summary>
    /// Single discovery URL. Kept for backwards compatibility; when set, the
    /// value is merged into <see cref="ServerUrls"/> automatically.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="ServerUrls"/> for new code. When <c>null</c> (the
    /// default in embedded mode), Bowire discovers services against the host
    /// it is embedded in — no extra URL needed.
    /// </remarks>
    public string? ServerUrl { get; set; }

    /// <summary>
    /// One or more discovery URLs for standalone or multi-target setups.
    /// Every installed protocol plugin tries every URL in parallel; the
    /// matching plugin wins per URL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Typical uses:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Running Bowire as a sidecar / CLI against a deployed service.</description></item>
    /// <item><description>Aggregating several microservices (gRPC + REST + SignalR) in one session.</description></item>
    /// <item><description>Comparing staging vs production side-by-side.</description></item>
    /// </list>
    /// </remarks>
    public List<string> ServerUrls { get; } = [];

    /// <summary>
    /// Plugin ids to exclude from the assembly-scan registry — handy
    /// when one of the installed plugins fails to load (broken DLL,
    /// missing dependency) or its discovery probe is too expensive
    /// for the current host's network. Wire it via
    /// <c>Bowire:DisabledPlugins</c> in <c>appsettings.json</c>, the
    /// <c>--disable-plugin</c> CLI flag, or by appending to this
    /// list directly when calling <c>AddBowire(opts =&gt;
    /// opts.DisabledPlugins.Add("grpc"))</c>. Matched case-
    /// insensitively against <see cref="IBowireProtocol.Id"/>.
    /// </summary>
    public List<string> DisabledPlugins { get; } = [];

    /// <summary>
    /// When <c>true</c>, the server-URL input in the UI is read-only.
    /// Use this for CI, demos, or hardened deployments where you want users
    /// to browse the pre-configured service but not point the workbench at
    /// other hosts.
    /// </summary>
    public bool LockServerUrl { get; set; }

    /// <summary>
    /// When <c>true</c>, the sidebar lists well-known internal services such
    /// as <c>grpc.reflection.v1alpha.ServerReflection</c> and the gRPC health
    /// endpoint. Useful for debugging reflection itself; hidden by default
    /// because it clutters the service tree for most users.
    /// </summary>
    public bool ShowInternalServices { get; set; }

    /// <summary>
    /// UI operating mode — controls whether the URL bar is shown and whether
    /// service discovery runs in-process or against remote URLs.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="BowireMode.Embedded"/> because
    /// <c>app.MapBowire()</c> implies an in-process host. The standalone
    /// <c>dotnet tool</c> (<c>Kuestenlogik.Bowire.Tool</c>) flips this to
    /// <see cref="BowireMode.Standalone"/> explicitly.
    /// </remarks>
    public BowireMode Mode { get; set; } = BowireMode.Embedded;

    /// <summary>
    /// Proto file sources used when a gRPC server does not expose
    /// <a href="https://grpc.io/docs/guides/reflection/">Server Reflection</a>.
    /// Each <see cref="ProtoSource"/> points at a file, embedded resource,
    /// or inline proto string.
    /// </summary>
    /// <remarks>
    /// When both reflection and proto sources are available, proto sources
    /// take precedence (they are considered the authoritative schema).
    /// </remarks>
    public List<ProtoSource> ProtoSources { get; } = [];

    /// <summary>
    /// Override for the user-local schema-hints file path. When
    /// <c>null</c> (the default), Bowire resolves to
    /// <c>~/.bowire/schema-hints.json</c> — the canonical location
    /// described by the frame-semantics-framework ADR. Set this to
    /// redirect the file (e.g. for tests that need an isolated path,
    /// or for hardened deployments that pin the location). Setting it
    /// to the empty string disables the user-local layer entirely;
    /// only the project-local file (when present) and session edits
    /// will contribute to <c>User</c>-priority annotations.
    /// </summary>
    public string? SchemaHintsPath { get; set; }

    /// <summary>
    /// When <c>true</c>, the five built-in
    /// <c>IBowireFieldDetector</c>s shipped by Bowire core
    /// (WGS84 coordinate, GeoJSON Point, image bytes, audio bytes,
    /// timestamp) are NOT registered. Useful for hardened deployments
    /// that want to ship their own detector set without the
    /// built-ins racing them, or for tests pinning a deterministic
    /// detector list. The <see cref="Semantics.Detectors.IFrameProber"/>
    /// singleton is still registered — it just has nothing to run
    /// until the host adds its own detectors to the container.
    /// </summary>
    public bool DisableBuiltInDetectors { get; set; }
}

/// <summary>
/// Bowire UI operating mode — chooses between in-process discovery
/// (embedded) and user-driven URL entry (standalone).
/// </summary>
public enum BowireMode
{
    /// <summary>
    /// In-process: the URL bar is hidden and services are discovered via the
    /// host's <see cref="System.IServiceProvider"/>. Any URLs configured via
    /// <see cref="BowireOptions.ServerUrls"/> are still used silently for
    /// transport-level discovery (e.g. MQTT broker introspection, OData
    /// <c>$metadata</c> fetches).
    /// </summary>
    Embedded,

    /// <summary>
    /// External: the URL bar is visible and the user can add / edit /
    /// remove discovery URLs at runtime. Used by the standalone CLI tool
    /// and any hosted test environment where Bowire is not colocated with
    /// the target service.
    /// </summary>
    Standalone
}

/// <summary>
/// Initial colour theme for the Bowire UI. Users can always override this
/// from the theme toggle in the header; the selected theme is persisted per
/// browser in <c>localStorage</c>.
/// </summary>
public enum BowireTheme
{
    /// <summary>Dark theme (default).</summary>
    Dark,

    /// <summary>Light theme.</summary>
    Light
}

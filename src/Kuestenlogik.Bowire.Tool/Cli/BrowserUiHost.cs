// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Ai;
using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Mock.Management;
using Kuestenlogik.Bowire.Telemetry;
// UseBowireAuth lives in Kuestenlogik.Bowire.Auth; already covered.
using Kuestenlogik.Bowire.PluginLoading;
using Kuestenlogik.Bowire.Protocol.Mcp;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// Default <c>bowire</c> entry point — runs the standalone browser UI
/// host. Lifted out of <c>Program.cs</c> when the CLI dispatch moved to
/// <see cref="BowireCli"/>; the logic is unchanged from the previous
/// monolithic Program.cs (multi-URL binding, plugin auto-load, optional
/// MCP adapter, auto-open browser).
/// </summary>
internal static class BrowserUiHost
{
    // internal: lets tests swap the browser-launch + ASP.NET host without
    // spawning a real Process or binding a real Kestrel port. The
    // defaults exactly reproduce the original inline behaviour.
    internal static Func<string, CancellationToken, Task> OpenBrowserAsync { get; set; } = DefaultOpenBrowser;

    // internal: tests substitute a TestServer-friendly runner that
    // captures the configured port + URL list instead of binding a real
    // socket. The default builds the live WebApplication exactly as the
    // original inline code did.
    internal static Func<string[], BrowserUiOptions, CancellationToken, Task<int>> HostRunner { get; set; } = DefaultHostRunner;

    public static async Task<int> RunAsync(string[] args, IConfiguration bootstrapConfig, string pluginDir,
        TextWriter? stdout = null, TextWriter? stderr = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(bootstrapConfig);
        _ = pluginDir; // resolved via the configuration stack by BuildBrowserUiOptions
        var io = CommandIo.Resolve(stdout, stderr);

        var ui = BowireConfiguration.BuildBrowserUiOptions(bootstrapConfig, args);

        // Plugins must be loaded before MapBowire's reflection scan
        // sees them. The CLI dispatcher already loaded them once; this
        // call is idempotent for the host's load context. We surface
        // per-plugin load outcomes to stderr so operators see version
        // mismatches and load failures up-front instead of debugging
        // a silently-missing protocol later.
        var pluginResults = PluginManager.LoadPlugins(ui.PluginDir);
        foreach (var r in pluginResults)
        {
            if (r.Status == Kuestenlogik.Bowire.PluginLoading.PluginLoadStatus.Loaded
                || r.Status == Kuestenlogik.Bowire.PluginLoading.PluginLoadStatus.AlreadyLoaded)
                continue;
            await io.Err.WriteLineAsync($"  Plugin '{r.PackageId}' failed to load ({r.Status}): {r.ErrorMessage}").ConfigureAwait(false);
        }

        var noBrowser = ui.NoBrowser
            || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
            || Environment.GetEnvironmentVariable("CI") is not null
            || !Environment.UserInteractive;

        io.OutLine();
        io.OutLine($"  Bowire is running at:  http://localhost:{ui.Port}/");
        if (ui.EnableMcpAdapter)
            io.OutLine($"  MCP adapter (opt-in):   http://localhost:{ui.Port}/mcp");
        foreach (var u in ui.ServerUrls)
            io.OutLine($"  Connected to:           {u}");
        io.OutLine();
        io.OutLine("  Press Ctrl+C to stop.");
        io.OutLine();

        if (!noBrowser)
        {
            var browserUrl = $"http://localhost:{ui.Port}/";
            // Capture the static delegate locally so a swap-back after
            // RunAsync returns (test seams, hot-reload) cannot redirect
            // the launch into a different implementation. The Task.Run
            // body reads the static at execution time, not scheduling
            // time -- without this capture a test that restores the
            // real DefaultOpenBrowser in its finally races us and ends
            // up spawning a real `xdg-open` on the CI runner.
            var openBrowser = OpenBrowserAsync;
            _ = Task.Run(async () =>
            {
                try
                {
                    await openBrowser(browserUrl, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Headless / CI / browser unavailable — silently swallow.
                }
            }, ct);
        }

        return await HostRunner(args, ui, ct).ConfigureAwait(false);
    }

    private static async Task DefaultOpenBrowser(string url, CancellationToken ct)
    {
        await Task.Delay(500, ct).ConfigureAwait(false);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private static async Task<int> DefaultHostRunner(string[] args, BrowserUiOptions ui, CancellationToken ct)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls($"http://localhost:{ui.Port}");
        builder.Services.AddResponseCompression(opts => opts.EnableForHttps = true);
        // Run every loaded plugin's IBowireProtocolServices.ConfigureServices
        // so prerequisites like services.AddGrpcReflection() actually land
        // in the container. Without this, MapBowire's per-plugin
        // MapDiscoveryEndpoints can fail with the "required services not
        // registered" warning even though the workbench itself renders
        // fine.
        builder.Services.AddBowire();

        // Mock-management surface (#56). Registers MockRegistry +
        // mounts /api/mocks endpoints so the workbench's Mocks panel
        // can start / stop / list UI-driven mocks without shelling
        // out to `bowire mock --recording`.
        builder.Services.AddBowireMockManagement();

        // #94 — IRecordingJsonProvider adapter that bridges the
        // Mock-package endpoints to the workbench's RecordingStore
        // (internal in core; reachable here via InternalsVisibleTo).
        builder.Services.AddSingleton<IRecordingJsonProvider, WorkbenchRecordingJsonProvider>();

        // Self-telemetry seam (#29). Off by default -- opted in via
        // --telemetry / Bowire:Telemetry:Enabled=true. When on, wires
        // the OTLP exporter against the canonical Kuestenlogik.Bowire
        // Meter + ActivitySource and lets standard OTEL_* env vars
        // drive the wire details.
        builder.Services.AddBowireTelemetry(builder.Configuration);

        // AI integration (#25 Phase 2). Registers IChatClient + the
        // /api/ai/* endpoints. Default provider is Ollama at
        // http://localhost:11434; the workbench's AI tab probes for
        // a local instance on first paint and offers a one-click
        // connect. Cloud providers slot in via the same seam in
        // Phase 3.
        builder.Services.AddBowireAi(builder.Configuration);

        // #154 Phase 4 — in-app help. The standalone CLI always
        // ships with the Help provider so users get docs without
        // extra setup. Embedded hosts opt in via AddBowireHelp() in
        // their own Program.cs.
        builder.Services.AddBowireHelp();

        // Opt-in auth gate. When --auth-provider <id> is set, the
        // matching IBowireAuthProvider plugin gets to wire its scheme
        // + the BowireAuthPolicies.Default policy; otherwise this is
        // a no-op and the workbench stays open (today's default).
        builder.Services.AddBowireAuth(builder.Configuration);

        // Opt-in MCP adapter. Registering it pre-Build is the new
        // DI-driven shape (the previous WithMcpAdapter() called at
        // map-time predates the official SDK Migration). The adapter
        // exposes Bowire's discovered API surface as MCP tools /
        // resources / prompts so AI agents can drive the workbench.
        if (ui.EnableMcpAdapter)
        {
            var mcpServerUrl = !string.IsNullOrEmpty(ui.PrimaryUrl)
                ? ui.PrimaryUrl
                : $"http://localhost:{ui.Port}";
            builder.Services.AddBowireMcpAdapter(mcpServerUrl);
        }

        var app = builder.Build();
        app.UseResponseCompression();

        // UseAuthentication/UseAuthorization are only meaningful when
        // an IBowireAuthProvider registered a scheme above. Calling
        // them unconditionally is safe because AddBowireAuth registers
        // the AuthenticationSchemeProvider + Authorization services
        // even when no provider is active — the middleware then runs
        // as a no-op. Keeps the pipeline shape predictable across both
        // modes.
        app.UseAuthentication();
        app.UseAuthorization();
        // #31 — give the active IBowireAuthProvider a chance to insert
        // middleware (callback paths, claims transformation, &c). No-op
        // when no provider is registered.
        app.UseBowireAuth();

        // Standalone CLI mounts the workbench at the site root ("/") —
        // there's no host app sharing the route table, so a `/bowire`
        // prefix would just be a wasted hop. Embedded callers keep the
        // default `/bowire` (or whatever pattern they pass) so they don't
        // collide with their own routes.
        var bowire = app.MapBowire("/", options =>
        {
            options.Mode = Kuestenlogik.Bowire.BowireMode.Standalone;
            options.Title = ui.Title;
            // Description carries operator-relevant status (which server
            // we're connected to in locked mode); the empty-state hint
            // ('please type a server URL') is UX copy and belongs in the
            // landing page, not the header. So in unlocked / first-run
            // mode we leave the description empty and the header collapses
            // to the small-logo + wordmark pattern from bowire.io.
            options.Description = ui.LockServerUrl
                ? (ui.ServerUrls.Count == 1 ? $"Connected to {ui.PrimaryUrl}" : $"Connected to {ui.ServerUrls.Count} URLs")
                : string.Empty;
            options.ServerUrl = ui.PrimaryUrl;
            foreach (var u in ui.ServerUrls) options.ServerUrls.Add(u);
            options.LockServerUrl = ui.LockServerUrl;
            // Forward --disable-plugin / Bowire:DisabledPlugins through
            // so the protocol-registry assembly scan honours it.
            foreach (var p in ui.DisabledPlugins) options.DisabledPlugins.Add(p);
            // Forward --map-basemap / Bowire:MapBasemap so the MapLibre
            // widget picks the operator-chosen basemap (osm / satellite /
            // demotiles / custom URL) instead of the bundled default.
            options.MapBasemap = ui.MapBasemap;
        });

        if (ui.EnableMcpAdapter)
        {
            // Standalone mounts at "/" — pass "" so the MCP adapter
            // lands at `/mcp`, not `/bowire/mcp`. The matching DI
            // registration happened pre-Build above.
            app.MapBowireMcpAdapter(prefix: string.Empty);
        }

        // Mock-management endpoints — same base-path discipline as the
        // bowire HTML mount (standalone => "" so endpoints land at
        // `/api/mocks`, not `/bowire/api/mocks`).
        app.MapBowireMockManagement(basePath: string.Empty);

        // #94 — "Use as mock" one-click endpoints. Recording lookup
        // dialled through a tiny adapter so the Mock package doesn't
        // see the workbench's internal RecordingStore.
        app.MapBowireMockHostEndpoints(basePath: string.Empty);

        // AI endpoints (#25 Phase 2). Same base-path discipline.
        app.MapBowireAiEndpoints(basePath: string.Empty);

        await app.RunAsync(ct).ConfigureAwait(false);
        return 0;
    }
}

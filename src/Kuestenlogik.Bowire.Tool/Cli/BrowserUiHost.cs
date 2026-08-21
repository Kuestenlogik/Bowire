// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Ai;
using Kuestenlogik.Bowire.Ai.Anthropic;
using Kuestenlogik.Bowire.Ai.Mcp;
using Kuestenlogik.Bowire.Ai.OpenAi;
using Kuestenlogik.Bowire.Security.Scanner;
using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.App.Plugins;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Mock.Management;
using Kuestenlogik.Bowire.Telemetry;
using Kuestenlogik.Bowire.Workspace.Git;
// UseBowireAuth lives in Kuestenlogik.Bowire.Auth; already covered.
using Kuestenlogik.Bowire.PluginLoading;
using Kuestenlogik.Bowire.Protocol.Mcp;
using Kuestenlogik.Bowire.Sources;
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
    internal static Func<string[], BrowserUiOptions, IBowirePluginLoader, CancellationToken, Task<int>> HostRunner { get; set; } = DefaultHostRunner;

    public static async Task<int> RunAsync(string[] args, IConfiguration bootstrapConfig, IBowirePluginLoader plugins,
        TextWriter? stdout = null, TextWriter? stderr = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(bootstrapConfig);
        ArgumentNullException.ThrowIfNull(plugins);
        var io = CommandIo.Resolve(stdout, stderr);

        // Operator mistakes in the options — a --url-file that is not there,
        // one with nothing usable in it — are reported as one line on stderr,
        // not as an unhandled exception. #604 was about a flag that failed
        // silently; answering that with a stack trace would swap one bad
        // failure mode for another, because a crash reads as "Bowire is
        // broken" rather than "that path is wrong".
        BrowserUiOptions ui;
        try
        {
            ui = BowireConfiguration.BuildBrowserUiOptions(bootstrapConfig, args);
        }
        catch (InvalidOperationException ex)
        {
            await io.Err.WriteLineAsync($"  {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        // Plugins must be loaded before MapBowire's reflection scan sees
        // them. Program.cs already loaded them through this same loader;
        // the repeat is idempotent because the ledger lives on the
        // instance. We surface per-plugin outcomes to stderr so operators
        // see version mismatches and load failures up front instead of
        // debugging a silently-missing protocol later.
        //
        // ui.PluginDir and the loader's directory cannot disagree —
        // BuildBrowserUiOptions fills the former from the same
        // BowirePluginOptions chain (#546).
        var pluginResults = plugins.Load();
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

        return await HostRunner(args, ui, plugins, ct).ConfigureAwait(false);
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

    private static async Task<int> DefaultHostRunner(string[] args, BrowserUiOptions ui, IBowirePluginLoader plugins, CancellationToken ct)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(args);
        // #537 — expand bare boolean flags before handing the args to a
        // second command-line source. CommandLineConfigurationProvider
        // reads the token AFTER a valueless flag as that flag's value, so
        // `--no-browser --catalogue-provider local` bound
        // no-browser="--catalogue-provider" and dropped the catalogue flag
        // on the floor. The bootstrap config has always done this
        // expansion; this container never did, which quietly made every
        // switch-mapped flag below order-dependent.
        var hostArgs = BowireConfiguration.ExpandKnownBooleanFlags(args);
        // #486 — bridge the OAST flags into THIS host's configuration. The
        // command-line source CreateBuilder(args) adds carries no switch
        // mappings, so --oast-server would land only in the bootstrap config
        // (which feeds BrowserUiOptions, not this container). The workbench OAST
        // service reads builder.Configuration, so map the flags here too. Env /
        // appsettings (Bowire__Oast__Server) already reach this config directly.
        builder.Configuration.AddCommandLine(hostArgs, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--oast-server"] = "Bowire:Oast:Server",
            ["--oast-token"] = "Bowire:Oast:Token",
            // #537 — same reasoning for the catalogue flags. AddBowireCatalogue
            // binds Bowire:Discovery:Catalogue off builder.Configuration, not
            // off BrowserUiOptions, so these belong in THIS dictionary rather
            // than in BowireConfiguration's bootstrap switch mappings.
            ["--catalogue-provider"] = "Bowire:Discovery:Catalogue:Provider",
            ["--catalogue-path"] = "Bowire:Discovery:Catalogue:Local:Path",
            ["--catalogue-url"] = "Bowire:Discovery:Catalogue:Http:Url",
            ["--catalogue-consul"] = "Bowire:Discovery:Catalogue:Consul:Address",
        });
        InferCatalogueProvider(builder.Configuration);
        builder.WebHost.UseUrls($"http://localhost:{ui.Port}");
        builder.Services.AddResponseCompression(opts => opts.EnableForHttps = true);
        // Run every loaded plugin's IBowireProtocolServices.ConfigureServices
        // so prerequisites like services.AddGrpcReflection() actually land
        // in the container. Without this, MapBowire's per-plugin
        // MapDiscoveryEndpoints can fail with the "required services not
        // registered" warning even though the workbench itself renders
        // fine.
        builder.Services.AddBowire();

        // Catalogue-provider seam (#136 / #537). The standalone tool never
        // opted in, so `bowire` was the one host where /api/catalogue/info
        // always answered available:false — the whole browse-a-catalogue
        // surface was dead in CLI mode while every embedded sample had it.
        // Registering unconditionally is a no-op until an operator sets
        // Bowire:Discovery:Catalogue:Provider (or --catalogue-provider /
        // --catalogue-path): with no provider id the accessor resolves to
        // null and the endpoints short-circuit to an empty list, exactly
        // as before.
        builder.Services.AddBowireCatalogue(builder.Configuration);

        // Mock-management surface (#56). Registers the host manager +
        // mounts /api/mocks endpoints so the workbench's Mocks panel
        // can start / stop / list UI-driven mocks without shelling
        // out to `bowire mock --recording`. #560 — wire the manager with
        // the plugin-contributed schema sources (Protocol.Rest / Grpc /
        // GraphQL) so the rail can also start a schema-only mock. `plugins`
        // is already Load()ed by RunAsync; the built-in enumeration is
        // cheap and independent of any plugin-directory install.
        builder.Services.AddBowireMockManagement(
            plugins.EnumerateServices<Kuestenlogik.Bowire.Mocking.IBowireMockSchemaSource>(),
            plugins.EnumerateServices<Kuestenlogik.Bowire.Mocking.IBowireMockLiveSchemaHandler>(),
            plugins.EnumerateServices<Kuestenlogik.Bowire.Mocking.IBowireMockHostingExtension>());

        // #94 — IRecordingJsonProvider adapter that bridges the
        // Mock-package endpoints to the workbench's RecordingStore
        // (internal in core; reachable here via InternalsVisibleTo).
        builder.Services.AddSingleton<IRecordingJsonProvider, WorkbenchRecordingJsonProvider>();

        // #563 — resolve a mock auth requirement's authRecordingId into a
        // captured credential from the per-workspace AuthRecordingStore, so an
        // operator can gate a mock behind a recording instead of a pasted token.
        builder.Services.AddSingleton<IAuthRecordingResolver, WorkbenchAuthRecordingResolver>();

        // #563 — flow-capture seam: run a scriptable login → token chain (via the
        // Security.Scanner sibling) and store the captured credential as an auth
        // recording. Outbound HTTP only ever fires on an explicit operator
        // capture action. Registered here so the workbench + MCP surfaces can
        // offer flow-capture; embedded hosts that skip it keep static capture.
        builder.Services.AddSingleton<Kuestenlogik.Bowire.Mocking.IAuthFlowCapturer, AuthFlowCapturer>();

        // Self-telemetry seam (#29). Off by default -- opted in via
        // --telemetry / Bowire:Telemetry:Enabled=true. When on, wires
        // the OTLP exporter against the canonical Kuestenlogik.Bowire
        // Meter + ActivitySource and lets standard OTEL_* env vars
        // drive the wire details.
        builder.Services.AddBowireTelemetry(builder.Configuration);

        // AI integration. Standalone CLI registers every provider:
        // - AddBowireAi (Phase 2)         — Ollama / LM Studio default
        // - AddBowireAiOpenAi (Phase 3)   — OpenAI + OpenRouter BYOK
        // - AddBowireAiAnthropic (Phase 3) — Claude BYOK
        // - AddBowireAiMcp (Phase 4)      — MCP-client reversal
        // Embedded hosts opt in granularly so Bowire core stays free
        // of every provider's transitive SDK weight (#25 ADR rule:
        // "AI features must be a property of the user's environment,
        // not of Bowire's infrastructure"). The workbench's AI tab
        // probes for a local instance on first paint and offers a
        // one-click connect against any of them via the Settings UI.
        builder.Services.AddBowireAi(builder.Configuration);
        builder.Services.AddBowireAiOpenAi();
        builder.Services.AddBowireAiAnthropic();
        builder.Services.AddBowireAiMcp();

        // #104 — the scanner-backed live probe runner for the AI scan
        // orchestration (POST /api/ai/security-scan). Standalone opts in so the
        // orchestration executes real probes; embedded hosts that don't call
        // this run the orchestration in plan-only mode.
        builder.Services.AddBowireSecurityScanProbeRunner();

        // #154 Phase 4 — in-app help. The standalone CLI always
        // ships with the Help provider so users get docs without
        // extra setup. Embedded hosts opt in via AddBowireHelp() in
        // their own Program.cs.
        builder.Services.AddBowireHelp();

        // #196 Phase 2 — Git-backed workspace runtime. Registers the
        // BowireGitWorkspaceExtension marker + the WorkspaceWatcher
        // singleton so the FS-watch SSE endpoint mapped below has
        // somewhere to land. Embedded hosts that DON'T want the
        // watcher skip this call (the SSE endpoint then surfaces a
        // 501 with a hint).
        builder.Services.AddBowireGitWorkspace();

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
        app.MapBowire("/", options =>
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
            // Forward --auto-create-initial-workspace / Bowire:AutoCreate
            // InitialWorkspace. Null (the usual case) means "no host
            // stance" and the workbench falls back to the mode default,
            // which for Standalone is off — the empty Home + "Create your
            // first workspace" CTA. Passing it through is what makes the
            // documented flag actually do something.
            options.AutoCreateInitialWorkspace = ui.AutoCreateInitialWorkspace;
        });

        if (ui.EnableMcpAdapter)
        {
            // Standalone mounts the workbench at "/" — pass "/mcp" so
            // the MCP adapter lands at `/mcp`, distinct from any future
            // `MapBowireMcp` mount (which the dual-mount convention
            // would put at `/mcp` with `/bowire/mcp/adapter` for the
            // adapter when run embedded). The standalone CLI doesn't
            // mount the full-server endpoint today; `/mcp` stays the
            // adapter URL the workbench JS probes. The matching DI
            // registration happened pre-Build above.
            app.MapBowireMcpAdapter(prefix: "/mcp");
        }

        // Mock-management endpoints — same base-path discipline as the
        // bowire HTML mount (standalone => "" so endpoints land at
        // `/api/mocks`, not `/bowire/api/mocks`). The #223 consolidation
        // collapsed the previous /api/mock/* (HostManager) + /api/mocks*
        // (MockRegistry) split into this single surface; "Use as mock"
        // now lands on POST /api/mocks with a { recordingId, label }
        // body, and the recording lookup runs through the
        // IRecordingJsonProvider seam registered above.
        app.MapBowireMockManagement(basePath: string.Empty);

        // AI endpoints (#25 Phase 2). Same base-path discipline.
        app.MapBowireAiEndpoints(basePath: string.Empty);

        // #196 Phase 2.4 — Git-backed workspace FS-watch SSE producer.
        // Workspace.Git is referenced by the standalone Tool so the
        // CLI ships the runtime bundled; embedded hosts add the
        // package + call this themselves. Endpoint surfaces a 501 +
        // hint when the DI registration is missing, so a misconfigured
        // host surfaces an obvious failure rather than a silent
        // never-fires SSE.
        app.MapBowireGitWorkspaceEvents(basePath: string.Empty);

        await app.RunAsync(ct).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Derive <c>Bowire:Discovery:Catalogue:Provider</c> from whichever
    /// provider-specific flag the operator actually passed.
    /// </summary>
    /// <remarks>
    /// Without this, <c>--catalogue-path ./team.json</c> boots with no
    /// catalogue at all: the path lands in configuration but
    /// <see cref="Kuestenlogik.Bowire.Sources.BowireCatalogueProviderRegistry"/>
    /// resolves to null without a provider id, so every catalogue endpoint
    /// short-circuits to empty. The flag looks accepted and does nothing —
    /// the worst kind of failure, because there is no error to search for.
    /// <c>--catalogue-path</c>'s help text already promised this implication;
    /// the two sibling flags get it as well, because a lone
    /// <c>--catalogue-consul</c> or <c>--catalogue-url</c> is exactly as
    /// unambiguous about what the operator meant.
    /// An explicit <c>--catalogue-provider</c> always wins.
    /// </remarks>
    private static void InferCatalogueProvider(ConfigurationManager configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration["Bowire:Discovery:Catalogue:Provider"]))
        {
            return;
        }

        var inferred = !string.IsNullOrWhiteSpace(configuration["Bowire:Discovery:Catalogue:Local:Path"]) ? "local"
            : !string.IsNullOrWhiteSpace(configuration["Bowire:Discovery:Catalogue:Http:Url"]) ? "http"
            : !string.IsNullOrWhiteSpace(configuration["Bowire:Discovery:Catalogue:Consul:Address"]) ? "consul"
            : null;

        if (inferred is null)
        {
            return;
        }

        configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Bowire:Discovery:Catalogue:Provider"] = inferred,
        });
    }
}

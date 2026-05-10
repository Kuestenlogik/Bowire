// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Configuration;
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

    public static async Task<int> RunAsync(string[] args, IConfiguration bootstrapConfig, string pluginDir, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(bootstrapConfig);
        _ = pluginDir; // resolved via the configuration stack by BuildBrowserUiOptions

        var ui = BowireConfiguration.BuildBrowserUiOptions(bootstrapConfig, args);

        // Plugins must be loaded before MapBowire's reflection scan
        // sees them. The CLI dispatcher already loaded them once; this
        // call is idempotent for the host's load context.
        PluginManager.LoadPlugins(ui.PluginDir);

        var noBrowser = ui.NoBrowser
            || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
            || Environment.GetEnvironmentVariable("CI") is not null
            || !Environment.UserInteractive;

        Console.WriteLine();
        Console.WriteLine($"  Bowire is running at:  http://localhost:{ui.Port}/bowire");
        if (ui.EnableMcpAdapter)
            Console.WriteLine($"  MCP adapter (opt-in):   http://localhost:{ui.Port}/bowire/mcp");
        foreach (var u in ui.ServerUrls)
            Console.WriteLine($"  Connected to:           {u}");
        Console.WriteLine();
        Console.WriteLine("  Press Ctrl+C to stop.");
        Console.WriteLine();

        if (!noBrowser)
        {
            var browserUrl = $"http://localhost:{ui.Port}/bowire";
            _ = Task.Run(async () =>
            {
                try
                {
                    await OpenBrowserAsync(browserUrl, ct).ConfigureAwait(false);
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

        var app = builder.Build();
        app.UseResponseCompression();

        var bowire = app.MapBowire(options =>
        {
            options.Mode = Kuestenlogik.Bowire.BowireMode.Standalone;
            options.Title = ui.Title;
            options.Description = ui.LockServerUrl
                ? (ui.ServerUrls.Count == 1 ? $"Connected to {ui.PrimaryUrl}" : $"Connected to {ui.ServerUrls.Count} URLs")
                : "Enter a gRPC server URL to connect";
            options.ServerUrl = ui.PrimaryUrl;
            foreach (var u in ui.ServerUrls) options.ServerUrls.Add(u);
            options.LockServerUrl = ui.LockServerUrl;
            // Forward --disable-plugin / Bowire:DisabledPlugins through
            // so the protocol-registry assembly scan honours it.
            foreach (var p in ui.DisabledPlugins) options.DisabledPlugins.Add(p);
        });

        if (ui.EnableMcpAdapter)
        {
            var mcpServerUrl = !string.IsNullOrEmpty(ui.PrimaryUrl)
                ? ui.PrimaryUrl
                : $"http://localhost:{ui.Port}";
            bowire.WithMcpAdapter(mcpServerUrl);
        }

        app.MapGet("/", () => Microsoft.AspNetCore.Http.Results.Redirect("/bowire"));

        await app.RunAsync(ct).ConfigureAwait(false);
        return 0;
    }
}

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
    public static async Task<int> RunAsync(string[] args, IConfiguration bootstrapConfig, string pluginDir, CancellationToken ct)
    {
        var ui = BowireConfiguration.BuildBrowserUiOptions(bootstrapConfig, args);

        // Plugins must be loaded before MapBowire's reflection scan
        // sees them. The CLI dispatcher already loaded them once; this
        // call is idempotent for the host's load context.
        PluginManager.LoadPlugins(ui.PluginDir);

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

        var app = builder.Build();
        app.UseResponseCompression();

        // Standalone CLI mounts the workbench at the site root ("/") —
        // there's no host app sharing the route table, so a `/bowire`
        // prefix would just be a wasted hop. Embedded callers keep the
        // default `/bowire` (or whatever pattern they pass) so they don't
        // collide with their own routes.
        var bowire = app.MapBowire("/", options =>
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
            // Standalone mounts at "/" — pass "" so the MCP adapter lands at `/mcp`,
            // not `/bowire/mcp`.
            bowire.WithMcpAdapter(mcpServerUrl, prefix: string.Empty);
        }

        var noBrowser = ui.NoBrowser
            || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
            || Environment.GetEnvironmentVariable("CI") is not null
            || !Environment.UserInteractive;

        Console.WriteLine();
        Console.WriteLine($"  Bowire is running at:  http://localhost:{ui.Port}/");
        if (ui.EnableMcpAdapter)
            Console.WriteLine($"  MCP adapter (opt-in):   http://localhost:{ui.Port}/mcp");
        foreach (var u in ui.ServerUrls)
            Console.WriteLine($"  Connected to:           {u}");
        Console.WriteLine();
        Console.WriteLine("  Press Ctrl+C to stop.");
        Console.WriteLine();

        if (!noBrowser)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = $"http://localhost:{ui.Port}/",
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // Headless / CI / browser unavailable — silently swallow.
                }
            }, ct);
        }

        await app.RunAsync(ct).ConfigureAwait(false);
        return 0;
    }
}

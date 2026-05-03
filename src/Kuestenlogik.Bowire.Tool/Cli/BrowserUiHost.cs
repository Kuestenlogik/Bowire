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
        });

        if (ui.EnableMcpAdapter)
        {
            var mcpServerUrl = !string.IsNullOrEmpty(ui.PrimaryUrl)
                ? ui.PrimaryUrl
                : $"http://localhost:{ui.Port}";
            bowire.WithMcpAdapter(mcpServerUrl);
        }

        app.MapGet("/", () => Microsoft.AspNetCore.Http.Results.Redirect("/bowire"));

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
            _ = Task.Run(async () =>
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = $"http://localhost:{ui.Port}/bowire",
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

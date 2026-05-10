// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Handler for <c>bowire mcp serve</c>. Two transports:
/// <list type="bullet">
///   <item><c>--bind stdio</c> (default) — JSON-RPC over stdin/stdout
///         for Claude Desktop / Cursor wiring.</item>
///   <item><c>--bind http</c> — Streamable-HTTP transport at
///         <c>/bowire/mcp</c> for embedded testing.</item>
/// </list>
/// In stdio mode every console-logger write goes to stderr — the SDK
/// owns stdout for JSON-RPC framing and any stray stdout byte derails
/// the protocol.
/// </summary>
internal static class McpServeCommand
{
    // internal: lets tests intercept the actual stdio/http host launch
    // without spawning a real process or binding a port. Defaults
    // reproduce the original inline behaviour exactly.
    internal static Func<McpServeConfig, Task<int>> StdioRunner { get; set; } = DefaultServeStdio;
    internal static Func<McpServeConfig, Task<int>> HttpRunner { get; set; } = DefaultServeHttp;

    /// <summary>
    /// Subset of <see cref="RunAsync"/>'s typed inputs the test seams
    /// need to assert wiring without actually launching a host. Keeps
    /// the seam type-stable when new flags get added.
    /// </summary>
    internal sealed record McpServeConfig(
        Action<BowireMcpOptions> ConfigureOptions,
        int Port,
        bool AllowArbitraryUrls,
        bool NoEnvAllowlist);

    public static Task<int> RunAsync(string bind, int port, bool allowArbitraryUrls, bool noEnvAllowlist)
    {
        Action<BowireMcpOptions> configureOpts = o =>
        {
            o.AllowArbitraryUrls = allowArbitraryUrls;
            o.LoadAllowlistFromEnvironments = !noEnvAllowlist;
        };

        var cfg = new McpServeConfig(configureOpts, port, allowArbitraryUrls, noEnvAllowlist);

        return bind switch
        {
            "stdio" => StdioRunner(cfg),
            "http" => HttpRunner(cfg),
            _ => Fail($"Unknown --bind value: {bind} (expected 'stdio' or 'http').")
        };
    }

    private static async Task<int> DefaultServeStdio(McpServeConfig cfg)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddBowireMcp(cfg.ConfigureOptions)
            .WithStdioServerTransport()
            .WithTools<BowireMcpTools>();

        if (cfg.AllowArbitraryUrls)
            await Console.Error.WriteLineAsync("[bowire-mcp] WARNING: --allow-arbitrary-urls set; bowire.invoke / bowire.subscribe accept any URL the agent supplies.").ConfigureAwait(false);

        await builder.Build().RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> DefaultServeHttp(McpServeConfig cfg)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddBowireMcp(cfg.ConfigureOptions)
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<BowireMcpTools>();

        var app = builder.Build();
        app.MapMcp("/bowire/mcp");

        Console.WriteLine($"  Bowire MCP - listening on http://localhost:{cfg.Port}/bowire/mcp");
        if (cfg.AllowArbitraryUrls)
            Console.WriteLine("  WARNING: --allow-arbitrary-urls is set; URL allowlist is disabled.");
        Console.WriteLine("  Connect Claude Desktop / Cursor with the URL above; or POST JSON-RPC directly.");

        await app.RunAsync($"http://localhost:{cfg.Port}").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> Fail(string message)
    {
        await Console.Error.WriteLineAsync(message).ConfigureAwait(false);
        return 2;
    }
}

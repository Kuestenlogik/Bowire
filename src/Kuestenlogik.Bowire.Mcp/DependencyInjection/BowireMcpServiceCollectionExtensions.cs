// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.Mcp;

/// <summary>
/// DI registration for <see cref="BowireMcpTools"/>. Registers the
/// Bowire protocol registry as a singleton and pulls the
/// <c>ModelContextProtocol</c> server fluent API in. Choose the transport
/// in your <c>Program.cs</c> after calling this — stdio for the CLI,
/// HTTP/SSE for the embedded host:
/// <code>
/// // stdio (CLI)
/// services.AddBowireMcp(o => o.AllowArbitraryUrls = false)
///         .WithStdioServerTransport()
///         .WithTools&lt;BowireMcpTools&gt;();
///
/// // HTTP/SSE (embedded)
/// services.AddBowireMcp()
///         .WithHttpTransport(o => o.Stateless = true)
///         .WithTools&lt;BowireMcpTools&gt;();
/// // …then `app.MapMcp("/bowire/mcp");`
/// </code>
/// </summary>
public static class BowireMcpServiceCollectionExtensions
{
    /// <summary>
    /// Register Bowire-MCP services and return the SDK's
    /// <see cref="IMcpServerBuilder"/> so the caller can chain a
    /// transport (<c>WithStdioServerTransport</c> or
    /// <c>WithHttpTransport</c>) and tool registration.
    /// </summary>
    public static IMcpServerBuilder AddBowireMcp(
        this IServiceCollection services,
        Action<BowireMcpOptions>? configure = null)
    {
        services.AddOptions<BowireMcpOptions>();
        if (configure is not null) services.Configure(configure);

        // The protocol registry is shared across every tool call. Use a
        // singleton so the assembly-scan cost is paid once at startup.
        // The factory takes ILoggerFactory so plugin-load failures land
        // in the host's normal logging pipeline.
        services.AddSingleton(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger("Kuestenlogik.Bowire.Mcp");
            return BowireProtocolRegistry.Discover(logger);
        });

        // Mock handle registry — backs bowire.mock.start / .stop / .list.
        // Singleton so handles outlive individual tool calls; disposal
        // happens with the host so a Ctrl+C cleanly shuts down spawned
        // mock instances.
        services.AddSingleton<BowireMockHandleRegistry>();

        return services.AddMcpServer(o =>
        {
            o.ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = "bowire-mcp",
                Version = typeof(BowireMcpTools).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Title = "Bowire workbench (self-MCP)"
            };
        });
    }
}

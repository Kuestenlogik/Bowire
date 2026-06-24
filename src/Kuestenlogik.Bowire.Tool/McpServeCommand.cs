// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
///
/// <para>
/// Two additional modes layer on top of the transport choice:
/// </para>
/// <list type="bullet">
///   <item><c>--attach &lt;parent-addr&gt;</c> — forwarder mode (#286).
///         No local tool registry; every incoming MCP request is relayed
///         to a running parent Bowire MCP server.</item>
///   <item><c>--token &lt;secret&gt;</c> — bearer-auth gate on the
///         <c>--bind http</c> endpoint. Pair with <c>--attach-token</c>
///         on the child.</item>
/// </list>
/// </summary>
internal static class McpServeCommand
{
    // internal: lets tests intercept the actual stdio/http host launch
    // without spawning a real process or binding a port. Defaults
    // reproduce the original inline behaviour exactly when called with
    // CancellationToken.None.
    internal static Func<McpServeConfig, CancellationToken, Task<int>> StdioRunner { get; set; } = DefaultServeStdio;
    internal static Func<McpServeConfig, CancellationToken, Task<int>> HttpRunner { get; set; } = DefaultServeHttp;

    /// <summary>
    /// Subset of the <c>RunAsync</c> typed inputs the test seams
    /// need to assert wiring without actually launching a host. Keeps
    /// the seam type-stable when new flags get added.
    /// </summary>
    /// <param name="ConfigureOptions">Applied to the local
    /// <see cref="BowireMcpOptions"/> when not in forwarder mode.</param>
    /// <param name="Port">HTTP-bind port; ignored for stdio.</param>
    /// <param name="AllowArbitraryUrls">Mirror of <see cref="BowireMcpOptions.AllowArbitraryUrls"/>.</param>
    /// <param name="NoEnvAllowlist">Inverse of <see cref="BowireMcpOptions.LoadAllowlistFromEnvironments"/>.</param>
    /// <param name="AllowInvoke">Inverse of <see cref="BowireMcpOptions.LoadAllowlistFromTypedUrls"/>'s default-off.</param>
    /// <param name="NoConfirm">Inverse of <see cref="BowireMcpOptions.RequireConfirmationForMutations"/>.</param>
    /// <param name="Io">Diagnostic sink (stderr in stdio mode, stdout in http mode).</param>
    /// <param name="AttachEndpoint">Forwarder target; <c>null</c> disables forwarder mode.</param>
    /// <param name="AttachToken">Bearer token sent to the parent during forwarding.</param>
    /// <param name="ServerToken">Bearer token expected on inbound requests in <c>--bind http</c>.</param>
    internal sealed record McpServeConfig(
        Action<BowireMcpOptions> ConfigureOptions,
        int Port,
        bool AllowArbitraryUrls,
        bool NoEnvAllowlist,
        bool AllowInvoke,
        bool NoConfirm,
        CommandIo Io,
        Uri? AttachEndpoint = null,
        string? AttachToken = null,
        string? ServerToken = null);

    public static Task<int> RunAsync(string bind, int port, bool allowArbitraryUrls, bool noEnvAllowlist)
        => RunAsync(bind, port, allowArbitraryUrls, noEnvAllowlist,
            allowInvoke: false, noConfirm: false,
            stdout: null, stderr: null, ct: CancellationToken.None);

    public static Task<int> RunAsync(string bind, int port, bool allowArbitraryUrls, bool noEnvAllowlist,
        bool allowInvoke, bool noConfirm)
        => RunAsync(bind, port, allowArbitraryUrls, noEnvAllowlist,
            allowInvoke, noConfirm, stdout: null, stderr: null, ct: CancellationToken.None);

    // internal: tests pass a pre-cancelled token so DefaultServeStdio /
    // DefaultServeHttp exit promptly without blocking on stdin/stdout
    // or a Kestrel socket. Public surface is the CT-less overload above
    // — production callers keep that.
    internal static Task<int> RunAsync(string bind, int port, bool allowArbitraryUrls, bool noEnvAllowlist,
        bool allowInvoke = false, bool noConfirm = false,
        TextWriter? stdout = null, TextWriter? stderr = null,
        string? attach = null, string? attachToken = null, string? token = null,
        CancellationToken ct = default)
    {
        Action<BowireMcpOptions> configureOpts = o =>
        {
            o.AllowArbitraryUrls = allowArbitraryUrls;
            o.LoadAllowlistFromEnvironments = !noEnvAllowlist;
            // --allow-invoke widens the allowlist to every URL the
            // user has typed at least once (~/.bowire/typed-urls.json).
            // Strictly additive — combines with the environments seed,
            // never narrows it.
            o.LoadAllowlistFromTypedUrls = allowInvoke;
            // --no-confirm drops the pending-confirmation gate on
            // mutator tools. Default-on so an interactive user gets
            // the safety net by default; agents with their own approval
            // pipeline opt out.
            o.RequireConfirmationForMutations = !noConfirm;
        };

        // Stdio-mode caveat: the JSON-RPC framing the SDK does owns
        // process Console.Out. The CommandIo here is the *diagnostic*
        // sink (the WARNING blob that lands on stderr), not the
        // protocol stream — so it's safe to redirect even in --bind stdio.
        var io = CommandIo.Resolve(stdout, stderr);

        Uri? attachUri = null;
        if (!string.IsNullOrWhiteSpace(attach))
        {
            if (!BowireForwardingMcpTransport.TryParseAttachEndpoint(attach, out attachUri, out var parseError))
                return Fail(parseError, io);
        }

        var cfg = new McpServeConfig(
            configureOpts, port, allowArbitraryUrls, noEnvAllowlist, allowInvoke, noConfirm, io,
            AttachEndpoint: attachUri,
            AttachToken: string.IsNullOrEmpty(attachToken) ? null : attachToken,
            ServerToken: string.IsNullOrEmpty(token) ? null : token);

        return bind switch
        {
            "stdio" => StdioRunner(cfg, ct),
            "http" => HttpRunner(cfg, ct),
            _ => Fail($"Unknown --bind value: {bind} (expected 'stdio' or 'http').", io)
        };
    }

    private static async Task<int> DefaultServeStdio(McpServeConfig cfg, CancellationToken ct)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        if (cfg.AttachEndpoint is not null)
        {
            // Forwarder mode — no local tools / resources / prompts.
            // Every incoming JSON-RPC request goes upstream.
            builder.Services
                .AddBowireMcpForwarder(cfg.AttachEndpoint, cfg.AttachToken)
                .WithStdioServerTransport();

            await cfg.Io.Err.WriteLineAsync(
                $"[bowire-mcp] --attach {cfg.AttachEndpoint} — forwarder mode (stdio). "
                + (cfg.AttachToken is null ? "(no token)" : "(bearer token set)")).ConfigureAwait(false);
            if (cfg.AllowArbitraryUrls || cfg.AllowInvoke || cfg.NoConfirm)
            {
                await cfg.Io.Err.WriteLineAsync(
                    "[bowire-mcp] --attach overrides local tool flags (--allow-arbitrary-urls, --allow-invoke, --no-confirm); the parent's configuration is in effect.").ConfigureAwait(false);
            }
        }
        else
        {
            builder.Services
                .AddBowireMcp(cfg.ConfigureOptions)
                .WithStdioServerTransport()
                .WithTools<BowireMcpTools>()
                .WithResources<BowireMcpResources>()
                .WithPrompts<BowireMcpPrompts>();

            if (cfg.AllowArbitraryUrls)
                await cfg.Io.Err.WriteLineAsync("[bowire-mcp] WARNING: --allow-arbitrary-urls set; bowire.invoke / bowire.subscribe accept any URL the agent supplies.").ConfigureAwait(false);
            if (cfg.AllowInvoke)
                await cfg.Io.Err.WriteLineAsync("[bowire-mcp] --allow-invoke set: seeding allowlist from ~/.bowire/typed-urls.json (in addition to environments.json).").ConfigureAwait(false);
            if (cfg.NoConfirm)
                await cfg.Io.Err.WriteLineAsync("[bowire-mcp] --no-confirm set: mutator tools (bowire.mock.start, bowire.record.start) execute without the two-step confirmation gate.").ConfigureAwait(false);
        }

        try
        {
            await builder.Build().RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
        return 0;
    }

    private static async Task<int> DefaultServeHttp(McpServeConfig cfg, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        if (cfg.AttachEndpoint is not null)
        {
            builder.Services
                .AddBowireMcpForwarder(cfg.AttachEndpoint, cfg.AttachToken)
                .WithHttpTransport(o => o.Stateless = true);
        }
        else
        {
            builder.Services
                .AddBowireMcp(cfg.ConfigureOptions)
                .WithHttpTransport(o => o.Stateless = true)
                .WithTools<BowireMcpTools>()
                .WithResources<BowireMcpResources>()
                .WithPrompts<BowireMcpPrompts>();
        }

        var app = builder.Build();

        // Bearer-auth gate — when --token is set, every request to
        // /bowire/mcp must carry Authorization: Bearer <secret>. The
        // child (--attach-token) supplies it transparently via the
        // forwarder transport's AdditionalHeaders.
        if (!string.IsNullOrEmpty(cfg.ServerToken))
        {
            var expected = "Bearer " + cfg.ServerToken;
            app.Use(async (HttpContext ctx, RequestDelegate next) =>
            {
                if (ctx.Request.Path.StartsWithSegments("/bowire/mcp"))
                {
                    var supplied = (string?)ctx.Request.Headers.Authorization;
                    if (!string.Equals(supplied, expected, StringComparison.Ordinal))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        ctx.Response.Headers["WWW-Authenticate"] = "Bearer realm=\"bowire-mcp\"";
                        await ctx.Response.WriteAsync("Unauthorized — invalid or missing bearer token.", ctx.RequestAborted).ConfigureAwait(false);
                        return;
                    }
                }
                await next(ctx).ConfigureAwait(false);
            });
        }

        app.MapMcp("/bowire/mcp");

        cfg.Io.OutLine($"  Bowire MCP - listening on http://localhost:{cfg.Port}/bowire/mcp");
        if (cfg.AttachEndpoint is not null)
        {
            cfg.Io.OutLine($"  --attach {cfg.AttachEndpoint} — forwarder mode "
                + (cfg.AttachToken is null ? "(no token)." : "(bearer token set)."));
            if (cfg.AllowArbitraryUrls || cfg.AllowInvoke || cfg.NoConfirm)
                cfg.Io.OutLine("  --attach overrides local tool flags; the parent's configuration applies.");
        }
        else
        {
            if (cfg.AllowArbitraryUrls)
                cfg.Io.OutLine("  WARNING: --allow-arbitrary-urls is set; URL allowlist is disabled.");
            if (cfg.AllowInvoke)
                cfg.Io.OutLine("  --allow-invoke set: seeding allowlist from ~/.bowire/typed-urls.json.");
            if (cfg.NoConfirm)
                cfg.Io.OutLine("  --no-confirm set: mutator tools execute without the two-step confirmation gate.");
        }
        if (!string.IsNullOrEmpty(cfg.ServerToken))
            cfg.Io.OutLine("  --token set: inbound requests must carry 'Authorization: Bearer <token>'.");
        cfg.Io.OutLine("  Connect Claude Desktop / Cursor with the URL above; or POST JSON-RPC directly.");

        try
        {
            await app.RunAsync($"http://localhost:{cfg.Port}").WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await app.StopAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        return 0;
    }

    private static async Task<int> Fail(string message, CommandIo io)
    {
        await io.Err.WriteLineAsync(message).ConfigureAwait(false);
        return 2;
    }
}

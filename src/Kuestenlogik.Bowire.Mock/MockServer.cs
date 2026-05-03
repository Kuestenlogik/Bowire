// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mock.Loading;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Mock;

/// <summary>
/// Standalone mock host used by the <c>bowire mock</c> CLI subcommand.
/// Spins up a minimal Kestrel pipeline with <c>UseBowireMock</c> mounted
/// at the root and no fallback service behind it, so unmatched requests
/// return <c>404</c> instead of falling through.
/// </summary>
/// <remarks>
/// <para>
/// Construct the server with <see cref="StartAsync"/>; it runs until
/// <see cref="WaitForShutdownAsync"/> returns or the instance is disposed.
/// Cancelling the cancellation token passed into <see cref="StartAsync"/>
/// is equivalent to <see cref="DisposeAsync"/>.
/// </para>
/// </remarks>
public sealed class MockServer : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly MockKestrelHostedService _kestrel;

    private MockServer(IHost host, MockKestrelHostedService kestrel)
    {
        _host = host;
        _kestrel = kestrel;
    }

    /// <summary>
    /// TCP port the server is actually listening on. Equal to
    /// <see cref="MockServerOptions.Port"/> when the caller specified one;
    /// when the caller passed <c>0</c>, this returns the OS-assigned port
    /// picked at bind time. Useful for tests that don't want to race on
    /// a hard-coded port number.
    /// </summary>
    public int Port => _kestrel.BoundPort;

    /// <summary>
    /// Bound ports for every plugin-contributed transport host that
    /// started successfully. Keyed by
    /// <see cref="IBowireMockTransportHost.Id"/> — typical entries are
    /// <c>"mqtt"</c> (when the MQTT plugin is loaded and the recording
    /// has MQTT steps), <c>"amqp"</c>, etc.
    /// </summary>
    public IReadOnlyDictionary<string, int> TransportPorts => _kestrel.TransportPorts;

    /// <summary>
    /// Build, start and return a running mock server. Throws on startup
    /// failure (invalid recording, port already in use, ...).
    /// </summary>
    public static async Task<MockServer> StartAsync(MockServerOptions options, CancellationToken ct)
    {
        var builder = Host.CreateApplicationBuilder();

        if (options.LoggerFactory is not null)
        {
            builder.Services.AddSingleton(options.LoggerFactory);
        }
        else
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole(fmt =>
            {
                fmt.TimestampFormat = "HH:mm:ss ";
                fmt.SingleLine = true;
            });
            builder.Logging.SetMinimumLevel(LogLevel.Information);
        }

        builder.Services.AddSingleton(options);
        // Register the hosted service as both IHostedService (so the host
        // starts it) and as itself (so MockServer can pull the bound-port
        // back out after Kestrel has chosen one).
        builder.Services.AddSingleton<MockKestrelHostedService>();
        builder.Services.AddSingleton<IHostedService>(sp =>
            sp.GetRequiredService<MockKestrelHostedService>());

        var host = builder.Build();
        await host.StartAsync(ct);
        var kestrel = host.Services.GetRequiredService<MockKestrelHostedService>();
        return new MockServer(host, kestrel);
    }

    /// <summary>Block until the host shuts down (Ctrl+C, explicit stop).</summary>
    public Task WaitForShutdownAsync(CancellationToken ct) => _host.WaitForShutdownAsync(ct);

    public ValueTask DisposeAsync()
    {
        if (_host is IAsyncDisposable async) return async.DisposeAsync();
        _host.Dispose();
        return ValueTask.CompletedTask;
    }

    // Hosted service that boots the actual Kestrel pipeline once the outer
    // host has wired up DI. Separate from the builder flow so Kestrel's
    // IServer lives inside IHostedService.StartAsync / StopAsync semantics.
    private sealed class MockKestrelHostedService : IHostedService, IAsyncDisposable
    {
        private readonly MockServerOptions _options;
        private readonly ILoggerFactory _loggerFactory;
        private WebApplication? _app;
        private readonly List<IBowireMockEmitter> _startedEmitters = new();
        private readonly List<IBowireMockTransportHost> _startedTransports = new();
        private readonly Dictionary<string, int> _transportPorts = new(StringComparer.OrdinalIgnoreCase);

        public MockKestrelHostedService(MockServerOptions options, ILoggerFactory loggerFactory)
        {
            _options = options;
            _loggerFactory = loggerFactory;
        }

        /// <summary>
        /// Port Kestrel actually bound — equal to <see cref="MockServerOptions.Port"/>
        /// when specified, or the OS-assigned port when the caller passed 0.
        /// Populated during <see cref="StartAsync"/> after the first listener
        /// is active.
        /// </summary>
        public int BoundPort { get; private set; }

        /// <summary>
        /// Per-transport-id bound ports — populated as each
        /// <see cref="IBowireMockTransportHost"/> in
        /// <see cref="MockServerOptions.TransportHosts"/> reports its
        /// listener address back. Read by <see cref="MockServer.TransportPorts"/>.
        /// </summary>
        public IReadOnlyDictionary<string, int> TransportPorts => _transportPorts;

        public async Task StartAsync(CancellationToken ct)
        {
            // Exactly one of RecordingPath / SchemaPath / GrpcSchemaPath /
            // GraphQlSchemaPath must be set — CLI and embedded API validate
            // this, but we guard here too so programmatic callers get a clean
            // error instead of a null dereference.
            var hasRecording = !string.IsNullOrEmpty(_options.RecordingPath);
            var hasSchema = !string.IsNullOrEmpty(_options.SchemaPath);
            var hasGrpcSchema = !string.IsNullOrEmpty(_options.GrpcSchemaPath);
            var hasGraphQlSchema = !string.IsNullOrEmpty(_options.GraphQlSchemaPath);
            var sourceCount = (hasRecording ? 1 : 0) + (hasSchema ? 1 : 0)
                + (hasGrpcSchema ? 1 : 0) + (hasGraphQlSchema ? 1 : 0);
            if (sourceCount != 1)
            {
                throw new InvalidOperationException(
                    "MockServerOptions requires exactly one of RecordingPath, SchemaPath, GrpcSchemaPath, or GraphQlSchemaPath.");
            }

            // Load (or synthesise) the recording up front so we can pick
            // the right Kestrel protocol level. Plaintext HTTP/2 and
            // HTTP/1.1 can't share a port without TLS+ALPN, so the mock
            // supports one or the other per port — any plugin-contributed
            // hosting extension that says RequiresHttp2 forces HTTP/2,
            // otherwise HTTP/1.1 stays curl-testable.
            //
            // Schema-only modes delegate to the matching plugin
            // contribution: openapi → Protocol.Rest, protobuf →
            // Protocol.Grpc, graphql → Protocol.GraphQL (live handler).
            BowireRecording recording;
            IBowireMockLiveSchemaHandler? liveSchemaHandler = null;
            if (hasGrpcSchema)
            {
                recording = await LoadFromSchemaAsync("protobuf", _options.GrpcSchemaPath!, ct).ConfigureAwait(false);
            }
            else if (hasSchema)
            {
                recording = await LoadFromSchemaAsync("openapi", _options.SchemaPath!, ct).ConfigureAwait(false);
            }
            else if (hasGraphQlSchema)
            {
                recording = await LoadFromSchemaAsync("graphql", _options.GraphQlSchemaPath!, ct).ConfigureAwait(false);
                liveSchemaHandler = _options.LiveSchemaHandlers.FirstOrDefault(h =>
                    string.Equals(h.Kind, "graphql", StringComparison.OrdinalIgnoreCase));
                if (liveSchemaHandler is not null)
                {
                    await liveSchemaHandler.LoadAsync(
                        _options.GraphQlSchemaPath!,
                        _loggerFactory.CreateLogger("Kuestenlogik.Bowire.Mock.LiveSchemaHandler"),
                        ct).ConfigureAwait(false);
                }
            }
            else
            {
                recording = Loading.RecordingLoader.Load(_options.RecordingPath!, _options.Select);
            }
            var needsHttp2 = _options.HostingExtensions.Any(e => e.RequiresHttp2(recording));

            var builder = WebApplication.CreateBuilder();

            builder.WebHost.ConfigureKestrel(opts =>
            {
                opts.Listen(System.Net.IPAddress.Parse(_options.Host), _options.Port, lo =>
                {
                    lo.Protocols = needsHttp2
                        ? HttpProtocols.Http2
                        : HttpProtocols.Http1;
                });
            });

            builder.Services.AddSingleton(_loggerFactory);

            // Hand the recording over to every plugin-contributed
            // hosting extension so it can register protocol-specific
            // services (gRPC ReflectionServiceImpl is the canonical
            // example). Extensions whose protocol isn't represented in
            // the recording typically no-op.
            foreach (var extension in _options.HostingExtensions)
            {
                extension.ConfigureServices(builder.Services, recording, _loggerFactory);
            }

            _app = builder.Build();

            // Explicit pipeline so endpoint routing (for gRPC Reflection)
            // and the mock middleware don't step on each other:
            //   UseRouting    → resolve the matched endpoint (if any)
            //   mock mw       → pass-through on specific-endpoint match,
            //                   otherwise try to replay the recording
            //   UseEndpoints  → dispatch the matched endpoint (reflection)
            //   app.Run       → terminal 404 for truly unmatched paths
            _app.UseRouting();
            // Enable the WebSocket middleware so Phase-2e duplex replays can
            // accept upgrade requests. Cheap enough to turn on unconditionally;
            // recordings without WS steps never trigger the upgrade path.
            _app.UseWebSockets();

            // Two mount paths:
            //   - recording on disk: the file-path overload wires in the
            //     RecordingWatcher for hot-reload semantics.
            //   - schema-only: we already have the synthesised recording in
            //     memory; the in-memory overload skips watching. Re-starting
            //     the host reloads the schema.
            Action<MockOptions> configure = opts =>
            {
                opts.Watch = hasRecording && _options.Watch;
                opts.Select = _options.Select;
                opts.Matcher = _options.Matcher;
                opts.ReplaySpeed = _options.ReplaySpeed;
                opts.Chaos = _options.Chaos;
                opts.Stateful = _options.Stateful;
                opts.StatefulWrapAround = _options.StatefulWrapAround;
                opts.CaptureMissPath = _options.CaptureMissPath;
                opts.ControlToken = _options.ControlToken;
                opts.PassThroughOnMiss = false; // standalone host has nothing behind it
            };

            // Live-schema handlers (GraphQL today) sit in front of the
            // recording-replay middleware — they own a specific request
            // shape end-to-end (POST /graphql) and return false for
            // everything else so downstream middleware still serves
            // health checks / reflection / etc.
            if (liveSchemaHandler is not null)
            {
                var captured = liveSchemaHandler;
                _app.Use(async (ctx, next) =>
                {
                    if (await captured.TryHandleAsync(ctx, ctx.RequestAborted).ConfigureAwait(false)) return;
                    await next().ConfigureAwait(false);
                });
            }

            if (hasRecording)
                _app.UseBowireMock(_options.RecordingPath!, configure);
            else
                _app.UseBowireMock(recording, configure);

            _app.UseEndpoints(endpoints =>
            {
                foreach (var extension in _options.HostingExtensions)
                {
                    extension.MapEndpoints(endpoints, recording);
                }
            });

            _app.Run(async ctx =>
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync(
                    "{\"error\":\"No recorded step matches this request.\"}",
                    ctx.RequestAborted);
            });

            await _app.StartAsync(ct);

            // Read the bound port back from Kestrel's address feature — this
            // is the real listener address, which differs from _options.Port
            // when the caller passed 0 (OS-assigned).
            BoundPort = ResolveBoundPort(_app, _options.Port);

            var logger = _loggerFactory.CreateLogger<MockServer>();
            var sourceLabel =
                hasGraphQlSchema ? "graphql-schema:" + _options.GraphQlSchemaPath :
                hasGrpcSchema ? "grpc-schema:" + _options.GrpcSchemaPath :
                hasSchema ? "schema:" + _options.SchemaPath :
                "recording:" + _options.RecordingPath;
            logger.LogInformation(
                "Bowire mock listening on http://{Host}:{Port} (source={Source}, steps={StepCount}, watch={Watch})",
                _options.Host, BoundPort, sourceLabel,
                recording.Steps.Count,
                hasRecording && _options.Watch);

            // Plugin-contributed transport hosts — MQTT broker since the
            // plugin-isation refactor (was hardcoded here previously),
            // and any future broker / participant the user has loaded
            // via the plugin directory. Each host owns its own port; we
            // ask it whether the recording is relevant and start the
            // ones that say yes.
            await StartTransportHostsAsync(recording, logger, ct).ConfigureAwait(false);

            // Plugin-contributed emitters (DIS, DDS, raw UDP, ...).
            // Each emitter inspects the recording, decides whether it
            // has relevant steps, and — if so — starts its own
            // transport. Failures are logged but don't abort startup:
            // a misconfigured DIS plugin shouldn't kill the mock's
            // REST replay.
            if (_options.Emitters.Count > 0)
            {
                var mockOptions = new MockOptions
                {
                    Watch = false,
                    Select = _options.Select,
                    Matcher = _options.Matcher,
                    ReplaySpeed = _options.ReplaySpeed,
                    Chaos = _options.Chaos,
                    Stateful = _options.Stateful,
                    StatefulWrapAround = _options.StatefulWrapAround,
                    CaptureMissPath = _options.CaptureMissPath,
                    ControlToken = _options.ControlToken,
                    Loop = _options.Loop,
                    PassThroughOnMiss = false
                };
                foreach (var emitter in _options.Emitters)
                {
                    try
                    {
                        if (!emitter.CanEmit(recording)) continue;
                        await emitter.StartAsync(recording, mockOptions.ForEmitter(), logger, ct);
                        _startedEmitters.Add(emitter);
                        logger.LogInformation("mock-emitter started: {EmitterId}", emitter.Id);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "mock-emitter '{EmitterId}' failed to start; skipping.", emitter.Id);
                    }
                }
            }
        }

        private async Task<BowireRecording> LoadFromSchemaAsync(string kind, string path, CancellationToken ct)
        {
            var source = _options.SchemaSources.FirstOrDefault(s =>
                string.Equals(s.Kind, kind, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            {
                var pluginHint = kind switch
                {
                    "openapi"  => "Kuestenlogik.Bowire.Protocol.Rest",
                    "protobuf" => "Kuestenlogik.Bowire.Protocol.Grpc",
                    "graphql"  => "Kuestenlogik.Bowire.Protocol.GraphQL",
                    _ => "(third-party plugin)"
                };
                throw new InvalidOperationException(
                    $"No IBowireMockSchemaSource registered for schema kind '{kind}'. " +
                    $"Install the matching plugin ({pluginHint}) or supply " +
                    $"MockServerOptions.SchemaSources programmatically.");
            }
            return await source.BuildAsync(path, ct).ConfigureAwait(false);
        }

        private async Task StartTransportHostsAsync(
            BowireRecording recording, ILogger logger, CancellationToken ct)
        {
            if (_options.TransportHosts.Count == 0) return;

            var bindIp = System.Net.IPAddress.Parse(_options.Host);
            foreach (var host in _options.TransportHosts)
            {
                if (!host.ShouldStart(recording)) continue;

                var requestedPort = _options.TransportPorts.TryGetValue(host.Id, out var p) ? p : 0;
                var ctx = new MockTransportContext(
                    Host: bindIp,
                    RequestedPort: requestedPort,
                    ReplaySpeed: _options.ReplaySpeed,
                    Loop: _options.Loop,
                    Logger: logger);

                try
                {
                    var bound = await host.StartAsync(recording, ctx, ct).ConfigureAwait(false);
                    _transportPorts[host.Id] = bound;
                    _startedTransports.Add(host);
                    logger.LogInformation(
                        "mock-transport started: id={Id} port={Port}", host.Id, bound);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "mock-transport '{Id}' failed to start; mock continues without it.", host.Id);
                }
            }
        }

        private static int ResolveBoundPort(WebApplication app, int requestedPort)
        {
            if (requestedPort > 0) return requestedPort;

            var addressFeature = app.Services
                .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features
                .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
            var first = addressFeature?.Addresses.FirstOrDefault();
            if (first is null) return 0;

            // IServerAddressesFeature returns strings like "http://127.0.0.1:51234".
            var uri = new Uri(first);
            return uri.Port;
        }

        public async Task StopAsync(CancellationToken ct)
        {
            await DisposePluginEmittersAsync();
            await StopTransportHostsAsync(ct).ConfigureAwait(false);
            if (_app is not null) await _app.StopAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            await DisposePluginEmittersAsync();
            await StopTransportHostsAsync(CancellationToken.None).ConfigureAwait(false);
            if (_app is not null) await _app.DisposeAsync();
        }

        private async Task StopTransportHostsAsync(CancellationToken ct)
        {
            if (_startedTransports.Count == 0) return;
            for (var i = _startedTransports.Count - 1; i >= 0; i--)
            {
                try { await _startedTransports[i].StopAsync(ct).ConfigureAwait(false); }
                catch { /* host owns cleanup; swallow to finish shutdown */ }
            }
            _startedTransports.Clear();
            _transportPorts.Clear();
        }

        private async Task DisposePluginEmittersAsync()
        {
            if (_startedEmitters.Count == 0) return;
            // Dispose in reverse order of startup so later emitters
            // that may depend on earlier ones see the right lifetime.
            for (var i = _startedEmitters.Count - 1; i >= 0; i--)
            {
                try { await _startedEmitters[i].DisposeAsync(); }
                catch { /* emitters own cleanup; swallow to finish host shutdown */ }
            }
            _startedEmitters.Clear();
        }
    }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mock.Chaos;
using Kuestenlogik.Bowire.Mock.Management;
using Kuestenlogik.Bowire.Mock.Matchers;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Mock;

/// <summary>
/// Tunables for <c>MapBowireMock</c> in embedded (middleware) mode.
/// </summary>
public sealed class MockOptions
{
    /// <summary>
    /// When <c>true</c> (default), incoming requests that don't match any
    /// recorded step fall through to the rest of the ASP.NET pipeline.
    /// Set to <c>false</c> to return <c>404</c> on a miss — useful when the
    /// mock is meant to be the only responder for this route prefix.
    /// </summary>
    public bool PassThroughOnMiss { get; set; } = true;

    /// <summary>
    /// #407: base URL of a real upstream to forward unmatched requests to
    /// (WireMock's <c>proxyBaseUrl</c>) — enables partial mocking: mock the
    /// stubs you have, proxy everything else to the live service. The target is
    /// fixed by this config (never taken from the request), so the mock can't
    /// be turned into an open relay. A matched stub can also opt into proxying
    /// via <see cref="Mocking.BowireRecordingStep.Proxy"/>. Null = no proxy.
    /// </summary>
    public string? ProxyBaseUrl { get; set; }

    /// <summary>
    /// #407: optional <see cref="HttpClient"/> used for proxy forwarding.
    /// Injectable for embedded hosts / tests; defaults to a shared reusable
    /// client (the recommended HttpClient pattern) when null.
    /// </summary>
    public HttpClient? ProxyHttpClient { get; set; }

    /// <summary>
    /// When <c>true</c> (default), the recording file is watched with
    /// <see cref="Loading.RecordingWatcher"/> and reloaded on change. Tests
    /// usually want this off to avoid reload-vs-assert races.
    /// </summary>
    public bool Watch { get; set; } = true;

    /// <summary>
    /// Optional recording name or id — disambiguates a file that contains
    /// multiple recordings (e.g. the full <c>~/.bowire/recordings.json</c>
    /// store). Ignored when the file contains a single recording.
    /// </summary>
    public string? Select { get; set; }

    /// <summary>
    /// Matcher used to pair incoming requests with recorded steps. Defaults
    /// to <see cref="ExactMatcher"/>; replace with a Phase-2 path / topic
    /// matcher once those ship.
    /// </summary>
    public IMockMatcher Matcher { get; set; } = new ExactMatcher();

    /// <summary>
    /// Optional logger for the middleware. When <c>null</c>, the middleware
    /// resolves one from the request's <c>IServiceProvider</c>.
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// Speed multiplier for streaming replay (Phase 2c onwards). <c>1.0</c>
    /// preserves the original cadence captured on the per-frame
    /// <c>timestampMs</c>; <c>2.0</c> is twice as fast; <c>0</c> emits every
    /// frame immediately. Non-positive values other than <c>0</c> are treated
    /// as <c>0</c>.
    /// </summary>
    public double ReplaySpeed { get; set; } = 1.0;

    /// <summary>
    /// Chaos-injection tunables — latency jitter and fail-rate applied
    /// before dispatch to the replayer (Phase 3a). Defaults are off.
    /// </summary>
    public ChaosOptions Chaos { get; set; } = new();

    /// <summary>
    /// Per-method fault-injection rules (#170) — latency distributions,
    /// error rates, partial responses, connection drops. Evaluated after
    /// <see cref="Chaos"/> on matched steps. Defaults to an empty set
    /// (off).
    /// </summary>
    public FaultRuleSet Faults { get; set; } = new();

    /// <summary>
    /// When <c>true</c>, the mock advances through the recording's steps
    /// in order — only the step at the current cursor position is eligible
    /// to match, step N+1 can only reply after step N has been hit
    /// (Phase 3b). Defaults to <c>false</c> (stateless — any step may match
    /// any time). Pairs with <see cref="StatefulWrapAround"/> for
    /// end-of-recording behaviour.
    /// </summary>
    public bool Stateful { get; set; }

    /// <summary>
    /// When <see cref="Stateful"/> is on and the cursor has advanced past
    /// the last step, <c>true</c> (default) wraps back to step 0 so the
    /// recording loops; <c>false</c> returns the configured miss
    /// (<see cref="PassThroughOnMiss"/>) for every subsequent request.
    /// </summary>
    public bool StatefulWrapAround { get; set; } = true;

    /// <summary>
    /// When set, unmatched REST requests are appended as placeholder steps
    /// to the named file (Phase 3c). Use this to turn an exercised mock
    /// into the seed of a new recording — run the client, collect the
    /// misses, fill in the missing responses. <c>null</c> disables capture
    /// (default). gRPC misses are skipped because their binary bodies
    /// can't be faithfully captured as text.
    /// </summary>
    public string? CaptureMissPath { get; set; }

    /// <summary>
    /// Shared secret for the runtime scenario-switch control endpoint
    /// (<c>POST /__bowire/mock/scenario</c>). When <c>null</c> (default)
    /// the control endpoints return <c>404</c> — not advertised, not
    /// reachable. When set, the endpoint compares the value against the
    /// <c>X-Bowire-Mock-Token</c> header and responds <c>401</c> on a
    /// mismatch. Pick a non-trivial value even in dev; a compromised
    /// CI pipeline shouldn't be able to re-point the mock at arbitrary
    /// files on the host.
    /// </summary>
    public string? ControlToken { get; set; }

    /// <summary>
    /// When <c>true</c>, proactive emitters (the built-in MQTT one and
    /// plugin-contributed ones) replay their step sequence on repeat.
    /// Propagated from <see cref="MockServerOptions.Loop"/> so emitters
    /// don't have to reach into the outer server options to learn it.
    /// Has no effect on request-driven replay paths.
    /// </summary>
    public bool Loop { get; set; }

    /// <summary>
    /// Optional sink that receives one <see cref="MockRequestEntry"/>
    /// per request after the response is written (#57). The workbench-
    /// driven mock registry wires a bounded <see cref="MockRequestLog"/>
    /// here; embedded hosts that don't care leave it <c>null</c>. The
    /// observer's <see cref="IMockRequestObserver.OnRequest"/> is
    /// invoked from a <see cref="Microsoft.AspNetCore.Http.HttpResponse.OnCompleted(System.Func{System.Threading.Tasks.Task})"/>
    /// callback, so it runs out of the request hot path and exceptions
    /// it throws never bubble back to the client.
    /// </summary>
    public IMockRequestObserver? RequestObserver { get; set; }

    /// <summary>
    /// #404: invoked with the <see cref="MockHandler"/> just after
    /// <c>UseBowireMock</c> creates it, so a host (e.g. the standalone
    /// <see cref="MockServer"/>) can capture the handler and expose per-stub
    /// CRUD on a running mock. Null for embedded hosts that don't need it.
    /// </summary>
    internal Action<MockHandler>? OnHandlerCreated { get; set; }

    /// <summary>
    /// Project this full options bag down to the slim
    /// <see cref="MockEmitterOptions"/> shape that plugin-contributed
    /// emitters consume. The server-only knobs (matchers, chaos,
    /// stateful cursor, control-token, miss-capture path) don't apply
    /// to proactive emitters and are deliberately excluded so plugins
    /// never have to reason about them.
    /// </summary>
    public MockEmitterOptions ForEmitter() => new()
    {
        ReplaySpeed = ReplaySpeed,
        Loop = Loop,
    };
}

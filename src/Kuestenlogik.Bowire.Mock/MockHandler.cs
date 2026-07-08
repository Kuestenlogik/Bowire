// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Kuestenlogik.Bowire.Mock.Chaos;
using Kuestenlogik.Bowire.Mock.Management;
using Kuestenlogik.Bowire.Mock.Matchers;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Mock.Replay;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Mock;

/// <summary>
/// Per-pipeline handler that owns the currently-active recording and
/// replays matching unary steps. Created once by <c>UseBowireMock</c> and
/// shared across every request on the pipeline — the recording reference
/// is swapped atomically by the file watcher on hot-reload.
/// </summary>
public sealed class MockHandler
{
    private readonly MockOptions _options;
    private readonly ILogger _logger;
    private BowireRecording _recording;

    // #404 stub CRUD: mutations are copy-on-write — each op builds a new
    // recording and swaps the _recording reference atomically, so an in-flight
    // match (which captured the old reference) keeps iterating a stable list.
    // _baseline is the recording to restore on reset.
    private readonly object _stubLock = new();
    private BowireRecording _baseline;

    // Strip CR/LF from user-controlled values before formatting them
    // into log messages — stops attackers from smuggling fake log lines
    // via crafted request paths/methods.
    private static string SafeLog(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty
            : s.Replace('\r', '_').Replace('\n', '_');

    // Phase 3b: stateful-mode cursor + its lock. The lock is held only
    // across the read-check-advance sequence, never across replay I/O —
    // so concurrent requests serialise on cursor ordering but the
    // responses themselves go out in parallel.
    private readonly object _cursorLock = new();
    private int _cursor;

    // #408 named-scenario state machine: scenario name → current state.
    // Absent = the initial state (Started). Reset on hot-reload.
    private const string ScenarioStartState = "Started";
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _scenarioStates =
        new(StringComparer.Ordinal);

    // Runtime-scenario-switch (control endpoint) state. When the mock
    // was mounted from a path, we remember it so the control endpoint
    // can reload a different file (or re-select a named recording in
    // the same multi-recording store).
    private readonly string? _recordingPath;
    private string? _currentSelect;

    public MockHandler(BowireRecording recording, MockOptions options, ILogger logger, string? recordingPath = null)
    {
        _recording = recording;
        _baseline = recording;
        _options = options;
        _logger = logger;
        _recordingPath = recordingPath;
        _currentSelect = options.Select;
    }

    /// <summary>
    /// Swap the in-memory recording. Called by <see cref="Loading.RecordingWatcher"/>
    /// when the source file changes. Resets the stateful cursor because the
    /// new file defines a fresh step sequence — carrying the old index over
    /// would almost certainly land on a different step than the user meant.
    /// </summary>
    public void ReplaceRecording(BowireRecording next)
    {
        lock (_stubLock)
        {
            _recording = next;
            _baseline = next; // hot-reload resets the stub-CRUD baseline too
        }
        lock (_cursorLock) { _cursor = 0; }
        _scenarioStates.Clear(); // #408: fresh recording → scenarios back to Started
    }

    // ---- #404: per-stub CRUD on a running mock ----
    // A "stub" is a BowireRecordingStep (it already carries the #402/#403 match
    // predicates + the response fields). These let an operator add / edit /
    // remove individual stubs at runtime instead of restarting the mock.

    /// <summary>Snapshot of the current stubs (recording steps).</summary>
    public IReadOnlyList<BowireRecordingStep> ListStubs() => _recording.Steps.ToArray();

    /// <summary>Find a stub by id, or null.</summary>
    public BowireRecordingStep? GetStub(string id) =>
        _recording.Steps.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    /// <summary>Append a stub (assigning an id when it has none). Returns the stored stub.</summary>
    public BowireRecordingStep AddStub(BowireRecordingStep stub)
    {
        ArgumentNullException.ThrowIfNull(stub);
        if (string.IsNullOrEmpty(stub.Id)) stub.Id = "stub_" + Guid.NewGuid().ToString("N")[..12];
        lock (_stubLock)
        {
            var steps = new List<BowireRecordingStep>(_recording.Steps) { stub };
            _recording = CloneWithSteps(_recording, steps);
        }
        return stub;
    }

    /// <summary>Replace the stub with the given id (id preserved). False when absent.</summary>
    public bool UpdateStub(string id, BowireRecordingStep stub)
    {
        ArgumentNullException.ThrowIfNull(stub);
        lock (_stubLock)
        {
            var steps = _recording.Steps.ToList();
            var idx = steps.FindIndex(s => string.Equals(s.Id, id, StringComparison.Ordinal));
            if (idx < 0) return false;
            stub.Id = id; // the id is the URL key — keep it stable across edits
            steps[idx] = stub;
            _recording = CloneWithSteps(_recording, steps);
        }
        return true;
    }

    /// <summary>Remove the stub with the given id. False when absent.</summary>
    public bool RemoveStub(string id)
    {
        lock (_stubLock)
        {
            var steps = _recording.Steps.ToList();
            if (steps.RemoveAll(s => string.Equals(s.Id, id, StringComparison.Ordinal)) == 0) return false;
            _recording = CloneWithSteps(_recording, steps);
        }
        return true;
    }

    /// <summary>Restore the stubs to the baseline recording (as loaded / last hot-reloaded).</summary>
    public void ResetStubs()
    {
        lock (_stubLock)
        {
            _recording = CloneWithSteps(_baseline, _baseline.Steps.ToList());
        }
        lock (_cursorLock) { _cursor = 0; }
    }

    // Shallow-clone a recording with a fresh step list (copy-on-write). Copies
    // the scalar metadata the mock cares about; a new field on BowireRecording
    // that must survive stub CRUD needs adding here.
    private static BowireRecording CloneWithSteps(BowireRecording src, IList<BowireRecordingStep> steps)
    {
        var clone = new BowireRecording
        {
            Id = src.Id,
            Name = src.Name,
            Description = src.Description,
            CreatedAt = src.CreatedAt,
            RecordingFormatVersion = src.RecordingFormatVersion,
            SchemaSnapshot = src.SchemaSnapshot,
            SourceSchema = src.SourceSchema,
            Attack = src.Attack,
            Vulnerability = src.Vulnerability,
            VulnerableWhen = src.VulnerableWhen,
        };
        foreach (var s in steps) clone.Steps.Add(s);
        return clone;
    }

    // ---- #408: named-scenario state machine ----

    private bool RecordingHasScenarios()
    {
        foreach (var s in _recording.Steps)
            if (s.Scenario is not null && !string.IsNullOrEmpty(s.Scenario.Name)) return true;
        return false;
    }

    private string ScenarioState(string name) =>
        _scenarioStates.GetValueOrDefault(name, ScenarioStartState);

    // A recording view containing only the stubs whose scenario gate is open in
    // the current state (plus every state-independent stub). Copy-on-write, so
    // concurrent matches see a stable list.
    private BowireRecording BuildScenarioView()
    {
        var eligible = new List<BowireRecordingStep>(_recording.Steps.Count);
        foreach (var s in _recording.Steps)
        {
            var sc = s.Scenario;
            if (sc is null || string.IsNullOrEmpty(sc.Name))
            {
                eligible.Add(s);
                continue;
            }
            var required = string.IsNullOrEmpty(sc.RequiredState) ? ScenarioStartState : sc.RequiredState;
            if (string.Equals(ScenarioState(sc.Name), required, StringComparison.Ordinal))
                eligible.Add(s);
        }
        return CloneWithSteps(_recording, eligible);
    }

    private void ApplyScenarioTransition(BowireRecordingStep step)
    {
        if (step.Scenario is { } sc && !string.IsNullOrEmpty(sc.Name) && !string.IsNullOrEmpty(sc.NewState))
        {
            _scenarioStates[sc.Name] = sc.NewState!;
            _logger.LogDebug("scenario-transition(name={Name}, newState={State})", sc.Name, sc.NewState);
        }
    }

    /// <summary>Current state of every scenario declared in the recording (name → state).</summary>
    public IReadOnlyDictionary<string, string> GetScenarioStates()
    {
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in _recording.Steps)
        {
            var name = s.Scenario?.Name;
            if (!string.IsNullOrEmpty(name)) states[name] = ScenarioState(name);
        }
        return states;
    }

    /// <summary>Force a scenario to a state. False when no stub declares that scenario name.</summary>
    public bool SetScenarioState(string name, string state)
    {
        if (!_recording.Steps.Any(s => string.Equals(s.Scenario?.Name, name, StringComparison.Ordinal)))
            return false;
        _scenarioStates[name] = state ?? ScenarioStartState;
        return true;
    }

    /// <summary>Reset every scenario back to its initial state (<c>Started</c>).</summary>
    public void ResetScenarios() => _scenarioStates.Clear();

    private const string MatchedStepIdItemKey = "__bowireMockMatchedStepId";
    private const string FaultItemKey = "__bowireMockFault";

    public async Task HandleAsync(HttpContext ctx, Func<Task> next)
    {
        // #57 request-log instrumentation. Hook OnCompleted at the top so
        // every return path below feeds the observer exactly once after
        // the response is written. The matched-step id (when there is
        // one) is stashed via HttpContext.Items by DispatchMatchedAsync
        // so we don't have to thread it back here through multiple early
        // returns. Cheap branch when no observer is registered.
        var observer = _options.RequestObserver;
        if (observer is not null)
        {
            var startTicks = Stopwatch.GetTimestamp();
            ctx.Response.OnCompleted(() =>
            {
                try
                {
                    var elapsedMs = (Stopwatch.GetTimestamp() - startTicks)
                        / (double)Stopwatch.Frequency * 1000.0;
                    var matchedStepId = ctx.Items.TryGetValue(MatchedStepIdItemKey, out var sid)
                        ? sid as string : null;
                    var outcome = matchedStepId is not null
                        ? "matched"
                        : ctx.Response.StatusCode == 404 ? "404" : "miss";
                    observer.OnRequest(new MockRequestEntry(
                        Sequence: 0, // overwritten by the sink
                        Timestamp: DateTimeOffset.UtcNow,
                        Method: ctx.Request.Method,
                        Path: ctx.Request.Path.Value ?? "/",
                        StatusCode: ctx.Response.StatusCode,
                        MatchedStepId: matchedStepId,
                        Outcome: outcome,
                        DurationMs: elapsedMs,
                        Fault: ctx.Items.TryGetValue(FaultItemKey, out var f) ? f as string : null,
                        // #409: retain query + headers so the verify API can
                        // assert on them (bounded — the log is a ring buffer).
                        Query: ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : null,
                        Headers: ctx.Request.Headers.ToDictionary(
                            h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase)));
                }
                catch
                {
                    // Observers must not break replay -- swallow.
                }
                return Task.CompletedTask;
            });
        }

        // Runtime scenario-switch control endpoint. Lives on a
        // distinctive prefix so it never collides with a recorded path.
        // Short-circuits ahead of step matching, chaos, and everything
        // else so an authorised operator can swap recordings mid-flight
        // without the swap interacting with replay state.
        var path = ctx.Request.Path.Value ?? "/";
        if (path.StartsWith("/__bowire/mock", StringComparison.Ordinal))
        {
            await HandleControlEndpointAsync(ctx, path);
            return;
        }

        // Miss-capture needs the request body to be readable twice (once
        // on miss to persist, potentially a second time downstream on
        // pass-through). Request-templating (${request.body.*}) also
        // reads the body and wants it positionable. EnableBuffering
        // switches the request stream to a seekable FileBufferingReadStream,
        // cheap when off and necessary when on.
        if (_options.CaptureMissPath is not null)
        {
            ctx.Request.EnableBuffering();
        }

        var contentType = ctx.Request.ContentType;
        var isGrpc = contentType is not null &&
            contentType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase);

        // #403: read the request body (buffered) only when the active
        // recording actually declares a body matcher, so ordinary scans don't
        // pay to read the stream. gRPC bodies are binary protobuf — skipped.
        string? requestBody = null;
        if (!isGrpc && ctx.Request.ContentLength is not 0 && RecordingHasBodyMatchers())
            requestBody = await ReadBufferedBodyAsync(ctx);

        var request = new MockRequest
        {
            Protocol = isGrpc ? "grpc" : "rest",
            HttpMethod = ctx.Request.Method,
            Path = ctx.Request.Path.Value ?? "/",
            Query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : null,
            Headers = ctx.Request.Headers.ToDictionary(
                h => h.Key,
                h => h.Value.ToString(),
                StringComparer.OrdinalIgnoreCase),
            ContentType = contentType,
            Body = requestBody,
        };

        var matched = TryMatch(request);
        if (matched is not null)
        {
            await DispatchMatchedAsync(ctx, matched, isGrpc);
            return;
        }

        // #411: fault-on-miss. A rule flagged `onMiss` fires on requests that
        // matched no stub — chaos-testing a client's handling of an unknown /
        // failing endpoint. A terminal kind (error / connection-drop /
        // malformed) short-circuits before pass-through / 404; latency-only
        // just delays and falls through to normal miss handling.
        if (_options.Faults.IsActive && _options.Faults.FirstMissMatch() is { } missFault)
        {
            if (await ApplyMissFaultAsync(ctx, missFault)) return;
        }

        // #407: forward-on-miss — when a proxy base URL is configured, an
        // unmatched request goes to the real upstream (partial mocking) rather
        // than falling through / 404ing.
        if (!string.IsNullOrEmpty(_options.ProxyBaseUrl))
        {
            await ForwardAsync(ctx, _options.ProxyBaseUrl);
            return;
        }

        // Only after the matcher missed do we defer to any endpoint
        // routing picked up — that's where gRPC Reflection (registered
        // by MockServer when the recording has proto descriptors)
        // takes over. Checking AFTER the matcher lets the mock shadow
        // the grpc-dotnet "Unimplemented" fallback endpoint for paths
        // that our own recording DOES have a step for.
        if (ctx.GetEndpoint() is not null)
        {
            await next();
            return;
        }

        if (_options.CaptureMissPath is string capturePath)
        {
            // Best-effort capture *before* the miss is propagated — so if
            // the downstream pass-through mutates the body stream, we've
            // already read it.
            await Capture.MissCaptureWriter.CaptureAsync(
                capturePath, ctx, _logger, ctx.RequestAborted);
        }

        if (_options.PassThroughOnMiss)
        {
            _logger.LogDebug(
                "no-match(path={Path}, method={Method}) → pass-through",
                SafeLog(request.Path), SafeLog(request.HttpMethod));
            await next();
            return;
        }

        _logger.LogWarning(
            "no-match(path={Path}, method={Method}) → 404",
            SafeLog(request.Path), SafeLog(request.HttpMethod));
        ctx.Response.StatusCode = 404;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(
            "{\"error\":\"No recorded step matches this request.\"}",
            ctx.RequestAborted);
    }

    private async Task HandleControlEndpointAsync(HttpContext ctx, string path)
    {
        // Token gate: when ControlToken isn't set, the whole control
        // surface is invisible (404 so we don't advertise it). When
        // set, every call must carry a matching X-Bowire-Mock-Token
        // header; mismatches return 401 with no further detail.
        if (string.IsNullOrEmpty(_options.ControlToken))
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        var presented = ctx.Request.Headers["X-Bowire-Mock-Token"].ToString();
        if (!string.Equals(presented, _options.ControlToken, StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(
                "{\"error\":\"Invalid or missing X-Bowire-Mock-Token header.\"}",
                ctx.RequestAborted);
            return;
        }

        if (path == "/__bowire/mock/status" && ctx.Request.Method == "GET")
        {
            await WriteControlStatusAsync(ctx);
            return;
        }
        if (path == "/__bowire/mock/scenario" && ctx.Request.Method == "POST")
        {
            await HandleScenarioSwitchAsync(ctx);
            return;
        }

        ctx.Response.StatusCode = 404;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(
            "{\"error\":\"Unknown mock control endpoint.\"}",
            ctx.RequestAborted);
    }

    private async Task WriteControlStatusAsync(HttpContext ctx)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            recording = new
            {
                id = _recording.Id,
                name = _recording.Name,
                stepCount = _recording.Steps.Count
            },
            source = new
            {
                path = _recordingPath,
                select = _currentSelect
            }
        });
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(payload, ctx.RequestAborted);
    }

    private async Task HandleScenarioSwitchAsync(HttpContext ctx)
    {
        ScenarioSwitchRequest? body;
        try
        {
            body = await System.Text.Json.JsonSerializer
                .DeserializeAsync<ScenarioSwitchRequest>(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        }
        catch (System.Text.Json.JsonException ex)
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(
                "{\"error\":\"Invalid JSON body: " + ex.Message.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"}",
                ctx.RequestAborted);
            return;
        }

        var resolved = ResolveScenarioPath(body?.Path, _recordingPath);
        if (resolved.Error is not null)
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(
                "{\"error\":\"" + resolved.Error.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"}",
                ctx.RequestAborted);
            return;
        }
        var targetPath = resolved.Path!;
        var targetSelect = body?.Name; // null is a valid "pick the only recording"

        BowireRecording newRecording;
        try
        {
            // CA3003 is suppressed deliberately: the path has already
            // been constrained to the initial recording's directory
            // subtree by ResolveScenarioPath above, so user input
            // can't reach arbitrary filesystem locations.
#pragma warning disable CA3003
            newRecording = Loading.RecordingLoader.Load(targetPath, targetSelect);
#pragma warning restore CA3003
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(
                "{\"error\":\"" + ex.Message.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"}",
                ctx.RequestAborted);
            return;
        }

        ReplaceRecording(newRecording);
        _currentSelect = targetSelect;

        _logger.LogInformation(
            "scenario-switch(path={Path}, select={Select}, stepCount={StepCount})",
            targetPath, targetSelect ?? "<default>", newRecording.Steps.Count);

        await WriteControlStatusAsync(ctx);
    }

    private sealed class ScenarioSwitchRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("path")]
        public string? Path { get; set; }
    }

    // Constrain user-supplied scenario paths to the directory tree of
    // the initial recording. Rejects absolute paths and `..` segments
    // that would escape the base directory — defence-in-depth on top
    // of the ControlToken gate. Returns the resolved absolute path on
    // success, or a human-readable error message.
    private static (string? Path, string? Error) ResolveScenarioPath(string? userPath, string? basePath)
    {
        // No path supplied → reload the same file (Select may change).
        if (string.IsNullOrWhiteSpace(userPath))
        {
            if (string.IsNullOrEmpty(basePath))
            {
                return (null, "No scenario path given and the mock wasn't mounted from a file path — cannot reload.");
            }
            return (basePath, null);
        }

        if (string.IsNullOrEmpty(basePath))
        {
            return (null, "Scenario path supplied, but the mock wasn't mounted from a file path — can't resolve a base directory for the swap.");
        }

        if (System.IO.Path.IsPathRooted(userPath))
        {
            return (null, "Scenario path must be relative to the initial recording's directory — absolute paths are not accepted.");
        }

        var baseDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(basePath));
        if (string.IsNullOrEmpty(baseDir))
        {
            return (null, "Could not determine the base directory of the initial recording.");
        }

        // Path.GetFullPath normalises `..` so we can compare the
        // result against the base directory prefix. This catches
        // `../outside.json` and `sub/../../outside.json` alike.
        var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, userPath));
        var baseDirWithSep = baseDir.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? baseDir
            : baseDir + System.IO.Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(baseDirWithSep, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidate, baseDir, StringComparison.OrdinalIgnoreCase))
        {
            return (null, "Scenario path would escape the initial recording's directory — rejected.");
        }

        return (candidate, null);
    }

    // In stateful mode only the step at the current cursor is eligible —
    // anything else is treated as a miss. A successful match advances the
    // cursor (wrapping to 0 when configured) so the next request looks at
    // step N+1. In stateless mode this delegates to the matcher unchanged.
    private BowireRecordingStep? TryMatch(MockRequest request)
    {
        if (!_options.Stateful)
        {
            // #408: when the recording uses named scenarios, match only against
            // the stubs whose scenario gate is currently open, then apply the
            // matched stub's state transition.
            var source = RecordingHasScenarios() ? BuildScenarioView() : _recording;
            if (_options.Matcher.TryMatch(request, source, out var step))
            {
                ApplyScenarioTransition(step);
                return step;
            }
            return null;
        }

        lock (_cursorLock)
        {
            var steps = _recording.Steps;
            if (steps.Count == 0) return null;

            if (_cursor >= steps.Count)
            {
                if (!_options.StatefulWrapAround) return null;
                _cursor = 0;
            }

            var candidate = steps[_cursor];
            // Build a one-step window recording so the matcher's existing
            // REST/gRPC logic runs against exactly this candidate without
            // a per-matcher "match a single step" API.
            var window = new BowireRecording { Steps = { candidate } };
            if (_options.Matcher.TryMatch(request, window, out _))
            {
                _cursor++;
                _logger.LogDebug(
                    "stateful-advance(step={StepId}, nextCursor={Cursor}/{Total})",
                    candidate.Id, _cursor, steps.Count);
                return candidate;
            }

            _logger.LogDebug(
                "stateful-miss(expectedStep={StepId}, cursor={Cursor}/{Total}, path={Path})",
                candidate.Id, _cursor, steps.Count, request.Path);
            return null;
        }
    }

    private async Task DispatchMatchedAsync(HttpContext ctx, BowireRecordingStep step, bool isGrpc)
    {
        // Stash the step id so the OnCompleted observer in HandleAsync can
        // tag the request-log entry with which recorded step replied.
        ctx.Items[MatchedStepIdItemKey] = step.Id;

        // #407: a stub can forward to a real upstream instead of replaying —
        // partial mocking (mock some endpoints, proxy the rest).
        if (!string.IsNullOrEmpty(step.Proxy))
        {
            await ForwardAsync(ctx, step.Proxy);
            return;
        }

        // Chaos injection (Phase 3a): apply latency jitter and fail-rate
        // *after* we've matched a step (so unmatched traffic still
        // surfaces the miss cleanly) but *before* dispatch (so streaming
        // replays start with the chaotic first-byte delay instead of
        // mid-stream). A fail-rate hit short-circuits the replayer — the
        // step doesn't run at all for this request.
        var chaos = _options.Chaos;
        if (chaos.IsActive)
        {
            if (chaos.LatencyMinMs is int lo && chaos.LatencyMaxMs is int hi)
            {
                var delayMs = lo == hi ? lo : lo + (int)Math.Floor(FaultRandom.NextDouble() * (hi - lo + 1));
                if (delayMs > 0)
                {
                    try { await Task.Delay(delayMs, ctx.RequestAborted); }
                    catch (OperationCanceledException) { return; }
                }
            }

            if (chaos.FailRate > 0 && FaultRandom.NextDouble() < chaos.FailRate)
            {
                ctx.Response.StatusCode = chaos.FailStatusCode;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync(
                    $"{{\"error\":\"chaos: simulated failure ({chaos.FailStatusCode})\"}}",
                    ctx.RequestAborted);
                _logger.LogInformation(
                    "chaos-fail(step={StepId}, statusCode={StatusCode})",
                    step.Id, chaos.FailStatusCode);
                return;
            }
        }

        // Per-method fault rules (#170) — the structured successor to the
        // global knobs above. First enabled rule whose method glob matches
        // the step handles the request: its latency shape always applies,
        // its fault kind fires at the configured rate. Every injection is
        // recorded via FaultItemKey so the request log carries the audit
        // trail.
        if (_options.Faults.IsActive
            && _options.Faults.FirstMatch(step.Service, step.Method) is { } fault)
        {
            if (fault.Latency is not null)
            {
                var delayMs = fault.Latency.SampleMs(FaultRandom.NextDouble);
                if (delayMs > 0)
                {
                    ctx.Items[FaultItemKey] = $"latency {delayMs}ms ({fault.Latency.Describe()})";
                    try { await Task.Delay(delayMs, ctx.RequestAborted); }
                    catch (OperationCanceledException) { return; }
                }
            }

            if (fault.Kind != FaultKind.LatencyOnly
                && (fault.Rate >= 1.0 || FaultRandom.NextDouble() < fault.Rate))
            {
                ctx.Items[FaultItemKey] = fault.Describe();
                _logger.LogInformation(
                    "fault(step={StepId}, service={Service}, method={Method}, fault={Fault})",
                    step.Id, step.Service, step.Method, fault.Describe());

                switch (fault.Kind)
                {
                    case FaultKind.Error:
                        ctx.Response.StatusCode = fault.ErrorStatusCode;
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        await ctx.Response.WriteAsync(
                            $"{{\"error\":\"fault: simulated failure ({fault.ErrorStatusCode})\"}}",
                            ctx.RequestAborted);
                        return;

                    case FaultKind.ConnectionDrop when fault.PartialBytes == 0:
                        ctx.Abort();
                        return;

                    case FaultKind.MalformedResponse:
                        // Emit garbage instead of the recorded body — the client
                        // parses nonsense (#411).
                        await WriteMalformedAsync(ctx, fault);
                        return;

                    case FaultKind.PartialResponse:
                    case FaultKind.ConnectionDrop:
                        // The replayer writes the full recorded body as
                        // always; the wrapper forwards only the first
                        // PartialBytes. Partial-response then ends the
                        // response cleanly (truncated body), connection-
                        // drop aborts the socket mid-body.
                        ctx.Response.Body = new TruncatingResponseStream(
                            ctx.Response.Body,
                            fault.PartialBytes,
                            fault.Kind == FaultKind.ConnectionDrop ? ctx.Abort : null);
                        break;
                }
            }
        }

        // Build the request template for ${request.*} substitution.
        // Only the REST/SSE paths consume it today; gRPC responses are
        // binary protobuf and don't mix with text substitution, and
        // WebSocket/SignalR replays use the upgrade context only
        // (no body to buffer). Skipping the body-read for gRPC avoids
        // buffering protobuf messages we'd never use.
        var requestTemplate = await BuildRequestTemplateAsync(ctx, step, isGrpc);

        var statusCode = await UnaryReplayer.ReplayAsync(
            ctx, step, _options, _logger, requestTemplate, ctx.RequestAborted);
        _logger.LogInformation(
            "match(step={StepId}, protocol={Protocol}, service={Service}, method={Method}) → {StatusCode}",
            step.Id, step.Protocol, step.Service, step.Method, statusCode);
    }

    // #411: apply an `onMiss` fault rule to an unmatched request. Returns true
    // when the response was terminated (error / drop / malformed / cancelled),
    // false when only latency ran and normal miss handling should continue.
    private async Task<bool> ApplyMissFaultAsync(HttpContext ctx, Chaos.FaultRule fault)
    {
        if (fault.Latency is not null)
        {
            var delayMs = fault.Latency.SampleMs(FaultRandom.NextDouble);
            if (delayMs > 0)
            {
                ctx.Items[FaultItemKey] = $"on-miss latency {delayMs}ms";
                try { await Task.Delay(delayMs, ctx.RequestAborted); }
                catch (OperationCanceledException) { return true; }
            }
        }

        if (fault.Kind == Chaos.FaultKind.LatencyOnly) return false;
        if (fault.Rate < 1.0 && FaultRandom.NextDouble() >= fault.Rate) return false;

        ctx.Items[FaultItemKey] = fault.Describe();
        _logger.LogInformation("fault-on-miss(path={Path}, fault={Fault})",
            SafeLog(ctx.Request.Path.Value), fault.Describe());

        switch (fault.Kind)
        {
            case Chaos.FaultKind.Error:
                ctx.Response.StatusCode = fault.ErrorStatusCode;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync(
                    $"{{\"error\":\"fault: unmatched request ({fault.ErrorStatusCode})\"}}",
                    ctx.RequestAborted);
                return true;

            case Chaos.FaultKind.ConnectionDrop:
                ctx.Abort();
                return true;

            case Chaos.FaultKind.MalformedResponse:
                await WriteMalformedAsync(ctx, fault);
                return true;

            default:
                // PartialResponse has no recorded body to truncate on a miss.
                return false;
        }
    }

    // #411: emit garbage bytes under a JSON content-type — the recorded body is
    // never written, so a client expecting JSON parses nonsense. 0xFF bytes are
    // invalid UTF-8 and invalid JSON, so the corruption is deterministic.
    private static async Task WriteMalformedAsync(HttpContext ctx, Chaos.FaultRule fault)
    {
        var n = Math.Clamp(fault.PartialBytes, 1, 64 * 1024);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength = n;
        var garbage = new byte[n];
        Array.Fill(garbage, (byte)0xFF);
        await ctx.Response.Body.WriteAsync(garbage, ctx.RequestAborted);
    }

    // ---- #407: upstream proxy ----

    // A shared client — reusing one HttpClient is the recommended pattern
    // (per-request clients exhaust sockets). Not a static "service": it's a
    // general-purpose forwarder, and embedded hosts can inject their own via
    // MockOptions.ProxyHttpClient.
    private static readonly HttpClient s_sharedProxyClient =
        new(new HttpClientHandler { AllowAutoRedirect = false, CheckCertificateRevocationList = true });

    // Hop-by-hop / framing headers Kestrel manages — never copied from upstream.
    private static readonly HashSet<string> s_proxyStripHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Transfer-Encoding", "Connection", "Keep-Alive", "Content-Length", "Server", "Date" };

    // #430 record-through: serialise appends to the capture file. A plain lock
    // + synchronous file I/O (short, opt-in dev path) — no disposable field.
    private readonly object _proxyRecordLock = new();
    private static readonly JsonSerializerOptions s_recordJson = new() { WriteIndented = true };

    private async Task ForwardAsync(HttpContext ctx, string targetBaseUrl)
    {
        var client = _options.ProxyHttpClient ?? s_sharedProxyClient;
        var target = CombineProxyUrl(targetBaseUrl, ctx.Request.Path.Value, ctx.Request.QueryString.Value);
        using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);

        if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
            req.Content = new StreamContent(ctx.Request.Body);

        foreach (var h in ctx.Request.Headers)
        {
            if (string.Equals(h.Key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
            var values = h.Value.ToArray();
            if (!req.Headers.TryAddWithoutValidation(h.Key, values))
                req.Content?.Headers.TryAddWithoutValidation(h.Key, values);
        }

        try
        {
            using var upstream = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
            ctx.Response.StatusCode = (int)upstream.StatusCode;
            foreach (var h in upstream.Headers)
                if (!s_proxyStripHeaders.Contains(h.Key)) ctx.Response.Headers[h.Key] = h.Value.ToArray();
            foreach (var h in upstream.Content.Headers)
                if (!s_proxyStripHeaders.Contains(h.Key)) ctx.Response.Headers[h.Key] = h.Value.ToArray();

            if (string.IsNullOrEmpty(_options.ProxyRecordPath))
            {
                await upstream.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            }
            else
            {
                // #430 record-through: buffer the body so it can be both served
                // and persisted as a stub.
                var bodyText = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted);
                await ctx.Response.WriteAsync(bodyText, ctx.RequestAborted);
                AppendProxiedStep(ctx, (int)upstream.StatusCode, bodyText);
            }
            _logger.LogDebug("proxy(path={Path}, status={Status})", SafeLog(ctx.Request.Path.Value), (int)upstream.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ctx.RequestAborted.IsCancellationRequested)
        {
            ctx.Response.StatusCode = 502;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(
                $"{{\"error\":\"proxy: upstream unreachable ({ex.GetType().Name})\"}}", ctx.RequestAborted);
        }
    }

    // #430: append a proxied response to the record-through file (load-or-create
    // a single BowireRecording, add a stub, save). Serialised by a semaphore so
    // concurrent proxied requests don't corrupt the file. Best-effort — a write
    // failure never breaks the response the client already received.
    private void AppendProxiedStep(HttpContext ctx, int statusCode, string body)
    {
        var path = _options.ProxyRecordPath!;
        var method = ctx.Request.Method;
        var reqPath = ctx.Request.Path.Value ?? "/";
        lock (_proxyRecordLock)
        {
            try
            {
                var recording = File.Exists(path)
                    ? JsonSerializer.Deserialize<BowireRecording>(File.ReadAllText(path), s_recordJson) ?? NewCaptureRecording()
                    : NewCaptureRecording();

                recording.Steps.Add(new BowireRecordingStep
                {
                    Id = "proxy_" + Guid.NewGuid().ToString("N")[..8],
                    Protocol = "rest",
                    MethodType = "Unary",
                    HttpVerb = method,
                    HttpPath = reqPath,
                    Status = statusCode is >= 200 and < 300 ? "OK" : statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Response = body,
                });

                File.WriteAllText(path, JsonSerializer.Serialize(recording, s_recordJson));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(ex, "record-through: could not append to {Path}", SafeLog(path));
            }
        }
    }

    private static BowireRecording NewCaptureRecording() => new()
    {
        Id = "rec_proxy_capture",
        Name = "proxy capture",
        Description = "Recorded by the mock's record-through proxy (#430).",
        RecordingFormatVersion = 2,
    };

    private static string CombineProxyUrl(string baseUrl, string? path, string? query)
    {
        var b = baseUrl.TrimEnd('/');
        var p = string.IsNullOrEmpty(path) ? "/" : (path.StartsWith('/') ? path : "/" + path);
        return b + p + (query ?? "");
    }

    /// <summary>Whether any step in the active recording declares a body matcher (#403).</summary>
    private bool RecordingHasBodyMatchers()
    {
        foreach (var s in _recording.Steps)
            if (s.Match?.Body is { Count: > 0 }) return true;
        return false;
    }

    /// <summary>
    /// Read the request body into a string (bounded at 1 MiB) with buffering
    /// enabled so downstream replayers / templating can re-read it, then rewind
    /// the stream. Shared by the body-matcher path and request templating.
    /// </summary>
    internal static async Task<string?> ReadBufferedBodyAsync(HttpContext ctx)
    {
        ctx.Request.EnableBuffering();
        using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
        var chars = new char[1024 * 1024];
        var total = 0;
        int read;
        while (total < chars.Length &&
               (read = await reader.ReadAsync(chars.AsMemory(total, chars.Length - total), ctx.RequestAborted)) > 0)
        {
            total += read;
        }
        var body = total > 0 ? new string(chars, 0, total) : "";
        ctx.Request.Body.Position = 0;
        return body;
    }

    private static async Task<Replay.RequestTemplate?> BuildRequestTemplateAsync(
        HttpContext ctx, BowireRecordingStep step, bool isGrpc)
    {
        string? body = null;
        if (!isGrpc && ctx.Request.ContentLength is not 0)
        {
            // Buffer the body once so the substitutor can read it
            // without consuming the stream that downstream replayers
            // (pass-through etc.) may still want. Cap matches
            // MissCaptureWriter so extremely large uploads don't
            // balloon memory — 1 MiB is plenty for mock-test payloads.
            ctx.Request.EnableBuffering();
            using var reader = new StreamReader(
                ctx.Request.Body,
                leaveOpen: true);
            var chars = new char[1024 * 1024];
            var total = 0;
            int read;
            while (total < chars.Length &&
                   (read = await reader.ReadAsync(chars.AsMemory(total, chars.Length - total), ctx.RequestAborted)) > 0)
            {
                total += read;
            }
            body = total > 0 ? new string(chars, 0, total) : "";
            ctx.Request.Body.Position = 0;
        }

        IReadOnlyDictionary<string, string>? templateBindings = null;
        if (!string.IsNullOrEmpty(step.HttpPath))
        {
            templateBindings = Matchers.ExactMatcher.ExtractTemplateBindings(
                step.HttpPath, ctx.Request.Path.Value ?? "");
        }

        return new Replay.RequestTemplate(ctx, body, templateBindings);
    }
}

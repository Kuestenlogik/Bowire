// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

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

    // Runtime-scenario-switch (control endpoint) state. When the mock
    // was mounted from a path, we remember it so the control endpoint
    // can reload a different file (or re-select a named recording in
    // the same multi-recording store).
    private readonly string? _recordingPath;
    private string? _currentSelect;

    public MockHandler(BowireRecording recording, MockOptions options, ILogger logger, string? recordingPath = null)
    {
        _recording = recording;
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
        _recording = next;
        lock (_cursorLock) { _cursor = 0; }
    }

    public async Task HandleAsync(HttpContext ctx, Func<Task> next)
    {
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
            // Phase 1's exact matcher doesn't inspect the body; left null to
            // avoid reading the request stream for nothing. Phase 2's
            // body-aware matcher will populate this when it ships.
            Body = null
        };

        var matched = TryMatch(request);
        if (matched is not null)
        {
            await DispatchMatchedAsync(ctx, matched, isGrpc);
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
            return _options.Matcher.TryMatch(request, _recording, out var step) ? step : null;
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
        // Chaos injection (Phase 3a): apply latency jitter and fail-rate
        // *after* we've matched a step (so unmatched traffic still
        // surfaces the miss cleanly) but *before* dispatch (so streaming
        // replays start with the chaotic first-byte delay instead of
        // mid-stream). A fail-rate hit short-circuits the replayer — the
        // step doesn't run at all for this request.
        //
        // CA5394: Random.Shared is deliberate — chaos jitter is
        // resilience-testing noise, not a security boundary.
#pragma warning disable CA5394
        var chaos = _options.Chaos;
        if (chaos.IsActive)
        {
            if (chaos.LatencyMinMs is int lo && chaos.LatencyMaxMs is int hi)
            {
                var delayMs = lo == hi ? lo : Random.Shared.Next(lo, hi + 1);
                if (delayMs > 0)
                {
                    try { await Task.Delay(delayMs, ctx.RequestAborted); }
                    catch (OperationCanceledException) { return; }
                }
            }

            if (chaos.FailRate > 0 && Random.Shared.NextDouble() < chaos.FailRate)
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
#pragma warning restore CA5394

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

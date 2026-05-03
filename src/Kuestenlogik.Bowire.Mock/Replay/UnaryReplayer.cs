// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Net;
using System.Text;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Mock.Replay;

/// <summary>
/// Writes a recorded unary response back onto an ASP.NET <see cref="HttpContext"/>.
/// Handles REST unary and gRPC unary; non-unary or unrecognised-protocol steps
/// get a structured <c>501</c> response so Phase-2 users see a clear error
/// instead of silent wrong behaviour.
/// </summary>
public static class UnaryReplayer
{
    /// <summary>
    /// Emit <paramref name="step"/> as the HTTP response on <paramref name="ctx"/>.
    /// </summary>
    /// <returns>
    /// The HTTP status code written — useful for the middleware's log line.
    /// </returns>
    public static Task<int> ReplayAsync(
        HttpContext ctx,
        BowireRecordingStep step,
        MockOptions options,
        ILogger logger,
        CancellationToken ct) => ReplayAsync(ctx, step, options, logger, request: null, ct);

    /// <summary>
    /// Full-featured overload with a <see cref="RequestTemplate"/> so
    /// the substitutor can resolve <c>${request.*}</c> placeholders
    /// against the live inbound request. Built by
    /// <see cref="MockHandler"/> after matching; tests can pass
    /// <c>null</c> to keep the pre-templating behaviour.
    /// </summary>
    public static async Task<int> ReplayAsync(
        HttpContext ctx,
        BowireRecordingStep step,
        MockOptions options,
        ILogger logger,
        RequestTemplate? request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        // Dispatch is chosen by wire-level characteristics, not by the
        // protocol label. Any step that carries an HTTP path + verb is
        // replayable through the REST (or SSE) path regardless of whether
        // the recorder tagged it as "rest", "odata", "mcp", etc. — those
        // are all HTTP-with-JSON on the wire. gRPC and WebSocket get their
        // own paths because their wire formats are distinctly different.
        var isGrpc = string.Equals(step.Protocol, "grpc", StringComparison.OrdinalIgnoreCase);
        var isWebSocket = string.Equals(step.Protocol, "websocket", StringComparison.OrdinalIgnoreCase);
        var isGraphQl = string.Equals(step.Protocol, "graphql", StringComparison.OrdinalIgnoreCase);
        var isSignalR = string.Equals(step.Protocol, "signalr", StringComparison.OrdinalIgnoreCase);
        var isSocketIo = string.Equals(step.Protocol, "socketio", StringComparison.OrdinalIgnoreCase);
        var hasHttpRouting = !string.IsNullOrEmpty(step.HttpPath) && !string.IsNullOrEmpty(step.HttpVerb);
        var isUnary = string.Equals(step.MethodType, "Unary", StringComparison.OrdinalIgnoreCase);
        var isServerStreaming = string.Equals(step.MethodType, "ServerStreaming", StringComparison.OrdinalIgnoreCase);
        var isClientStreaming = string.Equals(step.MethodType, "ClientStreaming", StringComparison.OrdinalIgnoreCase);
        var isDuplex = string.Equals(step.MethodType, "Duplex", StringComparison.OrdinalIgnoreCase);
        var isDuplexLike = isDuplex || isClientStreaming;

        // Protocol-specific replay paths come before the generic HTTP one so
        // a GraphQL subscription step (which also has httpPath set because
        // the upgrade URL is an HTTP path) doesn't fall through to the
        // SSE replayer. gRPC is checked first because a gRPC step never
        // has httpPath set; the ordering mostly matters for clarity.
        if (isGrpc && isUnary) return await ReplayGrpcAsync(ctx, step, logger, ct);
        if (isGrpc && isServerStreaming) return await ReplayGrpcStreamAsync(ctx, step, options, logger, ct);
        if (isGrpc && isClientStreaming) return await ReplayGrpcClientStreamAsync(ctx, step, logger, ct);
        if (isGrpc && isDuplex) return await ReplayGrpcBidiAsync(ctx, step, options, logger, ct);
        if (isWebSocket && (isDuplexLike || isServerStreaming))
            return await ReplayWebSocketAsync(ctx, step, options, logger, ct);
        if (isGraphQl && (isDuplexLike || isServerStreaming) && ctx.WebSockets.IsWebSocketRequest)
            return await ReplayGraphQlSubscriptionAsync(ctx, step, options, logger, ct);
        if (isSignalR && ctx.WebSockets.IsWebSocketRequest)
            return await ReplaySignalRAsync(ctx, step, options, logger, ct);
        if (isSocketIo && ctx.WebSockets.IsWebSocketRequest)
            return await ReplaySocketIoAsync(ctx, step, options, logger, ct);
        // SignalR /{hub}/negotiate is a plain POST → falls through to the
        // REST replayer if the recording captured it.
        if (hasHttpRouting && isUnary) return await ReplayRestAsync(ctx, step, request, ct);
        if (hasHttpRouting && isServerStreaming) return await ReplaySseAsync(ctx, step, options, logger, request, ct);

        return await Not501(ctx, logger,
            $"Step '{step.Id}' has protocol '{step.Protocol}' and methodType '{step.MethodType}'. " +
            $"Phase 2 of the mock server replays HTTP-style unary + SSE (any protocol label), " +
            $"gRPC unary / server-streaming, and WebSocket duplex; other combinations arrive in later phases.",
            ct);
    }

    private static async Task<int> ReplayRestAsync(
        HttpContext ctx, BowireRecordingStep step, RequestTemplate? request, CancellationToken ct)
    {
        var statusCode = MapStatus(step.Status);
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";

        // Dynamic-value substitution happens per request — ${uuid} and
        // ${now}-style placeholders in the recorded body are resolved
        // here so every replay sees fresh IDs / timestamps; ${request.*}
        // pulls live values out of the inbound request. gRPC skips
        // this because its response is binary protobuf; text
        // substitution would break the wire format.
        var body = ResponseBodySubstitutor.Substitute(
            step.Response ?? string.Empty, request, extraBindings: null);
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes, ct);

        return statusCode;
    }

    // gRPC unary response shape:
    //   HTTP/2 status 200
    //   Content-Type: application/grpc
    //   Body: 1 compression-flag byte + 4 big-endian length bytes + <length> bytes of protobuf
    //   Trailers: grpc-status: 0 (OK) or mapped status, optional grpc-message
    //
    // Trailers require HTTP/2 + StartAsync() must have flushed headers before
    // the body is written, otherwise Kestrel reports "trailers cannot be sent
    // after the response has started without buffering".
    private static async Task<int> ReplayGrpcAsync(
        HttpContext ctx, BowireRecordingStep step, ILogger logger, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(step.ResponseBinary))
        {
            return await Not501(ctx, logger,
                $"gRPC step '{step.Id}' has no 'responseBinary' — the recording was captured by a Bowire build that " +
                $"didn't yet emit wire bytes for gRPC calls (pre-v2 format). Re-record against a current Bowire.",
                ct);
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(step.ResponseBinary);
        }
        catch (FormatException ex)
        {
            return await Not501(ctx, logger,
                $"gRPC step '{step.Id}' has malformed base64 in 'responseBinary': {ex.Message}",
                ct);
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/grpc";
        // Ensure response headers are flushed before trailers are appended —
        // required for HTTP/2 trailer delivery.
        await ctx.Response.StartAsync(ct);

        // Write the length-prefixed message envelope.
        var frame = new byte[5 + payload.Length];
        frame[0] = 0; // compression flag: 0 = no compression
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(5));
        await ctx.Response.Body.WriteAsync(frame, ct);

        // Map the recorded status onto gRPC's numeric status code; OK = 0.
        // Bowire records statuses as the gRPC status-code enum name
        // (see GrpcInvoker: "ex.StatusCode.ToString()"), which we reverse here.
        var (grpcStatus, grpcMessage) = MapToGrpcStatus(step.Status);
        ctx.Response.AppendTrailer("grpc-status", grpcStatus.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(grpcMessage))
            ctx.Response.AppendTrailer("grpc-message", grpcMessage);

        return 200;
    }

    // SSE replay: write each recorded frame as a `data: <json>\n\n` event,
    // paced by the per-frame timestampMs. The captured envelope's outer
    // shape ({index, data, timestampMs}) is metadata for Bowire's own UI;
    // the wire output drops it and re-emits what the original backend
    // would have sent — just the inner `data` payload.
    //
    // Speed control: options.ReplaySpeed scales the delay between frames.
    //   1.0 (default) preserves the original cadence.
    //   2.0 doubles playback speed.
    //   0 (or anything <= 0) emits every frame immediately.
    private static async Task<int> ReplaySseAsync(
        HttpContext ctx, BowireRecordingStep step, MockOptions options, ILogger logger,
        RequestTemplate? request, CancellationToken ct)
    {
        var frames = step.ReceivedMessages;
        if (frames is null || frames.Count == 0)
        {
            return await Not501(ctx, logger,
                $"SSE step '{step.Id}' has no receivedMessages — the recording predates Phase-2c capture. " +
                $"Re-record against a current Bowire.",
                ct);
        }

        // SSE `Last-Event-ID` resume: browsers (and native SSE clients)
        // send this header when they reconnect after a drop. We skip
        // every frame whose Index is <= the header value so replay
        // picks up exactly one past where it left off. The browser
        // also accepts the value from a lowercased spelling; the
        // ASP.NET header collection is case-insensitive so one lookup
        // covers both.
        var resumeAfter = ParseLastEventId(ctx.Request.Headers["Last-Event-ID"].ToString());
        if (resumeAfter is int lastId)
        {
            logger.LogInformation(
                "sse-resume(step={StepId}, lastEventId={LastEventId}, totalFrames={Total})",
                step.Id, lastId, frames.Count);
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no"; // disable proxy buffering
        await ctx.Response.StartAsync(ct);

        var speed = options.ReplaySpeed;
        var pace = speed > 0;

        long lastTimestampMs = 0;
        foreach (var frame in frames)
        {
            ct.ThrowIfCancellationRequested();

            // Skip frames the client already saw. Frames are ordered
            // by capture time; their Index is a monotonic counter the
            // recorder assigned. Resuming "after" the last seen id
            // means emitting frames with Index > lastId.
            if (resumeAfter is int skipUpTo && frame.Index <= skipUpTo) continue;

            if (pace && frame.TimestampMs is long frameTs && frameTs > lastTimestampMs)
            {
                var waitMs = (long)((frameTs - lastTimestampMs) / speed);
                if (waitMs > 0)
                {
                    try { await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct); }
                    catch (OperationCanceledException) { return 200; }
                }
                lastTimestampMs = frameTs;
            }

            // Apply response-body substitution to the frame payload. Same
            // ${uuid} / ${now} / ... vocabulary as unary REST, so streamed
            // frames can carry dynamic per-request values.
            var rawPayload = SerializeFrameData(frame.Data);
            rawPayload = ResponseBodySubstitutor.Substitute(rawPayload, request, extraBindings: null);

            // When the recorder captured a native SSE stream (through
            // SseSubscriber), each frame is an envelope object carrying
            // the parsed id / event / retry / data fields. Unwrap it so
            // replay emits the same wire shape the original server sent
            // instead of wrapping the whole envelope inside one data:
            // line. When the payload isn't an envelope (arbitrary JSON
            // from a non-SSE-native source), fall back to the legacy
            // shape: `id: <index>\ndata: <payload>`.
            var eventText = FormatSseFrame(frame.Index, rawPayload);
            var eventBytes = Encoding.UTF8.GetBytes(eventText);
            await ctx.Response.Body.WriteAsync(eventBytes, ct);
            await ctx.Response.Body.FlushAsync(ct);
        }

        return 200;
    }

    // Convert a captured frame payload into SSE wire-format lines.
    // Recognises the SseEventPayload envelope shape from the recorder
    // (Id / Event / Data / Retry). Falls back to `id: <index>\ndata:
    // <raw>` for non-envelope payloads so old recordings still replay.
    internal static string FormatSseFrame(int frameIndex, string rawPayload)
    {
        var indexText = frameIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (!string.IsNullOrEmpty(rawPayload) && rawPayload[0] == '{')
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(rawPayload);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    LooksLikeSseEnvelope(doc.RootElement))
                {
                    return BuildSseLines(doc.RootElement, indexText);
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Not JSON — treat as opaque payload below.
            }
        }

        return "id: " + indexText + "\ndata: " + rawPayload + "\n\n";
    }

    // SseEventPayload is the record-shape positional type; System.Text.Json
    // serialises it with PascalCase names. Recognising both cases lets
    // other producers feed compatible envelopes without matching our
    // exact property casing.
    private static bool LooksLikeSseEnvelope(System.Text.Json.JsonElement obj) =>
        TryGetProperty(obj, "Data", out _) || TryGetProperty(obj, "data", out _);

    private static bool TryGetProperty(
        System.Text.Json.JsonElement obj, string name, out System.Text.Json.JsonElement value) =>
        obj.TryGetProperty(name, out value);

    private static string BuildSseLines(System.Text.Json.JsonElement env, string fallbackId)
    {
        var sb = new System.Text.StringBuilder();

        // id — prefer the recorded value, fall back to the monotonic
        // index so Last-Event-ID resume still works even for captures
        // that never set id: on the wire.
        var id = ReadStringField(env, "Id", "id");
        sb.Append("id: ").Append(string.IsNullOrEmpty(id) ? fallbackId : id).Append('\n');

        var evtName = ReadStringField(env, "Event", "event");
        if (!string.IsNullOrEmpty(evtName))
        {
            sb.Append("event: ").Append(evtName).Append('\n');
        }

        if (TryReadRetry(env, out var retryMs))
        {
            sb.Append("retry: ").Append(retryMs.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }

        var data = ReadDataField(env);
        // Multi-line data per the SSE spec gets one `data:` line per
        // source line. Single-line payloads (the common case for JSON)
        // fall through to one line.
        var split = data.Split('\n');
        foreach (var line in split)
        {
            sb.Append("data: ").Append(line).Append('\n');
        }

        sb.Append('\n');
        return sb.ToString();
    }

    private static string? ReadStringField(
        System.Text.Json.JsonElement obj, string pascalName, string camelName)
    {
        if (obj.TryGetProperty(pascalName, out var el) ||
            obj.TryGetProperty(camelName, out el))
        {
            if (el.ValueKind == System.Text.Json.JsonValueKind.String) return el.GetString();
            if (el.ValueKind == System.Text.Json.JsonValueKind.Null) return null;
        }
        return null;
    }

    private static bool TryReadRetry(System.Text.Json.JsonElement obj, out int retryMs)
    {
        retryMs = 0;
        if ((obj.TryGetProperty("Retry", out var el) ||
             obj.TryGetProperty("retry", out el)) &&
            el.ValueKind == System.Text.Json.JsonValueKind.Number &&
            el.TryGetInt32(out var parsed))
        {
            retryMs = parsed;
            return true;
        }
        return false;
    }

    // `data` can be any JSON type captured by SseSubscriber. Strings are
    // emitted verbatim (the original SSE `data:` line); numbers/bools/
    // objects/arrays are re-serialised to their JSON form so the data
    // line carries the wire content.
    private static string ReadDataField(System.Text.Json.JsonElement obj)
    {
        if (!obj.TryGetProperty("Data", out var el) &&
            !obj.TryGetProperty("data", out el))
        {
            return "";
        }

        return el.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => el.GetString() ?? "",
            System.Text.Json.JsonValueKind.Null => "",
            _ => el.GetRawText()
        };
    }

    // Parse the Last-Event-ID header. The spec lets it be any string;
    // our recorder always writes integers (frame.Index), so accept
    // those and silently ignore non-numeric values (no resume). An
    // empty or missing header → null = start from frame 0.
    private static int? ParseLastEventId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    // The frame payload in a BowireRecordingFrame is the deserialised JSON
    // value the client captured (via JSON.parse in recording.js). We need to
    // re-serialise it as the exact-string form the original wire carried.
    // System.Text.Json here does a canonical compact serialisation; the
    // original cadence still holds byte-for-byte for typical JSON payloads.
    private static string SerializeFrameData(object? data)
    {
        return data switch
        {
            null => "null",
            string s => s,
            System.Text.Json.JsonElement el => el.GetRawText(),
            _ => System.Text.Json.JsonSerializer.Serialize(data)
        };
    }

    // gRPC server-streaming replay. Same HTTP/2 envelope + grpc-status
    // trailer as unary gRPC, but the body is a sequence of length-prefixed
    // frames paced by each frame's timestampMs. Like REST streaming, the
    // outer capture envelope is stripped — clients see the same wire
    // sequence the original backend produced.
    private static async Task<int> ReplayGrpcStreamAsync(
        HttpContext ctx, BowireRecordingStep step, MockOptions options, ILogger logger, CancellationToken ct)
    {
        var frames = step.ReceivedMessages;
        if (frames is null || frames.Count == 0)
        {
            return await Not501(ctx, logger,
                $"gRPC streaming step '{step.Id}' has no receivedMessages — the recording predates Phase-2c capture. " +
                $"Re-record against a current Bowire.",
                ct);
        }

        // Sanity-check: every frame needs responseBinary. Without it we
        // have no way to emit the wire bytes, and encoding JSON→protobuf
        // dynamically isn't possible without a DynamicMessage equivalent.
        if (frames.Any(f => string.IsNullOrEmpty(f.ResponseBinary)))
        {
            return await Not501(ctx, logger,
                $"gRPC streaming step '{step.Id}' has frames without responseBinary — the recording predates Phase-2d capture. " +
                $"Re-record against a current Bowire.",
                ct);
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/grpc";
        await ctx.Response.StartAsync(ct);

        var speed = options.ReplaySpeed;
        var pace = speed > 0;

        long lastTimestampMs = 0;
        foreach (var frame in frames)
        {
            ct.ThrowIfCancellationRequested();

            if (pace && frame.TimestampMs is long frameTs && frameTs > lastTimestampMs)
            {
                var waitMs = (long)((frameTs - lastTimestampMs) / speed);
                if (waitMs > 0)
                {
                    try { await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct); }
                    catch (OperationCanceledException)
                    {
                        WriteGrpcStatusTrailer(ctx, status: 1, message: "Cancelled");
                        return 200;
                    }
                }
                lastTimestampMs = frameTs;
            }

            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(frame.ResponseBinary!);
            }
            catch (FormatException ex)
            {
                logger.LogWarning("gRPC streaming frame {Index} of step '{StepId}' has malformed base64: {Message}",
                    frame.Index, step.Id, ex.Message);
                continue; // skip this frame but keep the stream alive
            }

            var envelope = new byte[5 + payload.Length];
            envelope[0] = 0;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(envelope.AsSpan(1, 4), (uint)payload.Length);
            payload.CopyTo(envelope.AsSpan(5));

            await ctx.Response.Body.WriteAsync(envelope, ct);
            await ctx.Response.Body.FlushAsync(ct);
        }

        var (grpcStatus, grpcMessage) = MapToGrpcStatus(step.Status);
        WriteGrpcStatusTrailer(ctx, grpcStatus, grpcMessage);
        return 200;
    }

    private static void WriteGrpcStatusTrailer(HttpContext ctx, int status, string? message)
    {
        ctx.Response.AppendTrailer("grpc-status", status.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(message))
            ctx.Response.AppendTrailer("grpc-message", message);
    }

    // gRPC client-streaming replay. The client sends many length-prefixed
    // request frames and gets back a single response (unary on the server
    // side), plus the usual grpc-status trailer.
    //
    // The replayer drains the request stream so the client's WriteAsync
    // / CompleteAsync calls don't deadlock waiting for flow-control
    // windows. Request-side frame content is NOT matched against
    // step.SentMessages — order-based validation for binary protobuf
    // would need schema-aware decoding, which the mock deliberately
    // avoids. The drain finishes once the client half-closes the
    // stream; only then does the mock write the recorded response so
    // the shape matches what a real server would emit.
    private static async Task<int> ReplayGrpcClientStreamAsync(
        HttpContext ctx, BowireRecordingStep step, ILogger logger, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(step.ResponseBinary))
        {
            return await Not501(ctx, logger,
                $"gRPC client-streaming step '{step.Id}' has no 'responseBinary' — the recording predates Phase-1c capture. " +
                $"Re-record against a current Bowire.",
                ct);
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(step.ResponseBinary);
        }
        catch (FormatException ex)
        {
            return await Not501(ctx, logger,
                $"gRPC client-streaming step '{step.Id}' has malformed base64 in 'responseBinary': {ex.Message}",
                ct);
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/grpc";
        await ctx.Response.StartAsync(ct);

        // Drain the client's length-prefixed messages before writing
        // the response. Real gRPC servers typically consume everything
        // first (either to process all inputs or to collect trailers);
        // mirroring that timing avoids surprising clients that rely on
        // stream-half-close ordering.
        var framesReceived = await DrainGrpcRequestStreamAsync(ctx, logger, ct);
        logger.LogInformation(
            "grpc-client-stream(step={StepId}, framesReceived={FramesReceived})",
            step.Id, framesReceived);

        var envelope = new byte[5 + payload.Length];
        envelope[0] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(envelope.AsSpan(1, 4), (uint)payload.Length);
        payload.CopyTo(envelope.AsSpan(5));
        await ctx.Response.Body.WriteAsync(envelope, ct);

        var (grpcStatus, grpcMessage) = MapToGrpcStatus(step.Status);
        WriteGrpcStatusTrailer(ctx, grpcStatus, grpcMessage);
        return 200;
    }

    // gRPC bidirectional-streaming replay. Both directions stream
    // independently. Walks the merged sent/received timeline (same
    // pattern as WebSocket input-gating): emit received frames up to
    // the next send checkpoint, then wait for the client to transmit
    // a matching request frame, then continue. Order-based matching —
    // content of client frames isn't inspected because protobuf
    // decoding would need schema awareness the mock intentionally
    // doesn't have.
    //
    // Recordings without sentMessages degrade to pure server-push
    // (equivalent to server-streaming) so a pre-Phase-2i bidi capture
    // still replays without gating.
    private static async Task<int> ReplayGrpcBidiAsync(
        HttpContext ctx, BowireRecordingStep step, MockOptions options, ILogger logger, CancellationToken ct)
    {
        var received = step.ReceivedMessages;
        if (received is null || received.Count == 0)
        {
            return await Not501(ctx, logger,
                $"gRPC bidi step '{step.Id}' has no receivedMessages — the recording predates Phase-2d capture. " +
                $"Re-record against a current Bowire.",
                ct);
        }

        if (received.Any(f => string.IsNullOrEmpty(f.ResponseBinary)))
        {
            return await Not501(ctx, logger,
                $"gRPC bidi step '{step.Id}' has frames without responseBinary — the recording predates Phase-2d capture. " +
                $"Re-record against a current Bowire.",
                ct);
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/grpc";
        await ctx.Response.StartAsync(ct);

        var speed = options.ReplaySpeed;
        var pace = speed > 0;
        var gating = step.SentMessages is { Count: > 0 };

        // Client-frame ticket channel for gating. Independent of the
        // response-write loop so a client that sends fast doesn't block
        // the response side.
        var clientTickets = System.Threading.Channels.Channel.CreateUnbounded<byte>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var (success, _) = await ReadGrpcFrameAsync(ctx.Request.Body, ct);
                    if (!success) break;
                    clientTickets.Writer.TryWrite(0);
                }
            }
            catch { /* client hung up / cancellation */ }
            finally { clientTickets.Writer.TryComplete(); }
        }, ct);

        if (!gating)
        {
            long lastTimestampMs = 0;
            foreach (var frame in received)
            {
                ct.ThrowIfCancellationRequested();
                if (pace && frame.TimestampMs is long frameTs && frameTs > lastTimestampMs)
                {
                    var waitMs = (long)((frameTs - lastTimestampMs) / speed);
                    if (waitMs > 0)
                    {
                        try { await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct); }
                        catch (OperationCanceledException)
                        {
                            WriteGrpcStatusTrailer(ctx, status: 1, message: "Cancelled");
                            return 200;
                        }
                    }
                    lastTimestampMs = frameTs;
                }
                await WriteGrpcFrameAsync(ctx, frame, logger, ct);
            }
        }
        else
        {
            var timeline = BuildGrpcTimeline(received, step.SentMessages!);
            long lastTimestampMs = 0;
            foreach (var evt in timeline)
            {
                ct.ThrowIfCancellationRequested();

                if (evt.Kind == WebSocketEventKind.Received)
                {
                    if (pace && evt.Frame!.TimestampMs is long frameTs && frameTs > lastTimestampMs)
                    {
                        var waitMs = (long)((frameTs - lastTimestampMs) / speed);
                        if (waitMs > 0)
                        {
                            try { await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct); }
                            catch (OperationCanceledException)
                            {
                                WriteGrpcStatusTrailer(ctx, status: 1, message: "Cancelled");
                                return 200;
                            }
                        }
                        lastTimestampMs = frameTs;
                    }
                    await WriteGrpcFrameAsync(ctx, evt.Frame!, logger, ct);
                }
                else
                {
                    bool ticketAvailable;
                    try { ticketAvailable = await clientTickets.Reader.WaitToReadAsync(ct); }
                    catch (OperationCanceledException) { break; }
                    if (!ticketAvailable) break;
                    clientTickets.Reader.TryRead(out _);
                    if (evt.TimestampMs > lastTimestampMs) lastTimestampMs = evt.TimestampMs;
                    logger.LogInformation(
                        "grpc-bidi-gate(step={StepId}, sentIndex={Index}) → released",
                        step.Id, evt.SentIndex);
                }
            }
        }

        var (grpcStatus, grpcMessage) = MapToGrpcStatus(step.Status);
        WriteGrpcStatusTrailer(ctx, grpcStatus, grpcMessage);
        return 200;
    }

    private static async Task WriteGrpcFrameAsync(
        HttpContext ctx, BowireRecordingFrame frame, ILogger logger, CancellationToken ct)
    {
        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(frame.ResponseBinary!);
        }
        catch (FormatException ex)
        {
            logger.LogWarning("gRPC frame {Index} has malformed base64: {Message}", frame.Index, ex.Message);
            return;
        }

        var envelope = new byte[5 + payload.Length];
        envelope[0] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(envelope.AsSpan(1, 4), (uint)payload.Length);
        payload.CopyTo(envelope.AsSpan(5));
        await ctx.Response.Body.WriteAsync(envelope, ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    // Drain the entire gRPC request stream. Returns the count of
    // length-prefixed frames read. Stops cleanly when the client
    // half-closes (stream ends).
    private static async Task<int> DrainGrpcRequestStreamAsync(
        HttpContext ctx, ILogger logger, CancellationToken ct)
    {
        var count = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (success, _) = await ReadGrpcFrameAsync(ctx.Request.Body, ct);
                if (!success) break;
                count++;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "gRPC request-stream drain ended with an error — treating as end-of-stream.");
        }
        return count;
    }

    // Read one gRPC length-prefixed frame from the request stream.
    // Returns (false, null) once the stream ends normally. Swallows
    // the payload — we don't match it against SentMessages since
    // binary protobuf needs schema-aware comparison the mock doesn't
    // do.
    private static async Task<(bool success, int length)> ReadGrpcFrameAsync(
        Stream stream, CancellationToken ct)
    {
        // Header: 1 byte compression flag + 4 bytes BE length.
        var header = new byte[5];
        var read = await ReadExactAsync(stream, header, 0, 5, ct);
        if (read == 0) return (false, 0);
        if (read < 5) return (false, 0);

        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1, 4));
        if (length < 0 || length > 16 * 1024 * 1024)
        {
            // Defensive: absurd length means wire corruption or a
            // compression flag we don't understand.
            return (false, 0);
        }
        if (length == 0) return (true, 0);

        var payload = new byte[length];
        var payloadRead = await ReadExactAsync(stream, payload, 0, length, ct);
        return payloadRead == length ? (true, length) : (false, 0);
    }

    private static async Task<int> ReadExactAsync(
        Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var total = 0;
        while (total < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset + total, count - total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    // Merge sent + received into a single timeline ordered by
    // timestampMs. Stable: on ties, received frames emit first so a
    // captured burst where recv@t and send@t share the same instant
    // still sends the recv before we start blocking on a gate. Reuses
    // the WebSocketEvent shape because the gating semantics are
    // identical.
    private static List<WebSocketEvent> BuildGrpcTimeline(
        IList<BowireRecordingFrame> received,
        IList<BowireRecordingFrame> sent)
    {
        var events = new List<WebSocketEvent>(received.Count + sent.Count);
        foreach (var r in received)
        {
            events.Add(new WebSocketEvent(
                WebSocketEventKind.Received, r.TimestampMs ?? 0, r, -1));
        }
        for (var i = 0; i < sent.Count; i++)
        {
            events.Add(new WebSocketEvent(
                WebSocketEventKind.Sent, sent[i].TimestampMs ?? 0, null, i));
        }
        events.Sort((a, b) =>
        {
            var cmp = a.TimestampMs.CompareTo(b.TimestampMs);
            if (cmp != 0) return cmp;
            return a.Kind == b.Kind ? 0 : (a.Kind == WebSocketEventKind.Received ? -1 : 1);
        });
        return events;
    }

    // WebSocket replay (Phase 2e).
    //
    // When a client opens a WebSocket handshake against a recorded duplex
    // step, the mock accepts the upgrade and pushes the captured
    // receivedMessages back at their original cadence.
    //
    // Input-gating (Phase 2e+): when the recording also carries
    // sentMessages, those act as synchronization points on the
    // timeline — the replayer emits received frames up to the next
    // sent checkpoint and then blocks until the client has sent a
    // corresponding frame, guaranteeing that a request/response duplex
    // stays in lockstep. Order-based matching: the N-th client frame
    // advances past sentMessages[N]; content isn't inspected (a future
    // slice can add content-matching for strict parity). Recordings
    // without sentMessages fall back to the original fire-and-drain
    // behaviour so pure server-push captures stay unaffected.
    //
    // Frame envelope in the recording:
    //   { "type": "text",   "text": <parsed JSON or string> }
    //   { "type": "binary", "base64": "..." }
    // The envelope is what Bowire's WebSocketBowireChannel wraps incoming
    // frames in; we unwrap it here and send the raw frame with the right
    // WebSocketMessageType.
    private static async Task<int> ReplayWebSocketAsync(
        HttpContext ctx, BowireRecordingStep step, MockOptions options, ILogger logger, CancellationToken ct)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            return await Not501(ctx, logger,
                $"Step '{step.Id}' is a WebSocket duplex recording but the incoming request isn't a WebSocket upgrade. " +
                $"Connect with a WebSocket client (e.g. new WebSocket('ws://host/{step.HttpPath?.TrimStart('/')}')).",
                ct);
        }

        var frames = step.ReceivedMessages;
        // Honour the recorded sub-protocol (captured by
        // WebSocketBowireChannel + the /api/channel/open endpoint).
        // When the incoming upgrade also lists it as a requested
        // sub-protocol the accept call echoes it back so the client's
        // ClientWebSocket.SubProtocol property matches; otherwise we
        // don't force a negotiation mismatch.
        var negotiatedSubProtocol = ResolveRecordedSubProtocol(step, ctx);

        if (frames is null || frames.Count == 0)
        {
            // Nothing to replay — accept and close gracefully so clients
            // don't hang.
            using var empty = negotiatedSubProtocol is null
                ? await ctx.WebSockets.AcceptWebSocketAsync()
                : await ctx.WebSockets.AcceptWebSocketAsync(negotiatedSubProtocol);
            try
            {
                await empty.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "no recorded frames", ct);
            }
            catch (System.Net.WebSockets.WebSocketException) { /* client hung up without close handshake */ }
            catch (OperationCanceledException) { /* request aborted */ }
            return 101;
        }

        using var socket = negotiatedSubProtocol is null
            ? await ctx.WebSockets.AcceptWebSocketAsync()
            : await ctx.WebSockets.AcceptWebSocketAsync(negotiatedSubProtocol);

        var inputGating = step.SentMessages is { Count: > 0 };

        // Client-frame notifications. The receive loop writes one
        // ticket per completed inbound message; the replay loop reads
        // one per send-gate. Unbounded so early client bursts before
        // the first send-gate don't block the receive loop.
        var clientTickets = System.Threading.Channels.Channel.CreateUnbounded<byte>();

        // Receive loop: drains (no gating) or signals each completed
        // inbound message (gating). Both modes discard the payload —
        // order-based matching needs no content inspection.
        _ = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            try
            {
                while (socket.State == System.Net.WebSockets.WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                    // Only count complete messages as a gate-release —
                    // fragmented frames span multiple ReceiveAsync calls
                    // until EndOfMessage flips to true.
                    if (result.EndOfMessage) clientTickets.Writer.TryWrite(0);
                }
            }
            catch { /* client hung up, cancellation, etc. */ }
            finally { clientTickets.Writer.TryComplete(); }
        }, ct);

        var speed = options.ReplaySpeed;
        var pace = speed > 0;

        if (!inputGating)
        {
            long lastTimestampMs = 0;
            foreach (var frame in frames)
            {
                ct.ThrowIfCancellationRequested();
                if (socket.State != System.Net.WebSockets.WebSocketState.Open) break;

                if (pace && frame.TimestampMs is long frameTs && frameTs > lastTimestampMs)
                {
                    var waitMs = (long)((frameTs - lastTimestampMs) / speed);
                    if (waitMs > 0)
                    {
                        try { await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct); }
                        catch (OperationCanceledException) { break; }
                    }
                    lastTimestampMs = frameTs;
                }

                if (!TrySendFrame(socket, frame, logger, ct, out var sendTask))
                {
                    continue;
                }
                await sendTask;
            }
        }
        else
        {
            // Merge sent / received frames into a single timeline and
            // walk it. Stable sort: on ts ties, recv emits first so a
            // frame captured at the same instant as a send reaches the
            // client before we start waiting for the next client ticket.
            var timeline = BuildWebSocketTimeline(frames, step.SentMessages!);
            long lastTimestampMs = 0;

            foreach (var evt in timeline)
            {
                ct.ThrowIfCancellationRequested();
                if (socket.State != System.Net.WebSockets.WebSocketState.Open) break;

                if (evt.Kind == WebSocketEventKind.Received)
                {
                    if (pace && evt.Frame!.TimestampMs is long frameTs && frameTs > lastTimestampMs)
                    {
                        var waitMs = (long)((frameTs - lastTimestampMs) / speed);
                        if (waitMs > 0)
                        {
                            try { await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct); }
                            catch (OperationCanceledException) { break; }
                        }
                        lastTimestampMs = frameTs;
                    }

                    if (TrySendFrame(socket, evt.Frame!, logger, ct, out var sendTask))
                    {
                        await sendTask;
                    }
                }
                else
                {
                    // Send-gate: wait for the client's next frame.
                    // WaitToReadAsync returns false once the channel is
                    // completed (receive loop exited) — abort replay
                    // rather than hang forever.
                    bool ticketAvailable;
                    try { ticketAvailable = await clientTickets.Reader.WaitToReadAsync(ct); }
                    catch (OperationCanceledException) { break; }

                    if (!ticketAvailable) break;
                    clientTickets.Reader.TryRead(out _);

                    // Advance virtual time to the send checkpoint so
                    // the next recv's pacing gap is measured from the
                    // gate-unlock moment — not from the replay start,
                    // which would bunch frames up after a slow client.
                    if (evt.TimestampMs > lastTimestampMs) lastTimestampMs = evt.TimestampMs;
                    logger.LogInformation(
                        "ws-input-gate(step={StepId}, sentIndex={Index}) → released",
                        step.Id, evt.SentIndex);
                }
            }
        }

        if (socket.State is System.Net.WebSockets.WebSocketState.Open
                         or System.Net.WebSockets.WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(
                    System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "replay complete", ct);
            }
            catch (System.Net.WebSockets.WebSocketException) { /* client hung up without close handshake */ }
            catch (OperationCanceledException) { /* request aborted */ }
        }

        // HTTP upgrade success code. Not written on the response directly
        // (the upgrade response is already sent by Kestrel); this is the
        // logical status we want the middleware to log.
        return 101;
    }

    private enum WebSocketEventKind { Received, Sent }

    private readonly record struct WebSocketEvent(
        WebSocketEventKind Kind,
        long TimestampMs,
        BowireRecordingFrame? Frame,
        int SentIndex);

    // Merge sent + received frame lists into a single wall-clock-ordered
    // walk. Stable: on timestamp ties, received frames come first so a
    // captured burst where `recv@t` and `send@t` share the exact same
    // instant still emits the recv before we start blocking on the send
    // gate. Null timestamps are treated as 0.
    private static List<WebSocketEvent> BuildWebSocketTimeline(
        IList<BowireRecordingFrame> received,
        IList<BowireRecordingFrame> sent)
    {
        var events = new List<WebSocketEvent>(received.Count + sent.Count);
        foreach (var r in received)
        {
            events.Add(new WebSocketEvent(
                WebSocketEventKind.Received, r.TimestampMs ?? 0, r, -1));
        }
        for (var i = 0; i < sent.Count; i++)
        {
            events.Add(new WebSocketEvent(
                WebSocketEventKind.Sent, sent[i].TimestampMs ?? 0, null, i));
        }

        events.Sort((a, b) =>
        {
            var cmp = a.TimestampMs.CompareTo(b.TimestampMs);
            if (cmp != 0) return cmp;
            // recv before sent on ties
            return a.Kind == b.Kind ? 0 : (a.Kind == WebSocketEventKind.Received ? -1 : 1);
        });
        return events;
    }

    // Pick the sub-protocol to echo on the 101 upgrade response. We only
    // echo a recorded value when the client's upgrade request actually
    // lists it — forcing an un-requested sub-protocol onto a vanilla
    // client would cause the handshake to fail with a negotiation
    // mismatch. Metadata is looked up under the reserved key
    // "_subprotocol" (underscore prefix reserves it from user-space
    // header collisions, mirroring the `_trailer:` convention for gRPC).
    internal static string? ResolveRecordedSubProtocol(
        BowireRecordingStep step, HttpContext ctx)
    {
        if (step.Metadata is null) return null;
        if (!step.Metadata.TryGetValue("_subprotocol", out var recorded) ||
            string.IsNullOrEmpty(recorded))
        {
            return null;
        }

        var requested = ctx.WebSockets.WebSocketRequestedProtocols;
        if (requested is null || requested.Count == 0)
        {
            return null;
        }

        return requested.Any(p => string.Equals(p, recorded, StringComparison.Ordinal))
            ? recorded
            : null;
    }

    private static bool TrySendFrame(
        System.Net.WebSockets.WebSocket socket,
        BowireRecordingFrame frame,
        ILogger logger,
        CancellationToken ct,
        out Task sendTask)
    {
        sendTask = Task.CompletedTask;

        if (frame.Data is null)
        {
            logger.LogWarning("WebSocket frame {Index} has null data; skipping.", frame.Index);
            return false;
        }

        try
        {
            var raw = System.Text.Json.JsonSerializer.Serialize(frame.Data);
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;

            string? type = null;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                root.TryGetProperty("type", out var typeEl) &&
                typeEl.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                type = typeEl.GetString();
            }

            if (string.Equals(type, "binary", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("base64", out var b64El) &&
                b64El.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var bytes = Convert.FromBase64String(b64El.GetString()!);
                sendTask = socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    System.Net.WebSockets.WebSocketMessageType.Binary,
                    endOfMessage: true,
                    cancellationToken: ct);
                return true;
            }

            // Default: text frame. For "text"-typed envelopes we pull the
            // inner text value (which may itself be a JSON object — preserve
            // it as the compact JSON string it represents). Anything else
            // gets sent as its raw JSON form.
            string payload;
            if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("text", out var textEl))
            {
                payload = textEl.ValueKind == System.Text.Json.JsonValueKind.String
                    ? textEl.GetString() ?? string.Empty
                    : textEl.GetRawText();
            }
            else
            {
                payload = raw;
            }

            payload = ResponseBodySubstitutor.Substitute(payload);
            var bytesText = Encoding.UTF8.GetBytes(payload);
            sendTask = socket.SendAsync(
                new ArraySegment<byte>(bytesText),
                System.Net.WebSockets.WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken: ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send WebSocket frame {Index}; skipping.", frame.Index);
            return false;
        }
    }

    // GraphQL subscription replay (Phase 2h).
    //
    // `graphql-transport-ws` (https://github.com/enisdenjo/graphql-ws) is a
    // pub-sub protocol on top of WebSocket with a tiny handshake + envelope:
    //
    //   client → {"type":"connection_init"}
    //   server → {"type":"connection_ack"}
    //   client → {"type":"subscribe","id":"<client-id>","payload":{...}}
    //   server → {"type":"next","id":"<client-id>","payload":{"data":{...}}}
    //   ...
    //   server → {"type":"complete","id":"<client-id>"}
    //
    // Recorded frames in a Bowire capture carry the *server's* original id
    // (assigned by whichever client was recording at the time). That id is
    // opaque to the real server so the real subscription works regardless,
    // but a fresh replay will almost always see a different client-chosen id
    // on this run. We can't blindly relay the recorded frames — the client
    // would ignore any `next`/`complete` whose id doesn't match its own. The
    // fix is cheap: rewrite the `id` field on each outgoing frame to whatever
    // the current client picked during `subscribe`. Everything else (the
    // `payload` shape, the `next`/`complete`/`error` cadence) is preserved
    // verbatim from the recording.
    private static async Task<int> ReplayGraphQlSubscriptionAsync(
        HttpContext ctx, BowireRecordingStep step, MockOptions options, ILogger logger, CancellationToken ct)
    {
        const string subprotocol = "graphql-transport-ws";

        // Accept the upgrade with the graphql-transport-ws subprotocol so the
        // client's ClientWebSocket.SubProtocol negotiation succeeds. Clients
        // that don't request this subprotocol still work — we just echo back
        // whatever they asked for if it matches.
        var requested = ctx.WebSockets.WebSocketRequestedProtocols;
        var selected = requested.FirstOrDefault(p =>
                            string.Equals(p, subprotocol, StringComparison.OrdinalIgnoreCase))
                       ?? subprotocol;

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync(selected);

        var frames = step.ReceivedMessages;
        if (frames is null || frames.Count == 0)
        {
            await socket.CloseAsync(
                System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "no recorded frames", ct);
            return 101;
        }

        // Read the client's connection_init. 4 KiB is plenty for the tiny
        // envelope messages this protocol uses; in the unlikely case a
        // connection_init payload exceeds that, we surface the problem
        // rather than papering over it.
        var (ackOk, _) = await ReadJsonEnvelopeAsync(socket, ct);
        if (!ackOk)
        {
            await socket.CloseAsync(
                System.Net.WebSockets.WebSocketCloseStatus.ProtocolError,
                "expected connection_init", ct);
            return 101;
        }

        await SendJsonAsync(socket, "{\"type\":\"connection_ack\"}", ct);

        // Read the first subscribe frame and capture the client's id. A
        // well-behaved client sends one subscribe per logical subscription;
        // multiplexing multiple subscriptions over one socket isn't part of
        // Phase 2h — we replay a single recorded subscription against the
        // first id we see.
        var (subOk, subId) = await ReadJsonEnvelopeAsync(socket, ct);
        if (!subOk || subId is null)
        {
            await socket.CloseAsync(
                System.Net.WebSockets.WebSocketCloseStatus.ProtocolError,
                "expected subscribe with id", ct);
            return 101;
        }

        // Drain any further client frames in the background so the socket's
        // receive buffer doesn't stall the send loop. The protocol allows
        // ping/pong and complete-from-client; Phase 2h treats all of them
        // as no-ops except close.
        _ = Task.Run(async () =>
        {
            var buf = new byte[4096];
            try
            {
                while (socket.State == System.Net.WebSockets.WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var r = await socket.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (r.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                }
            }
            catch { /* client hung up or cancelled */ }
        }, ct);

        var speed = options.ReplaySpeed;
        var pace = speed > 0;

        long lastTimestampMs = 0;
        var sawComplete = false;

        foreach (var frame in frames)
        {
            ct.ThrowIfCancellationRequested();
            if (socket.State != System.Net.WebSockets.WebSocketState.Open) break;

            if (pace && frame.TimestampMs is long frameTs && frameTs > lastTimestampMs)
            {
                var waitMs = (long)((frameTs - lastTimestampMs) / speed);
                if (waitMs > 0)
                {
                    try { await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct); }
                    catch (OperationCanceledException) { break; }
                }
                lastTimestampMs = frameTs;
            }

            var rewritten = RewriteGraphQlFrame(frame, subId, logger);
            if (rewritten is null) continue;

            if (rewritten.Value.IsComplete) sawComplete = true;

            var payload = ResponseBodySubstitutor.Substitute(rewritten.Value.Payload);
            var bytes = Encoding.UTF8.GetBytes(payload);
            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                System.Net.WebSockets.WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken: ct);
        }

        // Every graphql-transport-ws subscription ends with a `complete` on
        // the server side. If the recording didn't capture one (unusual but
        // possible for abruptly-terminated captures) we synthesise one so
        // clients don't hang waiting for it.
        if (!sawComplete && socket.State == System.Net.WebSockets.WebSocketState.Open)
        {
            var completeFrame = $"{{\"type\":\"complete\",\"id\":{System.Text.Json.JsonSerializer.Serialize(subId)}}}";
            await SendJsonAsync(socket, completeFrame, ct);
        }

        if (socket.State is System.Net.WebSockets.WebSocketState.Open
                         or System.Net.WebSockets.WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(
                    System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "replay complete", ct);
            }
            catch (System.Net.WebSockets.WebSocketException) { /* client hung up without close handshake */ }
            catch (OperationCanceledException) { /* request aborted */ }
        }

        return 101;
    }

    // Read one text frame from the socket and try to extract the graphql-ws
    // envelope's `id` field. Returns (false, null) on anything that looks
    // wrong so callers can surface a protocol error to the client.
    private static async Task<(bool Ok, string? Id)> ReadJsonEnvelopeAsync(
        System.Net.WebSockets.WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        System.Net.WebSockets.WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) return (false, null);
            await ms.WriteAsync(buffer.AsMemory(0, result.Count), ct);
        } while (!result.EndOfMessage);

        var text = Encoding.UTF8.GetString(ms.ToArray());
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return (false, null);
            string? id = null;
            if (doc.RootElement.TryGetProperty("id", out var idEl) &&
                idEl.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                id = idEl.GetString();
            }
            return (true, id);
        }
        catch (System.Text.Json.JsonException)
        {
            return (false, null);
        }
    }

    private static async Task SendJsonAsync(
        System.Net.WebSockets.WebSocket socket, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            System.Net.WebSockets.WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken: ct);
    }

    // Unwrap the recorded frame and return the outgoing graphql-transport-ws
    // JSON with its `id` rewritten to the current subscription id. Returns
    // null for frames we can't make sense of (logged as a warning so they
    // show up at replay time).
    //
    // The recorder's envelope is either:
    //   { "type": "text", "text": "<stringified json>" }            — inner is a string
    //   { "type": "text", "text": { "type": "next", "id": ..., ... } } — inner is an object
    // or (older / non-channel captures):
    //   { "type": "next", "id": ..., ... }                          — raw protocol frame
    private static (string Payload, bool IsComplete)? RewriteGraphQlFrame(
        BowireRecordingFrame frame, string subId, ILogger logger)
    {
        if (frame.Data is null)
        {
            logger.LogWarning("graphql subscription frame {Index} has null data; skipping.", frame.Index);
            return null;
        }

        try
        {
            var raw = System.Text.Json.JsonSerializer.Serialize(frame.Data);
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // Peel the channel envelope if present.
            System.Text.Json.JsonElement protocolFrame = root;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                root.TryGetProperty("type", out var envTypeEl) &&
                envTypeEl.ValueKind == System.Text.Json.JsonValueKind.String &&
                string.Equals(envTypeEl.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("text", out var textEl))
            {
                if (textEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var inner = textEl.GetString() ?? "";
                    try
                    {
                        using var innerDoc = System.Text.Json.JsonDocument.Parse(inner);
                        return RewriteProtocolFrame(innerDoc.RootElement, subId);
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // Inner wasn't JSON — pass the string through verbatim
                        // so recordings with non-JSON text frames still replay.
                        return (inner, false);
                    }
                }
                protocolFrame = textEl;
            }

            return RewriteProtocolFrame(protocolFrame, subId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to rewrite graphql subscription frame {Index}; skipping.", frame.Index);
            return null;
        }
    }

    private static (string Payload, bool IsComplete)? RewriteProtocolFrame(
        System.Text.Json.JsonElement protocolFrame, string subId)
    {
        if (protocolFrame.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

        string? type = null;
        if (protocolFrame.TryGetProperty("type", out var typeEl) &&
            typeEl.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            type = typeEl.GetString();
        }

        // Rebuild the frame with id rewritten. We copy every property
        // verbatim except `id`, which is forced to the current client's
        // subscription id. Writing via Utf8JsonWriter keeps the order
        // stable (type, id, payload) and the output compact.
        using var buffer = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            var wroteId = false;
            foreach (var prop in protocolFrame.EnumerateObject())
            {
                if (string.Equals(prop.Name, "id", StringComparison.Ordinal))
                {
                    writer.WriteString("id", subId);
                    wroteId = true;
                    continue;
                }
                prop.WriteTo(writer);
            }
            // `next` / `complete` / `error` all require an id; add one if the
            // recorded frame was missing it (e.g. for future protocol frames
            // like `ping` that don't need id we still write id — harmless,
            // well-behaved clients ignore unknown fields).
            if (!wroteId && NeedsId(type))
            {
                writer.WriteString("id", subId);
            }
            writer.WriteEndObject();
        }

        var payload = Encoding.UTF8.GetString(buffer.ToArray());
        var isComplete = string.Equals(type, "complete", StringComparison.OrdinalIgnoreCase);
        return (payload, isComplete);
    }

    private static bool NeedsId(string? type) =>
        string.Equals(type, "next", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "complete", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "error", StringComparison.OrdinalIgnoreCase);

    // SignalR hub replay (Phase 2h+).
    //
    // Protocol at a glance:
    //   - WebSocket upgrade with or without a sub-protocol; the client
    //     sends the negotiated JSON hub protocol as its first frame
    //     before any invocations.
    //   - Frame separator: U+001E (record separator). Each JSON message
    //     is followed by \x1e; multiple messages may be concatenated in
    //     a single WS frame.
    //   - Handshake: client → {"protocol":"json","version":1}\x1e,
    //     server → {}\x1e
    //   - Message types (0-indexed into the JSON .type field):
    //       1 Invocation             {type:1, invocationId?, target, arguments}
    //       2 StreamItem             {type:2, invocationId, item}
    //       3 Completion             {type:3, invocationId, result?, error?}
    //       4 StreamInvocation       like 1 but for streaming targets
    //       5 CancelInvocation       {type:5, invocationId}
    //       6 Ping                   {type:6}
    //       7 Close                  {type:7, error?}
    //
    // Mock semantics:
    //   - step.ReceivedMessages carries the server-side frames captured
    //     by the recorder. Server-initiated broadcasts (Invocation/
    //     StreamItem/Completion with server-chosen ids) emit as-is,
    //     paced by frame.TimestampMs.
    //   - Client-initiated invocations arrive with a client-chosen
    //     invocationId. The mock pairs each incoming Invocation with
    //     the first recorded Completion (or StreamItem+Completion)
    //     whose invocationId appears in step.SentMessages as paired
    //     with the same target method, rewrites the id to match, and
    //     emits. If no pairing is found, emit a synthesised Completion
    //     with error="No recorded response for target 'X'." so the
    //     client doesn't hang.
    //   - Pings get pings. CancelInvocation is logged and ignored.
    //   - Close from the client → close cleanly. Close from the
    //     server side (type=7 in ReceivedMessages) triggers a socket
    //     close with NormalClosure.
    private static async Task<int> ReplaySignalRAsync(
        HttpContext ctx, BowireRecordingStep step, MockOptions options, ILogger logger, CancellationToken ct)
    {
        // Pick up whatever subprotocol the client requested (if any);
        // SignalR doesn't mandate one for JSON transport.
        var requested = ctx.WebSockets.WebSocketRequestedProtocols;
        var selected = requested.Count > 0 ? requested[0] : null;
        using var socket = selected is not null
            ? await ctx.WebSockets.AcceptWebSocketAsync(selected)
            : await ctx.WebSockets.AcceptWebSocketAsync();

        // ---- Handshake ----
        var handshake = await ReadSignalRFramesAsync(socket, ct);
        if (handshake.Count == 0)
        {
            await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.ProtocolError,
                "expected handshake", ct);
            return 101;
        }
        try
        {
            using var handshakeDoc = System.Text.Json.JsonDocument.Parse(handshake[0]);
            if (!handshakeDoc.RootElement.TryGetProperty("protocol", out var protoEl) ||
                protoEl.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.ProtocolError,
                    "handshake missing 'protocol'", ct);
                return 101;
            }
            var proto = protoEl.GetString();
            if (!string.Equals(proto, "json", StringComparison.OrdinalIgnoreCase))
            {
                // Reply with a handshake error per the SignalR spec so
                // the client's error callback fires cleanly.
                await SendSignalRFrameAsync(socket,
                    "{\"error\":\"Only the 'json' hub protocol is supported by this mock.\"}", ct);
                await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.ProtocolError,
                    "protocol not supported", ct);
                return 101;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.ProtocolError,
                "malformed handshake", ct);
            return 101;
        }

        // Handshake ack: empty object.
        await SendSignalRFrameAsync(socket, "{}", ct);

        // ---- State for client-initiated invocation pairing ----
        // Map from recorded invocation target → list of captured
        // server-response frames for it, in capture order. Populated
        // from step.SentMessages pairs + step.ReceivedMessages.
        var pairings = BuildInvocationPairings(step);

        // ---- Concurrent receive loop ----
        //
        // Clients ping, invoke methods, and close at arbitrary times.
        // Read them on a background task so the server-push loop below
        // can run uninterrupted; coordinate via a cancellation token
        // the receive loop owns and the emit loop can observe.
        //
        // try/finally around the whole CTS lifetime so the analyzer
        // sees the Dispose on every exit path and the task always has
        // a live token while it's running.
        var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
        var pendingClientTasks = new List<Task>();
        var clientTaskLock = new object();

        // CA2025: we DO await receiveTask before the outer finally
        // disposes sessionCts, so the token is live for the task's
        // whole lifetime. Analyzer can't trace the await-then-dispose
        // sequence across the try/finally boundary.
#pragma warning disable CA2025
        var receiveTask = Task.Run(async () =>
        {
            try
            {
                while (socket.State == System.Net.WebSockets.WebSocketState.Open &&
                       !sessionCts.Token.IsCancellationRequested)
                {
                    var frames = await ReadSignalRFramesAsync(socket, sessionCts.Token);
                    if (frames.Count == 0 && socket.State != System.Net.WebSockets.WebSocketState.Open) break;

                    foreach (var raw in frames)
                    {
                        var respond = HandleClientFrameAsync(socket, raw, pairings, logger, sessionCts.Token);
                        lock (clientTaskLock) pendingClientTasks.Add(respond);
                    }
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch { /* socket errors — the emit loop will notice */ }
        }, sessionCts.Token);
#pragma warning restore CA2025

        // ---- Server-push loop: recorded ReceivedMessages ----
        var frames2 = step.ReceivedMessages;
        if (frames2 is { Count: > 0 })
        {
            var speed = options.ReplaySpeed;
            var pace = speed > 0;
            long lastTimestampMs = 0;

            foreach (var frame in frames2)
            {
                ct.ThrowIfCancellationRequested();
                if (socket.State != System.Net.WebSockets.WebSocketState.Open) break;

                if (pace && frame.TimestampMs is long frameTs && frameTs > lastTimestampMs)
                {
                    var waitMs = (long)((frameTs - lastTimestampMs) / speed);
                    if (waitMs > 0)
                    {
                        try { await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct); }
                        catch (OperationCanceledException) { break; }
                    }
                    lastTimestampMs = frameTs;
                }

                // Serialise the recorded frame to its SignalR JSON form.
                // Channel-envelope frames ({type:"text", text:...}) are
                // unwrapped so the inner hub message gets the wire treatment.
                var signalRJson = SerialiseSignalRFrame(frame.Data, logger);
                if (signalRJson is null) continue;

                // Apply ${uuid}/${now}/... dynamic substitution for
                // symmetry with every other streaming replayer.
                signalRJson = ResponseBodySubstitutor.Substitute(signalRJson);

                await SendSignalRFrameAsync(socket, signalRJson, ct);
            }
        }

        // Server-push loop is done, but the conversation isn't —
        // clients may still invoke hub methods, send pings, or close
        // on their own schedule. Keep the socket open until:
        //   - The client closes (receiveTask exits), OR
        //   - The outer request is aborted (ct), OR
        //   - An idle timeout elapses with no ongoing work.
        // For broadcast-only recordings the client closes promptly
        // after reading the expected frames; for request-response
        // recordings the client sends its Invocations, gets replies
        // through the pairings table, and then closes. Either flow
        // terminates receiveTask cleanly.
        try { await receiveTask.WaitAsync(TimeSpan.FromSeconds(30), ct); }
        catch (TimeoutException) { /* idle — cancel below */ }
        catch (OperationCanceledException) { /* shutdown */ }

        // Drain any in-flight invocation responses before we close so
        // the paired Completion reaches the client first.
        Task[] snapshot;
        lock (clientTaskLock) snapshot = pendingClientTasks.ToArray();
        if (snapshot.Length > 0)
        {
            try { await Task.WhenAll(snapshot).WaitAsync(TimeSpan.FromSeconds(2), ct); }
            catch (TimeoutException) { /* best-effort */ }
            catch (OperationCanceledException) { /* shutdown */ }
        }

        await sessionCts.CancelAsync();

        // Complete the close handshake whether we initiated it
        // (Open → send Close) or the client did (CloseReceived →
        // acknowledge). WebSocketState.Open covers the first case,
        // CloseReceived the second; both need our Close frame out.
        if (socket.State is System.Net.WebSockets.WebSocketState.Open
                         or System.Net.WebSockets.WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(
                    System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "replay complete", ct);
            }
            catch (System.Net.WebSockets.WebSocketException) { /* already gone */ }
        }

        // Make sure the receive task has actually observed the CTS
        // cancellation before the outer finally disposes it.
        try { await receiveTask; }
        catch (OperationCanceledException) { /* expected */ }
        catch { /* swallow */ }

        return 101;
        }
        finally
        {
            sessionCts.Dispose();
        }
    }

    // Pair recorded SentMessages invocations with the ReceivedMessages
    // frames that carry their invocationId. Returns a map from target
    // method name → the ordered list of server-frame JSON payloads the
    // recording captured as the response, with the invocationId already
    // stripped — the caller rewrites it to the client's chosen id
    // before emitting.
    private static Dictionary<string, List<System.Text.Json.JsonElement>> BuildInvocationPairings(BowireRecordingStep step)
    {
        var result = new Dictionary<string, List<System.Text.Json.JsonElement>>(StringComparer.Ordinal);
        if (step.SentMessages is null || step.ReceivedMessages is null) return result;

        // Index received frames by invocationId for O(1) lookup.
        var receivedById = new Dictionary<string, List<System.Text.Json.JsonElement>>(StringComparer.Ordinal);
        foreach (var rec in step.ReceivedMessages)
        {
            if (rec.Data is null) continue;
            try
            {
                var raw = System.Text.Json.JsonSerializer.Serialize(rec.Data);
                var doc = System.Text.Json.JsonDocument.Parse(raw);
                var el = doc.RootElement;
                // Unwrap channel envelope {type:"text", text:<inner>}
                if (el.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    el.TryGetProperty("type", out var envT) &&
                    envT.ValueKind == System.Text.Json.JsonValueKind.String &&
                    string.Equals(envT.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                    el.TryGetProperty("text", out var textEl))
                {
                    if (textEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        try { el = System.Text.Json.JsonDocument.Parse(textEl.GetString() ?? "").RootElement.Clone(); }
                        catch { continue; }
                    }
                    else { el = textEl.Clone(); }
                }
                if (el.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!el.TryGetProperty("invocationId", out var idEl) ||
                    idEl.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var id = idEl.GetString();
                if (string.IsNullOrEmpty(id)) continue;
                if (!receivedById.TryGetValue(id, out var list))
                {
                    list = [];
                    receivedById[id] = list;
                }
                list.Add(el.Clone());
            }
            catch { continue; }
        }

        // Now walk sent invocations, pair each by its invocationId+target.
        foreach (var sent in step.SentMessages)
        {
            if (sent.Data is null) continue;
            try
            {
                var raw = System.Text.Json.JsonSerializer.Serialize(sent.Data);
                var doc = System.Text.Json.JsonDocument.Parse(raw);
                var el = doc.RootElement;
                if (el.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    el.TryGetProperty("type", out var envT) &&
                    envT.ValueKind == System.Text.Json.JsonValueKind.String &&
                    string.Equals(envT.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                    el.TryGetProperty("text", out var textEl))
                {
                    if (textEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        try { el = System.Text.Json.JsonDocument.Parse(textEl.GetString() ?? "").RootElement.Clone(); }
                        catch { continue; }
                    }
                    else { el = textEl.Clone(); }
                }
                if (el.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!el.TryGetProperty("target", out var tgtEl) ||
                    tgtEl.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                if (!el.TryGetProperty("invocationId", out var idEl) ||
                    idEl.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var target = tgtEl.GetString()!;
                var id = idEl.GetString();
                if (string.IsNullOrEmpty(id)) continue;
                if (!receivedById.TryGetValue(id, out var paired)) continue;
                if (!result.TryGetValue(target, out var list))
                {
                    list = [];
                    result[target] = list;
                }
                list.AddRange(paired);
            }
            catch { continue; }
        }

        return result;
    }

    private static async Task HandleClientFrameAsync(
        System.Net.WebSockets.WebSocket socket,
        string frameJson,
        Dictionary<string, List<System.Text.Json.JsonElement>> pairings,
        ILogger logger,
        CancellationToken ct)
    {
        System.Text.Json.JsonDocument doc;
        try { doc = System.Text.Json.JsonDocument.Parse(frameJson); }
        catch (System.Text.Json.JsonException)
        {
            logger.LogDebug("signalr: ignoring non-JSON client frame ({Len} bytes)", frameJson.Length);
            return;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return;
            if (!doc.RootElement.TryGetProperty("type", out var typeEl) ||
                typeEl.ValueKind != System.Text.Json.JsonValueKind.Number) return;
            var type = typeEl.GetInt32();

            switch (type)
            {
                case 6: // Ping → pong (same frame).
                    await SendSignalRFrameAsync(socket, "{\"type\":6}", ct);
                    return;

                case 7: // Close from client — ignore; the outer receive
                        // loop will notice the WS close and tear down.
                    return;

                case 5: // CancelInvocation — log + ignore for mock replay.
                    logger.LogDebug("signalr: client cancelled invocation, mock ignores");
                    return;

                case 1: // Invocation
                case 4: // StreamInvocation (treat identically for replay)
                    await HandleClientInvocationAsync(socket, doc.RootElement, pairings, logger, ct);
                    return;

                default:
                    // Unknown type — emit nothing.
                    return;
            }
        }
    }

    private static async Task HandleClientInvocationAsync(
        System.Net.WebSockets.WebSocket socket,
        System.Text.Json.JsonElement invocation,
        Dictionary<string, List<System.Text.Json.JsonElement>> pairings,
        ILogger logger,
        CancellationToken ct)
    {
        if (!invocation.TryGetProperty("target", out var tgtEl) ||
            tgtEl.ValueKind != System.Text.Json.JsonValueKind.String) return;
        var target = tgtEl.GetString()!;

        // invocationId is optional for fire-and-forget invocations.
        string? clientId = null;
        if (invocation.TryGetProperty("invocationId", out var idEl) &&
            idEl.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            clientId = idEl.GetString();
        }

        // No pairing recorded → if the client expects a completion,
        // synthesise an error so it doesn't hang. Fire-and-forget
        // invocations (no invocationId) get no response at all.
        if (!pairings.TryGetValue(target, out var responses) || responses.Count == 0)
        {
            if (!string.IsNullOrEmpty(clientId))
            {
                var synth = "{\"type\":3,\"invocationId\":" +
                    System.Text.Json.JsonSerializer.Serialize(clientId) +
                    ",\"error\":\"No recorded response for target '" +
                    EscapeJson(target) + "'.\"}";
                await SendSignalRFrameAsync(socket, synth, ct);
            }
            return;
        }

        // Consume the first recorded response set for this target (this
        // matches "call it once, get the recorded pair"). Remaining
        // calls re-use the last pair if the list is not repeating.
        var responseSet = responses[0];
        if (responses.Count > 1) responses.RemoveAt(0);

        // responseSet is a list of frames — iterate and emit each with
        // invocationId rewritten. For type=1/4 Invocation (no such
        // thing as a server-returned invocation for this target), skip.
        foreach (var frame in EnumerateFrames(responseSet))
        {
            if (frame.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            var payload = RewriteInvocationId(frame, clientId);
            await SendSignalRFrameAsync(socket, payload, ct);
        }
    }

    private static IEnumerable<System.Text.Json.JsonElement> EnumerateFrames(System.Text.Json.JsonElement single)
    {
        yield return single;
    }

    private static string RewriteInvocationId(System.Text.Json.JsonElement frame, string? newId)
    {
        using var ms = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var prop in frame.EnumerateObject())
            {
                if (string.Equals(prop.Name, "invocationId", StringComparison.Ordinal))
                {
                    if (!string.IsNullOrEmpty(newId)) writer.WriteString("invocationId", newId);
                    // else drop the property — fire-and-forget semantics
                    continue;
                }
                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // Serialise a recorded ReceivedMessages frame into SignalR JSON.
    // Handles the channel envelope ({type:"text", text:...}) so a
    // recorder that wrapped frames via WebSocketBowireChannel still
    // replays cleanly.
    private static string? SerialiseSignalRFrame(object? data, ILogger logger)
    {
        if (data is null)
        {
            logger.LogDebug("signalr: skipping null frame");
            return null;
        }
        try
        {
            var raw = System.Text.Json.JsonSerializer.Serialize(data);
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                root.TryGetProperty("type", out var envT) &&
                envT.ValueKind == System.Text.Json.JsonValueKind.String &&
                string.Equals(envT.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("text", out var textEl))
            {
                if (textEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    return textEl.GetString() ?? "";
                return textEl.GetRawText();
            }
            return raw;
        }
        catch (System.Text.Json.JsonException ex)
        {
            logger.LogDebug(ex, "signalr: frame serialisation failed");
            return null;
        }
    }

    // Read one batch of SignalR frames from the socket. A single WS
    // message may carry multiple frames separated by U+001E; we split
    // on that byte and return each JSON body separately.
    private static async Task<List<string>> ReadSignalRFramesAsync(
        System.Net.WebSockets.WebSocket socket, CancellationToken ct)
    {
        const byte separator = 0x1E;
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        System.Net.WebSockets.WebSocketReceiveResult result;
        try
        {
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    return [];
                await ms.WriteAsync(buffer.AsMemory(0, result.Count), ct);
            } while (!result.EndOfMessage);
        }
        catch (OperationCanceledException) { return []; }
        catch (System.Net.WebSockets.WebSocketException) { return []; }

        var bytes = ms.ToArray();
        var frames = new List<string>();
        var start = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == separator)
            {
                if (i > start)
                    frames.Add(Encoding.UTF8.GetString(bytes, start, i - start));
                start = i + 1;
            }
        }
        if (start < bytes.Length)
        {
            // Trailing bytes without a separator — non-conforming but
            // some clients send unterminated JSON on Close; accept it.
            frames.Add(Encoding.UTF8.GetString(bytes, start, bytes.Length - start));
        }
        return frames;
    }

    private static async Task SendSignalRFrameAsync(
        System.Net.WebSockets.WebSocket socket, string json, CancellationToken ct)
    {
        // U+001E terminator appended to every frame. SignalR clients
        // won't deliver the message to the handler until they see it.
        var bytes = Encoding.UTF8.GetBytes(json + "");
        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            System.Net.WebSockets.WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken: ct);
    }

    /// <summary>
    /// Parse Bowire's recorded status field into an HTTP status code. The
    /// recording stores either an <see cref="HttpStatusCode"/> name
    /// (<c>"OK"</c>, <c>"NotFound"</c>), the numeric string (<c>"204"</c>),
    /// or a gRPC status name (<c>"InvalidArgument"</c>). Falls back to
    /// <c>200</c> when the string is missing or unrecognisable — deliberately
    /// lenient because the alternative (server-side strict parsing failing
    /// at request time) is worse for the user.
    /// </summary>
    internal static int MapStatus(string? status)
    {
        if (string.IsNullOrEmpty(status)) return 200;

        if (int.TryParse(status, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var numeric) &&
            numeric is >= 100 and < 600)
            return numeric;

        if (Enum.TryParse<HttpStatusCode>(status, ignoreCase: true, out var code))
            return (int)code;

        // gRPC status names — map to the closest HTTP code.
        return status.ToUpperInvariant() switch
        {
            "OK" => 200,
            "CANCELLED" => 499,
            "UNKNOWN" => 500,
            "INVALIDARGUMENT" => 400,
            "DEADLINEEXCEEDED" => 504,
            "NOTFOUND" => 404,
            "ALREADYEXISTS" => 409,
            "PERMISSIONDENIED" => 403,
            "RESOURCEEXHAUSTED" => 429,
            "FAILEDPRECONDITION" => 400,
            "ABORTED" => 409,
            "OUTOFRANGE" => 400,
            "UNIMPLEMENTED" => 501,
            "INTERNAL" => 500,
            "UNAVAILABLE" => 503,
            "DATALOSS" => 500,
            "UNAUTHENTICATED" => 401,
            _ => 200
        };
    }

    // Socket.IO replay over the engine.io-on-WebSocket transport.
    //
    // Wire layering (client → mock):
    //   1. GET /socket.io/?EIO=4&transport=websocket with an Upgrade.
    //   2. Mock accepts the WS upgrade.
    //   3. Mock sends engine.io "open" packet (type 0) with a fake sid
    //      and empty upgrade list so the client doesn't try to upgrade
    //      the transport again.
    //   4. Client sends Socket.IO CONNECT packet ("40" or "40/ns,").
    //   5. Mock replies with CONNECT ack ("40{"sid":"..."}" or
    //      "40/ns,{"sid":"..."}").
    //   6. Mock emits recorded events as "42["eventName",payload]"
    //      paced by per-frame timestampMs.
    //   7. Ping (client sends "2") is answered with pong ("3") in the
    //      background receive loop so the session doesn't time out.
    //
    // The recorder captures each received event as
    // { event, data, timestamp } (see BowireSocketIoProtocol.
    // InvokeStreamAsync), which this method unwraps into its
    // engine.io-envelope form. Frames that are already in raw
    // "42[...]" shape pass through unchanged so hand-crafted
    // recordings work too.
    private static async Task<int> ReplaySocketIoAsync(
        HttpContext ctx, BowireRecordingStep step, MockOptions options, ILogger logger, CancellationToken ct)
    {
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();

        // engine.io OPEN (type 0) — sid is synthetic, upgrades empty so
        // the client doesn't try to negotiate an alternate transport.
        var sid = Guid.NewGuid().ToString("N")[..16];
        var openPacket = "0" + System.Text.Json.JsonSerializer.Serialize(new
        {
            sid,
            upgrades = Array.Empty<string>(),
            pingInterval = 25000,
            pingTimeout = 60000,
            maxPayload = 1_000_000
        });
        await SendSocketIoTextAsync(socket, openPacket, ct);

        // Wait for the client's CONNECT packet. SocketIOClient 4.x
        // sends it immediately after the engine.io open. Capture its
        // namespace (default "/") so the CONNECT ack and event frames
        // target the same namespace.
        var connectFrame = await ReadSocketIoTextAsync(socket, ct);
        if (connectFrame is null)
        {
            await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "no connect", ct);
            return 101;
        }

        var namespaceName = ParseSocketIoConnectNamespace(connectFrame);
        var connectAck = "40" + (namespaceName is null ? "" : namespaceName + ",") +
            "{\"sid\":\"" + Guid.NewGuid().ToString("N")[..16] + "\"}";
        await SendSocketIoTextAsync(socket, connectAck, ct);

        // Split receivedMessages into two streams at load time:
        //   - broadcasts: regular server→client events, emitted on
        //     the main timeline (paced by timestampMs).
        //   - acks:       ordered FIFO queue. Each incoming client
        //     event with an ack id (42N[...] / N>0) pops one entry
        //     and the mock sends 43N[<payload>] back so the client's
        //     callback fires.
        // Ack frames are identified by the reserved envelope event
        // name "__ack__" — user-crafted recordings opt in by using
        // that marker; broadcasts stay the default shape.
        var (broadcasts, ackQueue) = PartitionSocketIoFrames(step.ReceivedMessages);
        var ackLock = new object();

        // Background loop: drain client frames, answer engine.io pings
        // with pongs, and dispatch ack responses for events that carry
        // an ack id.
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var drainTask = Task.Run(async () =>
        {
            try
            {
                while (socket.State == System.Net.WebSockets.WebSocketState.Open && !loopCts.IsCancellationRequested)
                {
                    var frame = await ReadSocketIoTextAsync(socket, loopCts.Token);
                    if (frame is null) break;
                    if (frame.Length > 0 && frame[0] == '2' && frame.Length <= 2)
                    {
                        // engine.io PING → respond PONG. EIO v4 flips
                        // direction (server pings client) but both
                        // SocketIOClient and the official clients
                        // tolerate receiving pongs unsolicited, and
                        // our mock is fine either way.
                        await SendSocketIoTextAsync(socket, "3", loopCts.Token);
                        continue;
                    }

                    // EVENT with an ack id? Pop the next recorded ack
                    // response and emit 43<id>[<payload>] in the same
                    // namespace the client used.
                    if (TryParseSocketIoEventWithAckId(frame, out var clientNamespace, out var ackId))
                    {
                        string? ackPayload = null;
                        lock (ackLock)
                        {
                            if (ackQueue.Count > 0) ackPayload = ackQueue.Dequeue();
                        }
                        if (ackPayload is null)
                        {
                            logger.LogDebug(
                                "socket.io-ack: client sent ack id {AckId} but no ack response queued on step {StepId}.",
                                ackId, step.Id);
                            continue;
                        }
                        var ackNamespace = clientNamespace ?? namespaceName;
                        var ackFrame = "43" +
                            (ackNamespace is null ? "" : ackNamespace + ",") +
                            ackId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            ackPayload;
                        ackFrame = ResponseBodySubstitutor.Substitute(ackFrame);
                        try { await SendSocketIoTextAsync(socket, ackFrame, loopCts.Token); }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "socket.io-ack emit failed for ack id {AckId}", ackId);
                        }
                    }
                }
            }
            catch { /* client hung up / cancellation */ }
        }, loopCts.Token);

        if (broadcasts.Count > 0)
        {
            var speed = options.ReplaySpeed;
            var pace = speed > 0;
            long lastTimestampMs = 0;

            foreach (var frame in broadcasts)
            {
                ct.ThrowIfCancellationRequested();
                if (socket.State != System.Net.WebSockets.WebSocketState.Open) break;

                if (pace && frame.TimestampMs is long frameTs && frameTs > lastTimestampMs)
                {
                    var waitMs = (long)((frameTs - lastTimestampMs) / speed);
                    if (waitMs > 0)
                    {
                        try { await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct); }
                        catch (OperationCanceledException) { break; }
                    }
                    lastTimestampMs = frameTs;
                }

                // Binary event? Envelope carries a `binary` field with
                // base64-encoded attachment bytes. Emit engine.io type-5
                // BINARY_EVENT with a placeholder, then the binary
                // payload as a raw WebSocket binary frame.
                if (TryExtractSocketIoBinaryFrame(frame.Data, namespaceName, out var header, out var binaryBytes))
                {
                    try
                    {
                        await SendSocketIoTextAsync(socket, header!, ct);
                        await socket.SendAsync(
                            binaryBytes!,
                            System.Net.WebSockets.WebSocketMessageType.Binary,
                            endOfMessage: true,
                            ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "socket.io-binary emit failed for frame {Index} of step {StepId}", frame.Index, step.Id);
                    }
                    continue;
                }

                var wireText = FormatSocketIoEventFrame(frame.Data, namespaceName);
                if (string.IsNullOrEmpty(wireText)) continue;

                wireText = ResponseBodySubstitutor.Substitute(wireText);
                try { await SendSocketIoTextAsync(socket, wireText, ct); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "socket.io-emit failed for frame {Index} of step {StepId}", frame.Index, step.Id);
                }
            }
        }

        // Server-push is done, but the client may still be sending
        // pings / explicit DISCONNECT / raw close. Keep the socket
        // alive up to an idle guard so ping-pong has room to happen
        // and the client drives the close — same pattern as the
        // SignalR replayer. Ping answering happens in drainTask.
        try { await drainTask.WaitAsync(TimeSpan.FromSeconds(30), ct); }
        catch (TimeoutException) { /* idle — close below */ }
        catch (OperationCanceledException) { /* shutdown */ }

        await loopCts.CancelAsync();
        try { await drainTask; } catch { /* already cancelled */ }

        if (socket.State is System.Net.WebSockets.WebSocketState.Open
                         or System.Net.WebSockets.WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(
                    System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "replay complete", ct);
            }
            catch { /* best-effort */ }
        }
        return 101;
    }

    // CONNECT packet shapes we parse:
    //   "40"                       default namespace
    //   "40/ns,"                   named namespace
    //   "40/ns,{\"token\":\"x\"}"  named namespace + auth payload
    // Returns the namespace name (starting with "/") when present,
    // else null = default namespace.
    private static string? ParseSocketIoConnectNamespace(string frame)
    {
        if (!frame.StartsWith("40", StringComparison.Ordinal)) return null;
        if (frame.Length <= 2) return null;
        var rest = frame[2..];
        if (rest[0] != '/') return null;
        var commaIdx = rest.IndexOf(',');
        return commaIdx < 0 ? rest : rest[..commaIdx];
    }

    // Convert a captured frame payload into the Socket.IO wire-
    // format EVENT packet ("42[...]"). Three shapes accepted:
    //
    //   - {event, data, timestamp} envelope (what the capture side
    //     persists) → re-encoded as 42["<event>",<data>].
    //   - raw "42[...]" string already in wire shape → passed through.
    //   - anything else → stringified JSON wrapped in a generic
    //     "message" event so at least the payload reaches the client.
    internal static string FormatSocketIoEventFrame(object? frameData, string? namespaceName)
    {
        if (frameData is null) return "";
        string raw;
        if (frameData is string s)
        {
            if (s.StartsWith("42", StringComparison.Ordinal)) return s;
            raw = s;
        }
        else if (frameData is System.Text.Json.JsonElement el)
        {
            raw = el.GetRawText();
        }
        else
        {
            raw = System.Text.Json.JsonSerializer.Serialize(frameData);
        }

        string? eventName = null;
        string? dataJson = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("event", out var evEl) &&
                    evEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    eventName = evEl.GetString();
                }
                if (doc.RootElement.TryGetProperty("data", out var dataEl))
                {
                    dataJson = dataEl.ValueKind == System.Text.Json.JsonValueKind.Null
                        ? "null"
                        : dataEl.GetRawText();
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON — fall back to message wrapper below.
        }

        var namespacePart = namespaceName is null ? "" : namespaceName + ",";

        if (!string.IsNullOrEmpty(eventName))
        {
            var eventJson = System.Text.Json.JsonSerializer.Serialize(eventName);
            return dataJson is null
                ? "42" + namespacePart + "[" + eventJson + "]"
                : "42" + namespacePart + "[" + eventJson + "," + dataJson + "]";
        }

        // No envelope — wrap the whole payload as the "message" event,
        // which is the default catch-all listener name in Socket.IO.
        var payloadJson = raw.TrimStart() is { Length: > 0 } trimmed && (trimmed[0] == '{' || trimmed[0] == '[' || trimmed[0] == '"' || trimmed[0] == 't' || trimmed[0] == 'f' || trimmed[0] == 'n' || char.IsDigit(trimmed[0]) || trimmed[0] == '-')
            ? raw
            : System.Text.Json.JsonSerializer.Serialize(raw);
        return "42" + namespacePart + "[\"message\"," + payloadJson + "]";
    }

    // Split receivedMessages into broadcast events + a FIFO queue of
    // recorded ack responses. A frame is an "ack response" when its
    // envelope's event name is the reserved marker "__ack__"; the
    // queued entry is the JSON array the mock appends after the ack
    // id, e.g. the "[\"ok\",42]" in `431["ok",42]`.
    internal static (List<BowireRecordingFrame> Broadcasts, Queue<string> AckResponses)
        PartitionSocketIoFrames(IList<BowireRecordingFrame>? frames)
    {
        var broadcasts = new List<BowireRecordingFrame>();
        var acks = new Queue<string>();
        if (frames is null) return (broadcasts, acks);

        foreach (var frame in frames)
        {
            if (frame.Data is null) { broadcasts.Add(frame); continue; }

            string raw;
            if (frame.Data is string s) raw = s;
            else if (frame.Data is System.Text.Json.JsonElement el) raw = el.GetRawText();
            else raw = System.Text.Json.JsonSerializer.Serialize(frame.Data);

            var isAck = false;
            string? dataJson = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("event", out var evEl) &&
                    evEl.ValueKind == System.Text.Json.JsonValueKind.String &&
                    string.Equals(evEl.GetString(), "__ack__", StringComparison.Ordinal))
                {
                    isAck = true;
                    if (doc.RootElement.TryGetProperty("data", out var dataEl))
                    {
                        dataJson = dataEl.ValueKind == System.Text.Json.JsonValueKind.Null
                            ? "null"
                            : dataEl.GetRawText();
                    }
                }
            }
            catch (System.Text.Json.JsonException) { /* not an envelope — broadcast */ }

            if (isAck)
            {
                // Ack payload on the wire must be a JSON array so the
                // client library can destructure into callback args.
                // Wrap scalar/object payloads in a single-element array;
                // pre-wrapped arrays pass through verbatim.
                var argsJson = dataJson switch
                {
                    null => "[]",
                    _ when dataJson.TrimStart().StartsWith('[') => dataJson,
                    _ => "[" + dataJson + "]"
                };
                acks.Enqueue(argsJson);
            }
            else
            {
                broadcasts.Add(frame);
            }
        }
        return (broadcasts, acks);
    }

    // Parse a client-sent Socket.IO EVENT frame and extract the ack id
    // + namespace. Returns false when the frame isn't an EVENT or has
    // no ack id. Shapes:
    //   42[...]            EVENT, no ack id → false
    //   421[...]           EVENT, ack id 1 → true (ack=1, ns=null)
    //   42/foo,7[...]      EVENT, ack id 7 in /foo namespace → true
    internal static bool TryParseSocketIoEventWithAckId(
        string frame, out string? namespaceName, out int ackId)
    {
        namespaceName = null;
        ackId = 0;

        if (frame.Length < 3 || frame[0] != '4' || frame[1] != '2') return false;

        var i = 2;
        if (i < frame.Length && frame[i] == '/')
        {
            var commaIdx = frame.IndexOf(',', i);
            if (commaIdx < 0) return false;
            namespaceName = frame[i..commaIdx];
            i = commaIdx + 1;
        }

        var digitStart = i;
        while (i < frame.Length && frame[i] >= '0' && frame[i] <= '9') i++;
        if (i == digitStart) return false; // no digits = no ack id
        if (i >= frame.Length || frame[i] != '[') return false;

        return int.TryParse(
            frame.AsSpan(digitStart, i - digitStart),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out ackId);
    }

    // Recognise a binary-event capture envelope:
    //   { "event": "name", "binary": "<base64>" }             single attachment, no other data
    //   { "event": "name", "data": <json>, "binary": "<b64>" } single attachment + JSON metadata
    //
    // Returns the engine.io type-5 header ("5<n>-[...]") to send as
    // text plus the decoded attachment bytes for the follow-up
    // WebSocket binary frame. Multi-attachment events are a future
    // slice — for now num=0 is the only placeholder emitted.
    internal static bool TryExtractSocketIoBinaryFrame(
        object? frameData, string? namespaceName,
        out string? header, out byte[]? binaryBytes)
    {
        header = null;
        binaryBytes = null;
        if (frameData is null) return false;

        string raw = frameData switch
        {
            string s => s,
            System.Text.Json.JsonElement el => el.GetRawText(),
            _ => System.Text.Json.JsonSerializer.Serialize(frameData)
        };

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return false;

            if (!doc.RootElement.TryGetProperty("binary", out var binEl) ||
                binEl.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return false;
            }

            var eventName = doc.RootElement.TryGetProperty("event", out var evEl) &&
                            evEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? evEl.GetString() ?? "message"
                : "message";

            try { binaryBytes = Convert.FromBase64String(binEl.GetString()!); }
            catch (FormatException) { return false; }

            // Build the placeholder-carrying text envelope. When the
            // frame has a `data` field alongside `binary`, pass it
            // through as an extra array element; otherwise the
            // placeholder is the only arg.
            var namespacePart = namespaceName is null ? "" : namespaceName + ",";
            var eventJson = System.Text.Json.JsonSerializer.Serialize(eventName);
            string argsBody;
            if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
                dataEl.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                argsBody = "[" + eventJson + "," + dataEl.GetRawText() + ",{\"_placeholder\":true,\"num\":0}]";
            }
            else
            {
                argsBody = "[" + eventJson + ",{\"_placeholder\":true,\"num\":0}]";
            }

            header = "51-" + namespacePart + argsBody;
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static async Task SendSocketIoTextAsync(
        System.Net.WebSockets.WebSocket socket, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            System.Net.WebSockets.WebSocketMessageType.Text,
            endOfMessage: true,
            ct);
    }

    private static async Task<string?> ReadSocketIoTextAsync(
        System.Net.WebSockets.WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        await using var ms = new MemoryStream();
        while (true)
        {
            System.Net.WebSockets.WebSocketReceiveResult result;
            try { result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct); }
            catch { return null; }
            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) return null;
            await ms.WriteAsync(buffer.AsMemory(0, result.Count), ct);
            if (result.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // gRPC status-code enum values per grpc.io spec.
    internal static (int Code, string? Message) MapToGrpcStatus(string? status)
    {
        if (string.IsNullOrEmpty(status)) return (0, null);
        return status.ToUpperInvariant() switch
        {
            "OK" => (0, null),
            "CANCELLED" => (1, "Cancelled"),
            "UNKNOWN" => (2, "Unknown"),
            "INVALIDARGUMENT" => (3, "InvalidArgument"),
            "DEADLINEEXCEEDED" => (4, "DeadlineExceeded"),
            "NOTFOUND" => (5, "NotFound"),
            "ALREADYEXISTS" => (6, "AlreadyExists"),
            "PERMISSIONDENIED" => (7, "PermissionDenied"),
            "RESOURCEEXHAUSTED" => (8, "ResourceExhausted"),
            "FAILEDPRECONDITION" => (9, "FailedPrecondition"),
            "ABORTED" => (10, "Aborted"),
            "OUTOFRANGE" => (11, "OutOfRange"),
            "UNIMPLEMENTED" => (12, "Unimplemented"),
            "INTERNAL" => (13, "Internal"),
            "UNAVAILABLE" => (14, "Unavailable"),
            "DATALOSS" => (15, "DataLoss"),
            "UNAUTHENTICATED" => (16, "Unauthenticated"),
            _ => (0, null)
        };
    }

    private static async Task<int> Not501(HttpContext ctx, ILogger logger, string message, CancellationToken ct)
    {
        logger.LogWarning("{Message}", message);
        ctx.Response.StatusCode = 501;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var payload = $"{{\"error\":\"{EscapeJson(message)}\"}}";
        await ctx.Response.WriteAsync(payload, ct);
        return 501;
    }

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal)
         .Replace("\"", "\\\"", StringComparison.Ordinal)
         .Replace("\n", "\\n", StringComparison.Ordinal)
         .Replace("\r", "\\r", StringComparison.Ordinal);
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Mock.Capture;

/// <summary>
/// Phase 3c: writes unmatched requests into a capture file so the user
/// can turn misses into future recording steps. The writer appends to
/// an existing <see cref="BowireRecording"/> JSON file or creates one
/// with a single-step skeleton on first miss.
/// </summary>
/// <remarks>
/// <para>
/// The captured step carries <c>body</c>, headers (as <c>metadata</c>),
/// verb and path; response/status are left as placeholders (<c>""</c> /
/// <c>"Unknown"</c>) because the mock never saw a real response for this
/// request. The user fills those in manually before replaying the
/// captured recording against the mock.
/// </para>
/// <para>
/// gRPC is captured too: the service/method split out of the HTTP/2
/// <c>:path</c>, the first length-prefixed request frame base64-encoded
/// into <c>requestBinary</c> (payload only, 5-byte envelope stripped),
/// and <c>responseBinary</c> left blank for the user to fill in. Only
/// the first request frame lands in the step for now — multi-message
/// client-streaming misses keep only the opener, which is usually
/// enough to identify which RPC needs a response.
/// </para>
/// </remarks>
internal static class MissCaptureWriter
{
    // One-writer-at-a-time across the whole process. A mock server in dev
    // mode rarely has enough concurrent misses to warrant per-file locks;
    // global serialisation is the simpler correct model.
    private static readonly SemaphoreSlim s_writerLock = new(1, 1);

    // Strip CR/LF from request paths and identifiers before they're
    // formatted into log messages, so an attacker can't smuggle fake
    // log lines via a crafted ?path=foo%0d%0afake-line.
    private static string Safe(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty
            : s.Replace('\r', '_').Replace('\n', '_');

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Cap on request body size we copy into the capture file. Larger
    // bodies are truncated with a trailing marker so the capture file
    // doesn't balloon on binary uploads.
    private const int MaxBodyBytes = 1024 * 1024;

    public static async Task CaptureAsync(
        string path,
        HttpContext ctx,
        ILogger logger,
        CancellationToken ct)
    {
        var contentType = ctx.Request.ContentType;
        var isGrpc = contentType is not null &&
            contentType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase);

        var step = isGrpc
            ? await BuildGrpcStepAsync(ctx, logger, ct)
            : await BuildRestStepAsync(ctx, logger, ct);

        await s_writerLock.WaitAsync(ct);
        try
        {
            var recording = await LoadOrCreateAsync(path, ct);
            recording.Steps.Add(step);
            await SaveAsync(path, recording, ct);
            logger.LogInformation(
                "miss-capture: appended {StepId} ({Protocol} {Verb} {Path}) to {File}",
                step.Id, step.Protocol, Safe(step.HttpVerb), Safe(step.HttpPath ?? ("/" + step.Service + "/" + step.Method)), path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "miss-capture: failed to persist miss for {Path} to {File}",
                Safe(ctx.Request.Path.Value), path);
        }
        finally
        {
            s_writerLock.Release();
        }
    }

    private static async Task<BowireRecordingStep> BuildRestStepAsync(
        HttpContext ctx, ILogger logger, CancellationToken ct)
    {
        string? body = null;
        try
        {
            if (ctx.Request.Body is { CanRead: true, CanSeek: true })
            {
                ctx.Request.Body.Position = 0;
                using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
                var raw = await reader.ReadToEndAsync(ct);
                if (raw.Length > MaxBodyBytes)
                {
                    body = raw[..MaxBodyBytes] + "\n/* …truncated — body exceeded " +
                        MaxBodyBytes + " bytes */";
                }
                else if (raw.Length > 0)
                {
                    body = raw;
                }
                ctx.Request.Body.Position = 0; // rewind for any downstream middleware
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "miss-capture: failed to read REST request body for {Path}", Safe(ctx.Request.Path.Value));
        }

        var headers = CollectPortableHeaders(ctx);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return new BowireRecordingStep
        {
            Id = NewStepId(now),
            CapturedAt = now,
            Protocol = "rest",
            Service = "Captured",
            Method = ctx.Request.Method + " " + (ctx.Request.Path.Value ?? "/"),
            MethodType = "Unary",
            HttpPath = ctx.Request.Path.Value ?? "/",
            HttpVerb = ctx.Request.Method,
            Body = body,
            Metadata = headers.Count > 0 ? headers : null,
            Status = "Unknown",
            Response = null
        };
    }

    private static async Task<BowireRecordingStep> BuildGrpcStepAsync(
        HttpContext ctx, ILogger logger, CancellationToken ct)
    {
        // gRPC URL shape is /<service>/<method>. Split the two segments
        // so the replayer's matcher can pair a recorded step against
        // the same RPC — matches ExactMatcher.MatchesGrpcPath.
        var pathValue = ctx.Request.Path.Value ?? "/";
        var segments = pathValue.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var service = segments.Length > 0 ? segments[0] : "Captured";
        var method = segments.Length > 1 ? segments[1] : "Unknown";

        string? requestBinary = null;
        try
        {
            if (ctx.Request.Body is { CanRead: true, CanSeek: true })
            {
                ctx.Request.Body.Position = 0;
                requestBinary = await ReadFirstGrpcFrameAsBase64Async(ctx.Request.Body, ct);
                ctx.Request.Body.Position = 0;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "miss-capture: failed to read gRPC request frame for {Path}", Safe(ctx.Request.Path.Value));
        }

        var headers = CollectPortableHeaders(ctx);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return new BowireRecordingStep
        {
            Id = NewStepId(now),
            CapturedAt = now,
            Protocol = "grpc",
            Service = service,
            Method = method,
            // Matcher keys gRPC on service+method regardless of MethodType,
            // so "Unary" is a safe default; the user can upgrade it to
            // ServerStreaming / ClientStreaming / Duplex when filling in
            // the response.
            MethodType = "Unary",
            Body = null,
            RequestBinary = requestBinary,
            Metadata = headers.Count > 0 ? headers : null,
            Status = "Unknown",
            Response = null,
            ResponseBinary = null
        };
    }

    // Read one length-prefixed gRPC frame: 1-byte compression flag + 4-byte
    // big-endian length + payload. Returns the base64 of the payload only
    // (envelope stripped) so the user sees the message the client sent, not
    // the wire framing. Returns null when the stream is empty or malformed —
    // the step still gets written so the user knows the RPC path was hit.
    private static async Task<string?> ReadFirstGrpcFrameAsBase64Async(Stream stream, CancellationToken ct)
    {
        var header = new byte[5];
        var read = await ReadExactAsync(stream, header, 0, 5, ct);
        if (read < 5) return null;

        var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1, 4));
        if (length < 0 || length > MaxBodyBytes)
        {
            // Defensive: truncate at MaxBodyBytes to keep the capture
            // file bounded, same cap as REST.
            length = Math.Min(Math.Max(length, 0), MaxBodyBytes);
        }
        if (length == 0) return "";

        var payload = new byte[length];
        var payloadRead = await ReadExactAsync(stream, payload, 0, length, ct);
        return payloadRead == length ? Convert.ToBase64String(payload) : null;
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
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

    private static Dictionary<string, string> CollectPortableHeaders(HttpContext ctx) =>
        ctx.Request.Headers
            .Where(h => !IsHopByHopHeader(h.Key))
            .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

    private static string NewStepId(long nowMs) =>
        "miss_" + nowMs.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "_" + Guid.NewGuid().ToString("N")[..6];

    private static async Task<BowireRecording> LoadOrCreateAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return new BowireRecording
            {
                Id = "miss-capture",
                Name = "Captured misses",
                Description = "Requests that didn't match any recorded step. Fill in response/status to replay.",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                RecordingFormatVersion = Loading.RecordingFormatVersion.Current
            };
        }

        var json = await File.ReadAllTextAsync(path, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new BowireRecording
            {
                Id = "miss-capture",
                Name = "Captured misses",
                RecordingFormatVersion = Loading.RecordingFormatVersion.Current
            };
        }

        var parsed = JsonSerializer.Deserialize<BowireRecording>(json, s_jsonOptions);
        if (parsed is null || parsed.Steps is null)
        {
            // File exists but isn't a plain recording — bail rather than
            // clobber something we don't understand.
            throw new InvalidDataException(
                $"Capture file '{path}' exists but isn't a single-recording JSON document.");
        }
        return parsed;
    }

    private static async Task SaveAsync(string path, BowireRecording recording, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(recording, s_jsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
    }

    // Hop-by-hop headers and ASP.NET-injected headers that shouldn't be
    // persisted — they're about the current connection, not the semantic
    // request the user would want to replay.
    private static bool IsHopByHopHeader(string name) =>
        name.StartsWith(':') ||
        string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Upgrade", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "TE", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Trailer", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Proxy-Authenticate", StringComparison.OrdinalIgnoreCase);
}

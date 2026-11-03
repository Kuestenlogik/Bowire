// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.Mock.Mcp;

/// <summary>
/// An MCP server made from a recording (#803). The workbench records an MCP
/// tool call as the plugin saw it — the tool, its arguments and the frames it
/// streamed — not as JSON-RPC on the wire, so the generic REST and SSE replay
/// cannot serve it. This answers the MCP calls a client makes, over
/// Streamable HTTP, on the path of every server the recording called:
/// <c>server/discover</c>, <c>initialize</c>, <c>ping</c>,
/// <c>tools/list</c> (the recorded tools) and <c>tools/call</c> (the
/// recorded result). A call recorded as a task becomes a task again for a
/// client that declares the <c>io.modelcontextprotocol/tasks</c> extension
/// (<see cref="McpMockTask"/>), with <c>tasks/get</c>, <c>tasks/update</c>
/// and <c>tasks/cancel</c>; any other client gets its result directly.
/// </summary>
/// <remarks>
/// It answers only what no recorded step matched, so a wire-level capture of
/// an MCP endpoint (a step with an HTTP route) keeps replaying as it did.
/// Responses are single JSON documents; the recorded progress and log frames
/// are not re-sent, and the tasks are the extension's shape (2026-07-28),
/// whichever shape the recording was made against.
/// </remarks>
internal sealed class McpMockEndpoint
{
    private const string TasksExtension = "io.modelcontextprotocol/tasks";
    private const string ClientCapabilities = "io.modelcontextprotocol/clientCapabilities";
    private static readonly string[] Versions = ["2026-07-28", "2025-11-25", "2025-06-18", "2025-03-26"];

    private readonly ConcurrentDictionary<string, McpMockTask> _tasks = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private int _nextTask;

    public McpMockEndpoint(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>The paths the recording's MCP servers were called on; empty when it has no tool-level MCP step.</summary>
    public static HashSet<string> PathsOf(BowireRecording recording)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in recording.Steps)
        {
            if (!McpRecordedCall.IsToolStep(step)) continue;
            var path = Uri.TryCreate(step.ServerUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : "/";
            paths.Add(path.Length > 1 ? path.TrimEnd('/') : "/");
        }
        return paths;
    }

    /// <summary>
    /// Answer the request when it is an MCP call on one of the recording's
    /// server paths; <see langword="false"/> leaves it to the rest of the
    /// pipeline. <paramref name="body"/> is the request body when the caller
    /// has already read it.
    /// </summary>
    public async Task<bool> TryHandleAsync(HttpContext ctx, BowireRecording recording, double speed, string? body, Action<string> matched)
    {
        var path = ctx.Request.Path.Value ?? "/";
        if (path.Length > 1) path = path.TrimEnd('/');
        if (!PathsOf(recording).Contains(path)) return false;

        if (!HttpMethods.IsPost(ctx.Request.Method))
        {
            // No server-to-client stream and no session to end: what a
            // stateless Streamable HTTP server answers.
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return true;
        }

        body ??= await MockHandler.ReadBufferedBodyAsync(ctx).ConfigureAwait(false);
        JsonObject? request;
        try { request = JsonNode.Parse(body ?? "") as JsonObject; }
        catch (JsonException) { request = null; }
        if (request is null)
        {
            await WriteAsync(ctx, Error(null, -32700, "Parse error: the body is not a JSON-RPC message."), StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return true;
        }

        var id = request["id"]?.DeepClone();
        if (id is null || request["method"] is null)
        {
            // A notification, or a response to a request this server never sends.
            ctx.Response.StatusCode = StatusCodes.Status202Accepted;
            return true;
        }

        var method = request["method"]!.GetValue<string>();
        var p = request["params"] as JsonObject ?? [];
        var (result, error) = Dispatch(method, p, recording, speed, matched);
        var response = error is { } e
            ? Error(id, e.Code, e.Message)
            : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        await WriteAsync(ctx, response, StatusCodes.Status200OK).ConfigureAwait(false);
        return true;
    }

    /// <summary>A JSON-RPC error to answer with.</summary>
    private readonly record struct Fault(int Code, string Message);

    private static (JsonNode? Result, Fault? Error) Ok(JsonNode result) => (result, null);
    private static (JsonNode? Result, Fault? Error) Fail(int code, string message) => (null, new Fault(code, message));

    private (JsonNode? Result, Fault? Error) Dispatch(string method, JsonObject p, BowireRecording recording, double speed, Action<string> matched)
    {
        switch (method)
        {
            case "server/discover":
                return Ok(new JsonObject
                {
                    ["supportedVersions"] = new JsonArray([.. Versions.Select(v => (JsonNode)v)]),
                    ["capabilities"] = Capabilities(),
                    ["serverInfo"] = ServerInfo(recording),
                    ["resultType"] = "complete",
                });
            case "initialize":
            {
                var asked = p["protocolVersion"]?.GetValue<string>();
                return Ok(new JsonObject
                {
                    ["protocolVersion"] = asked is not null && Versions.Contains(asked) ? asked : Versions[0],
                    ["capabilities"] = Capabilities(),
                    ["serverInfo"] = ServerInfo(recording),
                });
            }
            case "ping":
                return Ok(new JsonObject());
            case "tools/list":
                return Ok(new JsonObject { ["tools"] = Tools(recording), ["resultType"] = "complete" });
            case "tools/call":
                return CallTool(p, recording, speed, matched);
            case "tasks/get":
            {
                if (Find(p) is not { } found) return Fail(-32602, Unknown(p));
                var task = found.Snapshot();
                task["resultType"] = "complete";
                return Ok(task);
            }
            case "tasks/update":
            {
                if (Find(p) is not { } found) return Fail(-32602, Unknown(p));
                if (p["inputResponses"] is not JsonObject responses) return Fail(-32602, "tasks/update needs inputResponses.");
                return found.Answer(JsonSerializer.SerializeToElement(responses)) is { } refused
                    ? Fail(-32602, refused)
                    : Ok(new JsonObject { ["resultType"] = "complete" });
            }
            case "tasks/cancel":
            {
                if (Find(p) is not { } found) return Fail(-32602, Unknown(p));
                found.Cancel();
                return Ok(new JsonObject { ["resultType"] = "complete" });
            }
            default:
                return Fail(-32601, $"Method not found: {method}");
        }
    }

    private (JsonNode? Result, Fault? Error) CallTool(JsonObject p, BowireRecording recording, double speed, Action<string> matched)
    {
        if (p["name"]?.GetValue<string>() is not { } name) return Fail(-32602, "tools/call needs a name.");
        if (Choose(recording, name, p["arguments"]) is not { } call) return Fail(-32602, $"Unknown tool: {name}. The recording has no call of it.");
        matched(call.Step.Id);

        var optedIn = p["_meta"]?[ClientCapabilities]?["extensions"]?[TasksExtension] is not null;
        if (call.IsTask && optedIn)
        {
            var taskId = "mock-task-" + Interlocked.Increment(ref _nextTask).ToString("D4", CultureInfo.InvariantCulture);
            var task = new McpMockTask(taskId, call, speed, _clock);
            _tasks[taskId] = task;
            var created = task.Snapshot();
            created.Remove("result");
            created.Remove("error");
            created["resultType"] = "task";
            return Ok(created);
        }

        if (call.Result is { } result) return Ok(JsonNode.Parse(result.GetRawText())!);
        if (call.Error is not { } error) return Fail(-32603, $"The recording of '{name}' has no result to replay.");
        var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : -32603;
        return Fail(code, message ?? $"The recorded call of '{name}' failed.");
    }

    // The recorded call of the tool whose arguments match; else the first of the tool.
    private static McpRecordedCall? Choose(BowireRecording recording, string name, JsonNode? arguments)
    {
        var calls = recording.Steps
            .Where(s => McpRecordedCall.IsToolStep(s) && string.Equals(s.Method, name, StringComparison.Ordinal))
            .Select(McpRecordedCall.From)
            .ToList();
        if (calls.Count == 0) return null;
        var wanted = arguments?.ToJsonString() is { } json ? JsonNode.Parse(json) : new JsonObject();
        return calls.FirstOrDefault(c => JsonNode.DeepEquals(c.Arguments is { } a ? JsonNode.Parse(a.GetRawText()) : new JsonObject(), wanted))
            ?? calls[0];
    }

    private McpMockTask? Find(JsonObject p) =>
        p["taskId"]?.GetValue<string>() is { } taskId && _tasks.TryGetValue(taskId, out var task) ? task : null;

    // -32602 is the spec's answer for an unknown task id.
    private static string Unknown(JsonObject p) =>
        p["taskId"]?.GetValue<string>() is { } taskId
            ? $"Failed to retrieve task: no task '{taskId}' in this mock."
            : "taskId is required.";

    private static JsonArray Tools(BowireRecording recording) =>
        new([.. recording.Steps
            .Where(McpRecordedCall.IsToolStep)
            .Select(s => s.Method!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(name => (JsonNode)new JsonObject
            {
                ["name"] = name,
                ["description"] = $"Replayed from the recording by the Bowire mock.",
                ["inputSchema"] = new JsonObject { ["type"] = "object" },
            })]);

    private static JsonObject Capabilities() => new()
    {
        ["tools"] = new JsonObject(),
        ["extensions"] = new JsonObject { [TasksExtension] = new JsonObject() },
    };

    private static JsonObject ServerInfo(BowireRecording recording) => new()
    {
        ["name"] = "bowire-mock",
        ["title"] = string.IsNullOrEmpty(recording.Name) ? "Bowire mock" : $"Bowire mock of {recording.Name}",
        ["version"] = typeof(McpMockEndpoint).Assembly.GetName().Version?.ToString() ?? "0",
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static async Task WriteAsync(HttpContext ctx, JsonObject message, int status)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(message.ToJsonString(), ctx.RequestAborted).ConfigureAwait(false);
    }
}

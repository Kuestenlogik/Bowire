// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tests;

/// <summary>
/// A task server written against the wire, for what an SDK-built server
/// cannot be made to do (#801): the 2025-11-25 task shape, which SDK 2.2.0
/// no longer models; a <c>subscriptions/listen</c> that agrees to push a
/// task's status; a TTL the server lets lapse; a task it has purged; a
/// 2025-11-25 task that asks its question as an <c>elicitation/create</c>
/// on <c>tasks/result</c> (#821).
/// </summary>
/// <remarks>
/// Every task starts <c>working</c> and stays there until the test moves
/// it — <see cref="Transition"/> — so a test decides when a poll sees what,
/// and reads the frames as they arrive rather than waiting on a clock.
/// </remarks>
internal sealed class FakeTaskServer : IAsyncDisposable
{
    internal enum Shape { Extension, Legacy }

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, JsonObject> _tasks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JsonNode> _outcomes = new(StringComparer.Ordinal);
    private readonly List<Channel<JsonObject>> _listeners = [];
    private readonly Lock _gate = new();
    private int _next;

    public Shape Mode { get; }
    public string Url { get; }

    /// <summary>The extension shape: agree to push status on <c>subscriptions/listen</c>.</summary>
    public bool PushStatus { get; init; }

    public long? TtlMs { get; init; } = 60_000;
    public long PollIntervalMs { get; init; } = 20;

    /// <summary>tasks/get calls seen, per task.</summary>
    public ConcurrentDictionary<string, int> Polls { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether the last tools/call declared the tasks extension in its per-request capabilities.</summary>
    public bool? LastCallOptedIn { get; private set; }

    /// <summary>Completes once a subscriptions/listen has been acknowledged.</summary>
    public Task Listening => _listening.Task;

    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The inputResponses of every tasks/update.</summary>
    public ConcurrentQueue<JsonObject> Updates { get; } = new();

    /// <summary>The client capabilities of every initialize, in order.</summary>
    public ConcurrentQueue<JsonObject> Initializations { get; } = new();

    /// <summary>The 2025-11-25 shape: the elicitation/create requests sent, and what the client answered.</summary>
    public ConcurrentQueue<(JsonObject Request, JsonObject Answer)> Elicitations { get; } = new();

    // 2025-11-25: the question each task asks on its next tasks/result,
    // and the client's answers to the server's requests, by JSON-RPC id.
    private readonly ConcurrentDictionary<string, string> _questions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _answers = new(StringComparer.Ordinal);

    private FakeTaskServer(WebApplication app, string url, Shape mode)
    {
        _app = app;
        Url = url;
        Mode = mode;
    }

    public static async Task<FakeTaskServer> StartAsync(
        Shape mode, CancellationToken ct, bool pushStatus = false, long? ttlMs = 60_000, long pollIntervalMs = 20)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        FakeTaskServer? server = null;
        app.MapPost("/", (HttpContext http) => server!.HandleAsync(http));
        app.MapGet("/", () => Results.StatusCode(405));
        app.MapDelete("/", () => Results.StatusCode(405));
        await app.StartAsync(ct);
        server = new FakeTaskServer(app, app.Urls.First(), mode)
        {
            PushStatus = pushStatus,
            TtlMs = ttlMs,
            PollIntervalMs = pollIntervalMs,
        };
        return server;
    }

    /// <summary>The ids of the tasks created so far, oldest first.</summary>
    public IReadOnlyList<string> TaskIds => [.. _tasks.Keys.OrderBy(k => k, StringComparer.Ordinal)];

    /// <summary>
    /// Move a task on: <c>working</c>, <c>input_required</c> (with
    /// <paramref name="inputRequests"/>), <c>completed</c> (with
    /// <paramref name="result"/>), <c>failed</c> (with <paramref name="error"/>),
    /// <c>cancelled</c>, or <c>expired</c> — gone, as a purge would leave it.
    /// </summary>
    public void Transition(string taskId, string status, JsonObject? result = null, JsonObject? error = null, JsonObject? inputRequests = null, string? message = null)
    {
        if (status == "expired")
        {
            _tasks.TryRemove(taskId, out _);
            return;
        }
        var task = _tasks[taskId];
        task["status"] = status;
        task["lastUpdatedAt"] = Now();
        if (message is not null) task["statusMessage"] = message;
        task.Remove("inputRequests");
        if (inputRequests is not null) task["inputRequests"] = inputRequests.DeepClone();
        if (result is not null) _outcomes[taskId] = result.DeepClone();
        if (error is not null) _outcomes[taskId] = new JsonObject { ["error"] = error.DeepClone() };
        Push(taskId);
    }

    /// <summary>
    /// The 2025-11-25 shape: turn the task <c>input_required</c> and ask
    /// <paramref name="question"/> as an <c>elicitation/create</c> on its
    /// next <c>tasks/result</c>. Accepted with a <c>name</c>, the task
    /// completes with "hello" and that name; declined, with "declined".
    /// </summary>
    public void Ask(string taskId, string question)
    {
        _questions[taskId] = question;
        Transition(taskId, "input_required");
    }

    private async Task HandleAsync(HttpContext http)
    {
        var request = (await JsonNode.ParseAsync(http.Request.Body))!.AsObject();
        var id = request["id"]?.DeepClone();
        if (request["method"] is null)
        {
            // The client's answer to a request of the server's.
            if (id is not null && _answers.TryGetValue(id.ToString(), out var waiting)) waiting.TrySetResult(request);
            http.Response.StatusCode = 202;
            return;
        }
        var method = request["method"]!.GetValue<string>();
        var p = request["params"] as JsonObject ?? [];
        if (method == "initialize") Initializations.Enqueue(p["capabilities"]?.DeepClone().AsObject() ?? []);

        if (id is null)
        {
            // A notification (initialized, cancelled …): nothing to answer.
            http.Response.StatusCode = 202;
            return;
        }

        if (method == "subscriptions/listen")
        {
            await ListenAsync(http, p);
            return;
        }

        if (method == "tasks/result" && Mode == Shape.Legacy
            && p["taskId"]?.GetValue<string>() is { } asked && _questions.TryRemove(asked, out var question))
        {
            await AskAsync(http, id!, asked, question);
            return;
        }

        JsonNode? result;
        JsonObject? error = null;
        try
        {
            result = Mode == Shape.Extension ? Extension(method, p) : Legacy(method, p);
        }
        catch (McpProtocolException e)
        {
            result = null;
            error = new JsonObject { ["code"] = (int)e.ErrorCode, ["message"] = e.Message };
        }

        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
        if (error is not null) response["error"] = error;
        else response["result"] = result;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(response.ToJsonString());
    }

    private JsonObject Extension(string method, JsonObject p)
    {
        switch (method)
        {
            case "server/discover":
                return new JsonObject
                {
                    ["supportedVersions"] = new JsonArray("2026-07-28"),
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject(),
                        ["extensions"] = new JsonObject { ["io.modelcontextprotocol/tasks"] = new JsonObject() },
                    },
                    ["resultType"] = "complete",
                };
            case "tools/list":
                return new JsonObject { ["tools"] = new JsonArray(Tool("job")) };
            case "tools/call":
            {
                var optedIn = p["_meta"]?["io.modelcontextprotocol/clientCapabilities"]?["extensions"]?["io.modelcontextprotocol/tasks"] is not null;
                LastCallOptedIn = optedIn;
                if (!optedIn) return Content("ran without a task");
                var task = Create();
                var created = task.DeepClone().AsObject();
                created["resultType"] = "task";
                return created;
            }
            case "tasks/get":
            {
                var task = Find(p);
                Polls.AddOrUpdate(task["taskId"]!.GetValue<string>(), 1, (_, n) => n + 1);
                var detailed = Detailed(task);
                detailed["resultType"] = "complete";
                return detailed;
            }
            case "tasks/update":
                Updates.Enqueue(p["inputResponses"]!.DeepClone().AsObject());
                Find(p);
                return new JsonObject { ["resultType"] = "complete" };
            case "tasks/cancel":
            {
                var task = Find(p);
                Transition(task["taskId"]!.GetValue<string>(), "cancelled", message: "Cancelled on request.");
                return new JsonObject { ["resultType"] = "complete" };
            }
            default:
                throw new McpProtocolException($"Method not found: {method}", McpErrorCode.MethodNotFound);
        }
    }

    private JsonObject Legacy(string method, JsonObject p)
    {
        switch (method)
        {
            case "server/discover":
                // A 2025-11-25 server has no discover; the SDK falls back to initialize.
                throw new McpProtocolException("Method not found: server/discover", McpErrorCode.MethodNotFound);
            case "initialize":
                return new JsonObject
                {
                    ["protocolVersion"] = "2025-11-25",
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject(),
                        ["tasks"] = new JsonObject
                        {
                            ["cancel"] = new JsonObject(),
                            ["requests"] = new JsonObject { ["tools"] = new JsonObject { ["call"] = new JsonObject() } },
                        },
                    },
                    ["serverInfo"] = new JsonObject { ["name"] = "fake-legacy", ["version"] = "1.0" },
                };
            case "tools/list":
            {
                var job = Tool("job");
                job["execution"] = new JsonObject { ["taskSupport"] = "required" };
                return new JsonObject { ["tools"] = new JsonArray(job, Tool("plain")) };
            }
            case "tools/call":
                if (p["task"] is null) return Content("ran without a task");
                return new JsonObject { ["task"] = Legacy(Create()) };
            case "tasks/get":
            {
                var task = Find(p);
                Polls.AddOrUpdate(task["taskId"]!.GetValue<string>(), 1, (_, n) => n + 1);
                return Legacy(task);
            }
            case "tasks/result":
            {
                var task = Find(p);
                var outcome = _outcomes[task["taskId"]!.GetValue<string>()];
                if (outcome["error"] is JsonObject e)
                    throw new McpProtocolException(e["message"]!.GetValue<string>(), (McpErrorCode)e["code"]!.GetValue<int>());
                return outcome.DeepClone().AsObject();
            }
            case "tasks/cancel":
            {
                var task = Find(p);
                Transition(task["taskId"]!.GetValue<string>(), "cancelled");
                return Legacy(task);
            }
            default:
                throw new McpProtocolException($"Method not found: {method}", McpErrorCode.MethodNotFound);
        }
    }

    /// <summary>
    /// <c>tasks/result</c> of a task with a question: ask it on the
    /// response stream, tagged with the task, wait for the client's answer,
    /// complete the task with it, and only then answer <c>tasks/result</c>.
    /// </summary>
    private async Task AskAsync(HttpContext http, JsonNode id, string taskId, string question)
    {
        var askId = "ask-" + taskId;
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _answers[askId] = answer;
        var elicit = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = askId,
            ["method"] = "elicitation/create",
            ["params"] = new JsonObject
            {
                ["message"] = question,
                ["requestedSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string" } },
                    ["required"] = new JsonArray("name"),
                },
                ["_meta"] = new JsonObject { ["io.modelcontextprotocol/related-task"] = new JsonObject { ["taskId"] = taskId } },
            },
        };

        http.Response.ContentType = "text/event-stream";
        await Send(http, elicit);
        var reply = await answer.Task.WaitAsync(http.RequestAborted);
        _answers.TryRemove(askId, out _);
        var result = reply["result"]?.AsObject() ?? [];
        Elicitations.Enqueue((elicit, result.DeepClone().AsObject()));

        var accepted = result["action"]?.GetValue<string>() == "accept";
        var outcome = Content(accepted ? "hello " + result["content"]?["name"]?.GetValue<string>() : "declined");
        Transition(taskId, "completed", result: outcome);
        await Send(http, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = outcome.DeepClone() });
    }

    private async Task ListenAsync(HttpContext http, JsonObject p)
    {
        var wanted = (p["notifications"]?["taskIds"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? [];
        var feed = Channel.CreateUnbounded<JsonObject>();
        lock (_gate) _listeners.Add(feed);

        http.Response.ContentType = "text/event-stream";
        await Send(http, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/subscriptions/acknowledged",
            ["params"] = new JsonObject
            {
                ["notifications"] = new JsonObject
                {
                    ["taskIds"] = new JsonArray([.. (PushStatus ? wanted : []).Select(w => (JsonNode)w)]),
                },
            },
        });
        _listening.TrySetResult();
        try
        {
            await foreach (var note in feed.Reader.ReadAllAsync(http.RequestAborted))
            {
                if (wanted.Contains(note["taskId"]!.GetValue<string>()) && PushStatus)
                    await Send(http, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/tasks", ["params"] = note });
            }
        }
        catch (OperationCanceledException) { /* the client stopped listening */ }
        finally
        {
            lock (_gate) _listeners.Remove(feed);
        }
    }

    private static async Task Send(HttpContext http, JsonObject message)
    {
        await http.Response.WriteAsync("event: message\ndata: " + message.ToJsonString() + "\n\n");
        await http.Response.Body.FlushAsync();
    }

    private void Push(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return;
        var note = Detailed(task);
        lock (_gate)
        {
            foreach (var listener in _listeners) listener.Writer.TryWrite(note.DeepClone().AsObject());
        }
    }

    private JsonObject Create()
    {
        var taskId = "task-" + Interlocked.Increment(ref _next).ToString("D3", CultureInfo.InvariantCulture);
        var now = Now();
        var task = new JsonObject
        {
            ["taskId"] = taskId,
            ["status"] = "working",
            ["createdAt"] = now,
            ["lastUpdatedAt"] = now,
            ["ttlMs"] = TtlMs,
            ["pollIntervalMs"] = PollIntervalMs,
        };
        _tasks[taskId] = task;
        return task;
    }

    private JsonObject Find(JsonObject p)
    {
        var taskId = p["taskId"]?.GetValue<string>() ?? throw new McpProtocolException("taskId is required", McpErrorCode.InvalidParams);
        return _tasks.TryGetValue(taskId, out var task)
            ? task
            : throw new McpProtocolException("Failed to retrieve task: Task has expired", McpErrorCode.InvalidParams);
    }

    private JsonObject Detailed(JsonObject task)
    {
        var detailed = task.DeepClone().AsObject();
        var taskId = task["taskId"]!.GetValue<string>();
        if (task["status"]!.GetValue<string>() is "completed" or "failed" && _outcomes.TryGetValue(taskId, out var outcome))
        {
            if (outcome["error"] is JsonObject e) detailed["error"] = e.DeepClone();
            else detailed["result"] = outcome.DeepClone();
        }
        return detailed;
    }

    private static JsonObject Legacy(JsonObject task) => new()
    {
        ["taskId"] = task["taskId"]!.DeepClone(),
        ["status"] = task["status"]!.DeepClone(),
        ["statusMessage"] = task["statusMessage"]?.DeepClone(),
        ["createdAt"] = task["createdAt"]!.DeepClone(),
        ["lastUpdatedAt"] = task["lastUpdatedAt"]!.DeepClone(),
        ["ttl"] = task["ttlMs"]?.DeepClone(),
        ["pollInterval"] = task["pollIntervalMs"]!.DeepClone(),
    };

    private static JsonObject Tool(string name) => new()
    {
        ["name"] = name,
        ["description"] = name + " tool",
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
    };

    private static JsonObject Content(string text) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
    };

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            foreach (var listener in _listeners) listener.Writer.TryComplete();
        }
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

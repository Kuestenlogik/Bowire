// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.Models;
using ModelContextProtocol.Client;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tasks;

/// <summary>
/// The MCP plugin's task surface (#801): which dialect a server speaks,
/// the <c>Tasks</c> service it adds to discovery, and the four methods on
/// it — <c>get</c>, <c>follow</c>, <c>update</c>, <c>cancel</c> — each
/// addressed by <c>taskId</c>. They are ordinary Bowire methods, so the
/// CLI, REST and the MCP adapter reach a task the same way the workbench
/// does, and a reload resumes with <c>follow</c>.
/// </summary>
internal sealed class McpTaskService
{
    public const string ServiceName = "Tasks";
    public const string Get = "get";
    public const string Follow = "follow";
    public const string Update = "update";
    public const string Cancel = "cancel";

    // 2025-11-25 servers: execution.taskSupport per tool, keyed by
    // normalised server URL, captured on discovery so a call does not need
    // a second tools/list. Same lifetime argument as the tool cache.
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _legacySupport =
        new(StringComparer.Ordinal);

    // 2025-11-25 servers: the questions of followed tasks that wait for an
    // answer in this process (#821).
    private readonly McpLegacyTaskInputs _legacyInputs = new();

    /// <summary>
    /// The dialect for a <c>Tasks/*</c> call: the extension when the server
    /// advertises it, the 2025-11-25 shape when that revision was
    /// negotiated, otherwise none. <paramref name="connect"/> opens another
    /// connection to the same server; a 2025-11-25 follow receives the
    /// task's questions on one.
    /// </summary>
    public IMcpTaskDialect? ForServer(
        McpClient client, string serverUrl, Func<McpClientOptions, CancellationToken, Task<McpClient>>? connect = null) =>
        McpExtensionTaskDialect.IsAdvertisedBy(client) ? McpExtensionTaskDialect.Instance
        : McpLegacyTaskDialect.IsNegotiatedBy(client) ? new McpLegacyTaskDialect(_legacyInputs, serverUrl, connect)
        : null;

    /// <summary>
    /// The dialect for calling <paramref name="toolName"/>, or
    /// <see langword="null"/> to call it the ordinary way. Under the
    /// extension the server decides per call, so every tool goes through it.
    /// Under 2025-11-25 the tool decides: <c>optional</c> and
    /// <c>required</c> run as tasks — Bowire is the place to see one run.
    /// </summary>
    public async Task<IMcpTaskDialect?> ForToolAsync(
        McpClient client, string serverUrl, string toolName, CancellationToken ct,
        Func<McpClientOptions, CancellationToken, Task<McpClient>>? connect = null)
    {
        if (McpExtensionTaskDialect.IsAdvertisedBy(client)) return McpExtensionTaskDialect.Instance;
        if (!McpLegacyTaskDialect.IsNegotiatedBy(client)) return null;

        if (!_legacySupport.TryGetValue(serverUrl, out var support) || !support.ContainsKey(toolName))
        {
            support = await McpLegacyTaskDialect.ReadTaskSupportAsync(client, ct).ConfigureAwait(false);
            _legacySupport[serverUrl] = support;
        }
        return support.ContainsKey(toolName) ? new McpLegacyTaskDialect(_legacyInputs, serverUrl, connect) : null;
    }

    /// <summary>
    /// Discovery's half: whether this server runs tasks, and if so the
    /// <c>Tasks</c> service plus a note per tool. Returns the shape, or
    /// <see langword="null"/> when the server has no tasks.
    /// </summary>
    public async Task<string?> DescribeAsync(
        McpClient client, string serverUrl, List<BowireMethodInfo> toolMethods, List<BowireServiceInfo> services, CancellationToken ct)
    {
        string shape;
        if (McpExtensionTaskDialect.IsAdvertisedBy(client))
        {
            shape = McpTaskShapes.Extension;
        }
        else if (McpLegacyTaskDialect.IsNegotiatedBy(client))
        {
            var support = await McpLegacyTaskDialect.ReadTaskSupportAsync(client, ct).ConfigureAwait(false);
            _legacySupport[serverUrl] = support;
            if (support.Count == 0) return null;
            for (var i = 0; i < toolMethods.Count; i++)
            {
                var method = toolMethods[i];
                if (support.TryGetValue(method.Name, out var mode))
                {
                    var note = $"Runs as an MCP task (taskSupport: {mode}, MCP 2025-11-25).";
                    toolMethods[i] = method with
                    {
                        Description = string.IsNullOrEmpty(method.Description) ? note : method.Description + "\n\n" + note,
                    };
                }
            }
            shape = McpTaskShapes.Legacy;
        }
        else
        {
            return null;
        }

        services.Add(new BowireServiceInfo(ServiceName, "mcp", BuildMethods())
        {
            Source = "mcp",
            OriginUrl = serverUrl,
            Description = shape == McpTaskShapes.Extension
                ? "MCP tasks (io.modelcontextprotocol/tasks, 2026-07-28) — this server may answer any tool call with a durable task. Follow, answer and cancel one by its taskId."
                : "MCP tasks (2025-11-25) — the tools marked as such run as durable tasks. Follow, answer and cancel one by its taskId; a task's questions are answered while Bowire follows it.",
        });
        return shape;
    }

    private static List<BowireMethodInfo> BuildMethods()
    {
        static BowireFieldInfo Field(string name, int number, string type, bool required, string description) =>
            new(name, number, type, required ? "required" : "optional", IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null)
            {
                Required = required,
                Description = description,
                Source = "body",
            };

        var taskId = Field("taskId", 1, "string", true, "The id the server returned when it created the task.");
        BowireMethodInfo Method(string name, bool streaming, string summary, params BowireFieldInfo[] fields) =>
            new(name, ServiceName + "/" + name, ClientStreaming: false, ServerStreaming: streaming,
                InputType: new BowireMessageInfo("Task" + char.ToUpperInvariant(name[0]) + name[1..] + "Request", "mcp.Task" + name, [.. fields]),
                OutputType: new BowireMessageInfo("Task", "mcp.Task", []),
                MethodType: streaming ? "ServerStreaming" : "Unary")
            {
                Summary = summary,
                Description = summary,
            };

        return
        [
            Method(Get, false, "tasks/get — the task's current status, with its result or error once it ended.", taskId),
            Method(Follow, true, "Follow a task until it ends: status as it changes, then the result. Resumes a task started earlier, e.g. after a reload.", taskId),
            Method(Update, false, "tasks/update — answer the task's inputRequests. Keys of inputResponses match the keys of inputRequests.",
                taskId,
                Field("inputResponses", 2, "object", true, "One response per outstanding input request, keyed like inputRequests — for an elicitation: { \"action\": \"accept\", \"content\": { … } }.")),
            Method(Cancel, false, "tasks/cancel — ask the server to stop. Cooperative: the task may still complete or fail.", taskId),
        ];
    }

    /// <summary>A unary <c>Tasks/*</c> call; the payload the response pane shows.</summary>
    public async Task<JsonElement> InvokeAsync(McpClient client, string serverUrl, string method, List<string> jsonMessages, CancellationToken ct)
    {
        var dialect = ForServer(client, serverUrl) ?? throw NoTasks();
        var (taskId, body) = ReadRequest(jsonMessages);

        switch (method)
        {
            case Get:
            {
                var state = await dialect.GetAsync(client, taskId, ct).ConfigureAwait(false);
                return WithOutcome(state);
            }
            case Update:
            {
                if (!body.TryGetProperty("inputResponses", out var responses) || responses.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("Tasks/update needs inputResponses: an object keyed like the task's inputRequests.");
                await dialect.UpdateAsync(client, taskId, responses, ct).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(new
                {
                    acknowledged = true,
                    taskId,
                    note = "The responses were delivered. The task goes on in its own time: follow it to see it continue.",
                });
            }
            case Cancel:
            {
                await dialect.CancelAsync(client, taskId, ct).ConfigureAwait(false);
                // Cancellation is cooperative: show where the task stands
                // now, which need not be `cancelled` yet — or ever.
                JsonElement? after = null;
                try { after = WithOutcome(await dialect.GetAsync(client, taskId, ct).ConfigureAwait(false)); }
                catch (ModelContextProtocol.McpException) { /* the server dropped it on cancel, which it may */ }
                return JsonSerializer.SerializeToElement(new
                {
                    acknowledged = true,
                    taskId,
                    note = "Cancellation is cooperative: the server was asked to stop, and the task may still complete or fail. Its status below is the server's answer right after the request.",
                    task = after,
                });
            }
            case Follow:
                throw new InvalidOperationException("Tasks/follow is a stream; invoke it as one.");
            default:
                throw new InvalidOperationException($"MCP Tasks has no method '{method}'. Use get, follow, update or cancel.");
        }
    }

    /// <summary><c>Tasks/follow</c>: the task's events from now until it ends.</summary>
    public async IAsyncEnumerable<McpTaskEvent> FollowAsync(
        McpClient client, string serverUrl, Func<McpClientOptions, CancellationToken, Task<McpClient>> connect,
        List<string> jsonMessages, [EnumeratorCancellation] CancellationToken ct)
    {
        var dialect = ForServer(client, serverUrl, connect) ?? throw NoTasks();
        var (taskId, _) = ReadRequest(jsonMessages);
        await foreach (var step in McpTaskFollower.FollowAsync(client, dialect, taskId, created: null, ct: ct).ConfigureAwait(false))
            yield return step;
    }

    private static JsonElement WithOutcome(McpTaskState state)
    {
        var node = JsonNode.Parse(state.ToFrameData("poll").GetRawText())!.AsObject();
        if (state.Result is { } result) node["result"] = JsonNode.Parse(result.GetRawText());
        if (state.Error is { } error) node["error"] = JsonNode.Parse(error.GetRawText());
        return JsonSerializer.SerializeToElement(node);
    }

    private static (string TaskId, JsonElement Body) ReadRequest(List<string> jsonMessages)
    {
        JsonElement body = default;
        if (jsonMessages.Count > 0 && !string.IsNullOrWhiteSpace(jsonMessages[0]))
        {
            using var doc = JsonDocument.Parse(jsonMessages[0]);
            body = doc.RootElement.Clone();
        }
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("taskId", out var id)
            || id.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(id.GetString()))
        {
            throw new ArgumentException("A Tasks call needs { \"taskId\": \"…\" } — the id the server returned when it created the task.");
        }
        return (id.GetString()!, body);
    }

    private static InvalidOperationException NoTasks() =>
        new("This MCP server does not advertise tasks: neither the io.modelcontextprotocol/tasks extension nor the 2025-11-25 task capability.");
}

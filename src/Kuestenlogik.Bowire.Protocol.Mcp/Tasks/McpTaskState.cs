// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tasks;

/// <summary>
/// One observation of an MCP task, in the vocabulary both spec shapes share
/// (#801). Status values are the wire strings — <c>working</c>,
/// <c>input_required</c>, <c>completed</c>, <c>failed</c>,
/// <c>cancelled</c> — so what the workbench shows is what the server said.
/// </summary>
/// <param name="Shape">Which spec the server used: <see cref="McpTaskShapes.Extension"/> or <see cref="McpTaskShapes.Legacy"/>.</param>
/// <param name="TaskId">The server-generated id; the handle for every later call.</param>
/// <param name="Status">The wire status.</param>
/// <param name="StatusMessage">The server's human-readable note on the status, if any.</param>
/// <param name="CreatedAt">When the server created the task.</param>
/// <param name="LastUpdatedAt">When the server last changed it.</param>
/// <param name="TtlMs">Lifetime from creation in milliseconds; <see langword="null"/> is unlimited.</param>
/// <param name="PollIntervalMs">The polling interval the server asks for.</param>
internal sealed record McpTaskState(
    string Shape,
    string TaskId,
    string Status,
    string? StatusMessage,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastUpdatedAt,
    long? TtlMs,
    long? PollIntervalMs)
{
    /// <summary>The outstanding server-to-client requests, keyed as the server keyed them.</summary>
    public JsonElement? InputRequests { get; init; }

    /// <summary>The underlying request's result, once the task is <c>completed</c>.</summary>
    public JsonElement? Result { get; init; }

    /// <summary>The JSON-RPC error, once the task is <c>failed</c>.</summary>
    public JsonElement? Error { get; init; }

    public bool IsTerminal => Status is McpTaskStatuses.Completed or McpTaskStatuses.Failed or McpTaskStatuses.Cancelled;

    /// <summary>
    /// When the server may discard the task, if it gave a TTL. The spec
    /// lets a client treat this as a backstop: a task still not terminal
    /// past it may be considered gone.
    /// </summary>
    /// <remarks>
    /// #868 — a TTL too large to add to the creation time is, for every purpose a
    /// client has, unlimited — the server picked the number, and
    /// <see cref="DateTimeOffset.AddMilliseconds"/> would throw on it.
    /// </remarks>
    public DateTimeOffset? ExpiresAt =>
        CreatedAt is { } created && TtlMs is { } ttl && ttl >= 0
        && ttl <= (DateTimeOffset.MaxValue - created).TotalMilliseconds
            ? created.AddMilliseconds(ttl)
            : null;

    /// <summary>
    /// Whether <paramref name="other"/> says something this observation did
    /// not — a poll that returns the same state is not worth a frame.
    /// </summary>
    public bool SameAs(McpTaskState other) =>
        Status == other.Status
        && StatusMessage == other.StatusMessage
        && TtlMs == other.TtlMs
        && PollIntervalMs == other.PollIntervalMs
        && LastUpdatedAt == other.LastUpdatedAt
        && Raw(InputRequests) == Raw(other.InputRequests);

    /// <summary>
    /// The <c>task</c> frame's payload. <paramref name="via"/> says how
    /// Bowire learned of this state: <c>create</c>, <c>poll</c> or
    /// <c>notification</c>. Result and error are not repeated here; they
    /// travel as the stream's <c>result</c> / <c>error</c> frame.
    /// </summary>
    public JsonElement ToFrameData(string via)
    {
        var node = new JsonObject
        {
            ["shape"] = Shape,
            ["taskId"] = TaskId,
            ["status"] = Status,
            ["via"] = via,
        };
        if (StatusMessage is not null) node["statusMessage"] = StatusMessage;
        if (CreatedAt is { } created) node["createdAt"] = created.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        if (LastUpdatedAt is { } updated) node["lastUpdatedAt"] = updated.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        // null is meaningful for the TTL: the server said "unlimited".
        node["ttlMs"] = TtlMs;
        if (PollIntervalMs is { } poll) node["pollIntervalMs"] = poll;
        if (InputRequests is { } input) node["inputRequests"] = JsonNode.Parse(input.GetRawText());
        return JsonSerializer.SerializeToElement(node);
    }

    private static string? Raw(JsonElement? element) => element?.GetRawText();
}

/// <summary>The two task shapes Bowire speaks, named by the spec revision that defines them.</summary>
internal static class McpTaskShapes
{
    /// <summary>The <c>io.modelcontextprotocol/tasks</c> extension, stable schema 2026-07-28.</summary>
    public const string Extension = "2026-07-28";

    /// <summary>Tasks as the experimental part of the 2025-11-25 core spec.</summary>
    public const string Legacy = "2025-11-25";
}

internal static class McpTaskStatuses
{
    public const string Working = "working";
    public const string InputRequired = "input_required";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tasks;

/// <summary>
/// Reads a task object off the wire in either shape (#801). The two specs
/// name the same fields differently — <c>ttlMs</c> / <c>pollIntervalMs</c>
/// in the extension, <c>ttl</c> / <c>pollInterval</c> in 2025-11-25 — and
/// the extension inlines <c>result</c>, <c>error</c> and
/// <c>inputRequests</c> where the older shape keeps them behind
/// <c>tasks/result</c>.
/// </summary>
internal static class McpTaskJson
{
    public static McpTaskState Parse(string shape, JsonElement task)
    {
        if (task.ValueKind != JsonValueKind.Object)
            throw new JsonException("A task must be a JSON object.");

        var taskId = String(task, "taskId")
            ?? throw new JsonException("The task carries no taskId.");
        var status = String(task, "status")
            ?? throw new JsonException($"Task '{taskId}' carries no status.");

        return new McpTaskState(
            shape,
            taskId,
            status,
            String(task, "statusMessage"),
            Timestamp(task, "createdAt"),
            Timestamp(task, "lastUpdatedAt"),
            Number(task, "ttlMs") ?? Number(task, "ttl"),
            Number(task, "pollIntervalMs") ?? Number(task, "pollInterval"))
        {
            InputRequests = Object(task, "inputRequests"),
            Result = Object(task, "result"),
            Error = Object(task, "error"),
        };
    }

    private static string? String(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;

    private static DateTimeOffset? Timestamp(JsonElement obj, string name) =>
        String(obj, name) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : null;

    private static JsonElement? Object(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value.Clone() : null;
}

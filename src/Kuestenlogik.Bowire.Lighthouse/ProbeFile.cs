// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Lighthouse;

/// <summary>
/// Loads a probe definition file — a saved recording wrapped with the three
/// Lighthouse extras (schedule, assertions, severity):
/// <code>
/// {
///   "name": "payments-health",
///   "schedule": "every 60s, 09:00-17:00 UTC, Mon-Fri",
///   "severity": "crit",
///   "assertions": [
///     { "kind": "status", "expected": "200" },
///     { "kind": "latencyBelowMs", "expected": "500" },
///     { "kind": "bodyContains", "expected": "healthy" }
///   ],
///   "recording": { "id": "...", "name": "...", "steps": [ ... ] }
/// }
/// </code>
/// A malformed file throws <see cref="ProbeFileException"/> with a clear reason
/// (the CLI reports it and skips that probe — visible, non-silent).
/// </summary>
public static class ProbeFile
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Read + parse a probe file from disk.</summary>
    public static Probe Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProbeFileException($"Couldn't read probe file '{path}': {ex.Message}", ex);
        }
        return Parse(text);
    }

    /// <summary>Parse a probe definition from a JSON string.</summary>
    public static Probe Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        ProbeFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ProbeFileDto>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new ProbeFileException($"Probe file isn't valid JSON: {ex.Message}", ex);
        }

        if (dto is null)
        {
            throw new ProbeFileException("Probe file is empty.");
        }
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new ProbeFileException("Probe file is missing a 'name'.");
        }
        if (!ProbeSchedule.TryParse(dto.Schedule, out var schedule, out var scheduleError))
        {
            throw new ProbeFileException($"Probe '{dto.Name}' has an invalid schedule: {scheduleError}");
        }
        if (dto.Recording is null)
        {
            throw new ProbeFileException($"Probe '{dto.Name}' is missing a 'recording'.");
        }

        BowireRecording recording;
        try
        {
            recording = JsonSerializer.Deserialize<BowireRecording>(dto.Recording.Value.GetRawText(), Json)
                ?? throw new ProbeFileException($"Probe '{dto.Name}' has an unreadable recording.");
        }
        catch (JsonException ex)
        {
            throw new ProbeFileException($"Probe '{dto.Name}' has an unreadable recording: {ex.Message}", ex);
        }

        return new Probe
        {
            Name = dto.Name,
            Schedule = schedule!,
            Severity = ParseSeverity(dto.Severity),
            Recording = recording,
            Assertions = (dto.Assertions ?? []).Select(ToAssertion).ToArray(),
        };
    }

    private static ProbeSeverity ParseSeverity(string? s) => s?.Trim().ToUpperInvariant() switch
    {
        "INFO" => ProbeSeverity.Info,
        "CRIT" or "CRITICAL" => ProbeSeverity.Crit,
        _ => ProbeSeverity.Warn,
    };

    private static ProbeAssertion ToAssertion(ProbeAssertionDto dto)
    {
        var kind = dto.Kind?.Trim().ToUpperInvariant() switch
        {
            "STATUS" => ProbeAssertionKind.Status,
            "LATENCY" or "LATENCYBELOWMS" => ProbeAssertionKind.LatencyBelowMs,
            "BODY" or "BODYCONTAINS" => ProbeAssertionKind.BodyContains,
            _ => throw new ProbeFileException($"Unknown assertion kind '{dto.Kind}'."),
        };
        if (string.IsNullOrEmpty(dto.Expected))
        {
            throw new ProbeFileException($"Assertion '{dto.Kind}' is missing an 'expected' value.");
        }
        return new ProbeAssertion { Kind = kind, Expected = dto.Expected };
    }

    private sealed record ProbeFileDto
    {
        public string? Name { get; init; }
        public string? Schedule { get; init; }
        public string? Severity { get; init; }
        public List<ProbeAssertionDto>? Assertions { get; init; }
        public JsonElement? Recording { get; init; }
    }

    private sealed record ProbeAssertionDto
    {
        public string? Kind { get; init; }
        public string? Expected { get; init; }
    }
}

/// <summary>Thrown when a probe file can't be read or parsed.</summary>
public sealed class ProbeFileException : Exception
{
    public ProbeFileException(string message) : base(message) { }
    public ProbeFileException(string message, Exception innerException) : base(message, innerException) { }
    public ProbeFileException() { }
}

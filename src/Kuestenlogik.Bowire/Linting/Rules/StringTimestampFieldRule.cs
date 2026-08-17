// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Linting.Rules;

/// <summary>
/// Flags a field that names a point in time (<c>created_at</c>, <c>updatedAt</c>,
/// <c>timestamp</c>, <c>*_time</c>) but is typed as a bare string. A string time
/// value loses its shape: it can't be validated, compared or formatted without
/// out-of-band knowledge. Prefer a typed timestamp / date. Low severity — a
/// modelling nit, not a defect.
/// </summary>
public sealed partial class StringTimestampFieldRule : IBowireLintRule
{
    public string Id => "BWR-LINT-STRING-TIMESTAMP";

    public string Title => "Time field typed as string";

    public BowireLintSeverity Severity => BowireLintSeverity.Low;

    // Explicit time signals compared against the name with separators removed.
    // Deliberately NOT a bare "time" (it would false-positive on timezone /
    // timeout / uptime); the `*_time` / `*Time` case is a suffix match below.
    private static readonly string[] TimeNameTokens =
    [
        "timestamp", "createdat", "updatedat", "deletedat",
    ];

    private const int MaxDepth = 3;

    public IEnumerable<BowireLintFinding> Inspect(BowireServiceInfo service)
    {
        foreach (var method in service.Methods ?? [])
        {
            foreach (var field in StringTimeFields(method.InputType, 0))
                yield return Finding(service, method, field);

            foreach (var field in StringTimeFields(method.OutputType, 0))
                yield return Finding(service, method, field);
        }
    }

    private BowireLintFinding Finding(BowireServiceInfo service, BowireMethodInfo method, BowireFieldInfo field)
        => new(
            Id, Severity, service.Name, method.Name, field.Name,
            $"Field '{field.Name}' looks like a time value but is typed as '{field.Type}'. Prefer a typed timestamp/date over a string.");

    private static IEnumerable<BowireFieldInfo> StringTimeFields(BowireMessageInfo? message, int depth)
    {
        if (message is null || depth > MaxDepth) yield break;

        foreach (var field in message.Fields ?? [])
        {
            if (IsStringType(field.Type) && LooksLikeTime(field.Name))
                yield return field;

            if (field.MessageType is not null)
            {
                foreach (var nested in StringTimeFields(field.MessageType, depth + 1))
                    yield return nested;
            }
        }
    }

    private static bool IsStringType(string? type)
        => string.Equals(type, "string", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "str", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeTime(string name)
    {
        // An explicit time word anywhere (separators removed): timestamp, time,
        // created_at / createdAt collapsed to createdat, ...
        var normalized = name.Replace("_", "", StringComparison.Ordinal)
                             .Replace("-", "", StringComparison.Ordinal);
        if (TimeNameTokens.Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase)))
            return true;

        // A `*_time` / `*Time` field (start_time, endTime). Suffix, not
        // substring, so a leading "time" (timezone, timeout) is not a match.
        if (normalized.EndsWith("time", StringComparison.OrdinalIgnoreCase))
            return true;

        // A generic `_at` / `At` suffix segment — but NOT a word that merely
        // ends in the letters "at" (format, stat): the "at" must sit behind a
        // separator or a camelCase hump.
        return TimeSuffix().IsMatch(name);
    }

    [GeneratedRegex(@"(?:[_-][aA][tT]|[a-z0-9]At)$")]
    private static partial Regex TimeSuffix();
}

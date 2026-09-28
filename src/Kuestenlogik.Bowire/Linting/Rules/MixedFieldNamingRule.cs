// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Linting.Rules;

/// <summary>
/// Flags a field whose name follows a different convention than the other
/// fields of its service — a payload with <c>created_at</c> next to
/// <c>updatedAt</c>. Measured across the whole service, request and response
/// alike, because a client writes one mapping for all of it (#583).
/// </summary>
/// <remarks>
/// HTTP headers and cookies do not vote. They are kebab-case by convention of
/// the transport, not of the API, and a body in camelCase next to an
/// <c>x-api-key</c> header is exactly how it should look.
/// </remarks>
public sealed class MixedFieldNamingRule : IBowireLintRule
{
    public string Id => "BWR-LINT-MIXED-FIELD-NAMING";
    public string Title => "Field names mix conventions";
    public BowireLintSeverity Severity => BowireLintSeverity.Info;

    private const int MaxDepth = 3;

    public IEnumerable<BowireLintFinding> Inspect(BowireServiceInfo service)
    {
        // First occurrence of every name, with where it was seen, so a field
        // shared by ten messages is one vote and one finding.
        var seen = new Dictionary<string, BowireMethodInfo>(StringComparer.Ordinal);
        foreach (var method in service.Methods ?? [])
        {
            foreach (var name in Names(method.InputType, 0).Concat(Names(method.OutputType, 0)))
                seen.TryAdd(name, method);
        }

        var tally = NamingConvention.Count(seen.Keys);
        if (!tally.Mixed) yield break;

        if (tally.Tied)
        {
            yield return new BowireLintFinding(
                Id, Severity, service.Name, null, null,
                $"Field names mix conventions with no majority ({tally.Describe()}). Pick one for the service.");
            yield break;
        }

        foreach (var (name, method) in seen)
        {
            var style = NamingConvention.Classify(name);
            if (style == NamingStyle.None || style == tally.Majority) continue;
            yield return new BowireLintFinding(
                Id, Severity, service.Name, method.Name, name,
                $"Field '{name}' is {NamingConvention.Label(style)}; the other fields of this service are "
                + $"{NamingConvention.Label(tally.Majority)} ({tally.Describe()}).");
        }
    }

    private static IEnumerable<string> Names(BowireMessageInfo? message, int depth)
    {
        if (message is null || depth > MaxDepth) yield break;
        foreach (var field in message.Fields ?? [])
        {
            if (IsTransportName(field.Source)) continue;
            yield return field.Name;
            foreach (var nested in Names(field.MessageType, depth + 1))
                yield return nested;
        }
    }

    private static bool IsTransportName(string? source)
        => string.Equals(source, "header", StringComparison.OrdinalIgnoreCase)
        || string.Equals(source, "cookie", StringComparison.OrdinalIgnoreCase);
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Linting.Rules;

/// <summary>
/// Flags a method that returns a list (a repeated field in the response) but
/// accepts no pagination parameter. An unbounded list response is a scaling
/// and denial-of-service risk: it grows with the data set and there is no way
/// for a caller to ask for a page.
/// </summary>
public sealed class MissingPaginationRule : IBowireLintRule
{
    public string Id => "BWR-LINT-MISSING-PAGINATION";

    public string Title => "List response without pagination";

    public BowireLintSeverity Severity => BowireLintSeverity.Medium;

    // Names (separators removed) that signal a paging control on the request.
    private static readonly string[] PaginationTokens =
    [
        "page", "limit", "offset", "cursor", "pagesize", "perpage",
        "pagetoken", "top", "skip", "first", "after",
    ];

    public IEnumerable<BowireLintFinding> Inspect(BowireServiceInfo service)
    {
        foreach (var method in service.Methods ?? [])
        {
            // A server stream is already incremental — pagination doesn't apply.
            if (method.ServerStreaming) continue;

            var returnsList = (method.OutputType?.Fields ?? []).Any(f => f.IsRepeated);
            if (!returnsList) continue;

            var hasPagination = (method.InputType?.Fields ?? []).Any(f => IsPaginationField(f.Name));
            if (hasPagination) continue;

            yield return new BowireLintFinding(
                Id, Severity, service.Name, method.Name, null,
                $"Method '{method.Name}' returns a list but takes no pagination parameter (page / limit / offset / cursor). Unbounded list responses are a scaling and denial-of-service risk.");
        }
    }

    private static bool IsPaginationField(string name)
    {
        var normalized = name.Replace("_", "", StringComparison.Ordinal)
                             .Replace("-", "", StringComparison.Ordinal);
        return PaginationTokens.Any(token =>
            normalized.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}

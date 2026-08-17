// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Linting.Rules;

/// <summary>
/// Flags a service that exposes no version at all — neither a declared
/// <see cref="BowireServiceInfo.Version"/> nor a version marker on any route
/// (<c>/v1/</c>, <c>.v2.</c>, <c>_v3</c>). An unversioned API cannot evolve
/// without breaking its consumers.
/// </summary>
public sealed partial class MissingVersioningRule : IBowireLintRule
{
    public string Id => "BWR-LINT-MISSING-VERSIONING";

    public string Title => "Service exposes no API version";

    public BowireLintSeverity Severity => BowireLintSeverity.Low;

    public IEnumerable<BowireLintFinding> Inspect(BowireServiceInfo service)
    {
        if (!string.IsNullOrWhiteSpace(service.Version)) yield break;

        var versioned = (service.Methods ?? [])
            .Any(m => HasVersionMarker(m.HttpPath) || HasVersionMarker(m.FullName));
        if (versioned) yield break;

        yield return new BowireLintFinding(
            Id, Severity, service.Name, null, null,
            $"Service '{service.Name}' declares no version and no route carries a version marker (e.g. /v1/). An unversioned API can't evolve without breaking consumers.");
    }

    private static bool HasVersionMarker(string? value)
        => value is not null && VersionMarker().IsMatch(value);

    [GeneratedRegex(@"(^|[/._])v\d+([/._]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionMarker();
}

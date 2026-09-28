// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Linting.Rules;

/// <summary>
/// Flags a method whose name follows a different convention than the rest of
/// its service — <c>GetOrder</c>, <c>ListOrders</c>, <c>cancelOrder</c>.
/// Consistency, not a style: which convention is right is the service's own
/// majority, so gRPC's PascalCase and REST's camelCase are both fine as long
/// as a service sticks to one (#583). Info — a nit, until somebody generates
/// a client from it.
/// </summary>
public sealed class MixedMethodNamingRule : IBowireLintRule
{
    public string Id => "BWR-LINT-MIXED-METHOD-NAMING";
    public string Title => "Method names mix conventions";
    public BowireLintSeverity Severity => BowireLintSeverity.Info;

    public IEnumerable<BowireLintFinding> Inspect(BowireServiceInfo service)
    {
        var methods = service.Methods ?? [];
        var tally = NamingConvention.Count(methods.Select(m => m.Name));
        if (!tally.Mixed) yield break;

        if (tally.Tied)
        {
            // No majority to measure against: naming one side the outlier
            // would be a coin toss. Said once, for the service.
            yield return new BowireLintFinding(
                Id, Severity, service.Name, null, null,
                $"Method names mix conventions with no majority ({tally.Describe()}). Pick one for the service.");
            yield break;
        }

        foreach (var method in methods)
        {
            var style = NamingConvention.Classify(method.Name);
            if (style == NamingStyle.None || style == tally.Majority) continue;
            yield return new BowireLintFinding(
                Id, Severity, service.Name, method.Name, null,
                $"Method '{method.Name}' is {NamingConvention.Label(style)}; the rest of this service is "
                + $"{NamingConvention.Label(tally.Majority)} ({tally.Describe()}).");
        }
    }
}

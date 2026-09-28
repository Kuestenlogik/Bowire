// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Linting.Rules;

/// <summary>
/// Flags a declared error response that carries a field which looks like
/// personal data — a validation error that echoes the <c>email</c> it
/// rejected, a not-found that names the <c>phone</c> it looked up (#583).
/// </summary>
/// <remarks>
/// <para>
/// Errors travel further than responses. They are logged by every proxy and
/// gateway on the way, copied into tickets, pasted into chat, and returned to
/// callers who were not supposed to see the resource at all — a 404 that
/// repeats the looked-up address tells a stranger the address is on file.
/// Same list of PII names as <see cref="PiiResponseFieldRule"/>, same Medium.
/// </para>
/// <para>
/// It can only see errors a schema declares: REST from an OpenAPI document's
/// 4xx / 5xx / default responses. gRPC and GraphQL do not describe their
/// errors' shape in the schema, so against those this rule has nothing to
/// read and stays silent — which is a gap in what discovery knows, not a
/// clean bill of health.
/// </para>
/// </remarks>
public sealed class PiiErrorFieldRule : IBowireLintRule
{
    public string Id => "BWR-LINT-PII-IN-ERROR";
    public string Title => "Error response exposes a PII field";
    public BowireLintSeverity Severity => BowireLintSeverity.Medium;

    public IEnumerable<BowireLintFinding> Inspect(BowireServiceInfo service)
    {
        foreach (var method in service.Methods ?? [])
        {
            // A field shared by several error shapes (400 and 422 carrying the
            // same validation envelope) is one finding, not one per status.
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var error in method.ErrorTypes ?? [])
            {
                foreach (var field in PiiResponseFieldRule.PiiFields(error, 0))
                {
                    if (!reported.Add(field)) continue;
                    yield return new BowireLintFinding(
                        Id, Severity, service.Name, method.Name, field,
                        $"An error from '{method.Name}' carries a field named '{field}', which looks like personal data (PII). "
                        + "Errors are logged and forwarded far more widely than responses; name what went wrong without repeating the data.");
                }
            }
        }
    }
}

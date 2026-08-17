// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Linting.Rules;

/// <summary>
/// Flags a response that carries a field which looks like personal data (PII) —
/// an email, phone number, SSN, date of birth, address, passport or tax id.
/// Unlike <see cref="SensitiveResponseFieldRule"/> (secrets / credentials, a
/// likely defect at High), returning PII is a privacy design smell worth a
/// second look rather than an outright leak, so it carries Medium severity.
/// </summary>
public sealed class PiiResponseFieldRule : IBowireLintRule
{
    public string Id => "BWR-LINT-PII-RESPONSE";

    public string Title => "Response exposes a PII field";

    public BowireLintSeverity Severity => BowireLintSeverity.Medium;

    // Compared against the field name with separators removed, so both
    // `date_of_birth` and `dateOfBirth` match `dateofbirth`.
    private static readonly string[] PiiTokens =
    [
        "email", "phone", "ssn", "socialsecurity", "dateofbirth", "dob",
        "address", "passport", "taxid",
    ];

    private const int MaxDepth = 3;

    public IEnumerable<BowireLintFinding> Inspect(BowireServiceInfo service)
    {
        foreach (var method in service.Methods ?? [])
        {
            foreach (var field in PiiFields(method.OutputType, 0))
            {
                yield return new BowireLintFinding(
                    Id, Severity, service.Name, method.Name, field,
                    $"Method '{method.Name}' returns a field named '{field}', which looks like personal data (PII). Consider whether it belongs in this response.");
            }
        }
    }

    private static IEnumerable<string> PiiFields(BowireMessageInfo? message, int depth)
    {
        if (message is null || depth > MaxDepth) yield break;

        foreach (var field in message.Fields ?? [])
        {
            if (IsPii(field.Name)) yield return field.Name;

            if (field.MessageType is not null)
            {
                foreach (var nested in PiiFields(field.MessageType, depth + 1))
                    yield return nested;
            }
        }
    }

    private static bool IsPii(string name)
    {
        var normalized = name.Replace("_", "", StringComparison.Ordinal)
                             .Replace("-", "", StringComparison.Ordinal);
        return PiiTokens.Any(token =>
            normalized.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}

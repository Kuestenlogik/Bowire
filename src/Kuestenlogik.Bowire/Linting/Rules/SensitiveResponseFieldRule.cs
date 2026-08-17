// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Linting.Rules;

/// <summary>
/// Flags a response that carries a field which looks like a credential or
/// secret (password, api key, token, SSN, card number, ...). Returning secrets
/// is a classic data-exposure defect that a schema makes visible before a
/// single request is sent.
/// </summary>
public sealed class SensitiveResponseFieldRule : IBowireLintRule
{
    public string Id => "BWR-LINT-SENSITIVE-RESPONSE";

    public string Title => "Response exposes a sensitive field";

    public BowireLintSeverity Severity => BowireLintSeverity.High;

    // Compared against the field name with separators removed, so both
    // `api_key` and `apiKey` match `apikey`.
    private static readonly string[] SensitiveTokens =
    [
        "password", "passwd", "secret", "apikey", "token", "privatekey",
        "ssn", "creditcard", "cardnumber", "cvv", "cvc",
    ];

    private const int MaxDepth = 3;

    public IEnumerable<BowireLintFinding> Inspect(BowireServiceInfo service)
    {
        foreach (var method in service.Methods ?? [])
        {
            foreach (var field in SensitiveFields(method.OutputType, 0))
            {
                yield return new BowireLintFinding(
                    Id, Severity, service.Name, method.Name, field,
                    $"Method '{method.Name}' returns a field named '{field}', which looks like a secret. An API should never return credentials or secrets in a response.");
            }
        }
    }

    private static IEnumerable<string> SensitiveFields(BowireMessageInfo? message, int depth)
    {
        if (message is null || depth > MaxDepth) yield break;

        foreach (var field in message.Fields ?? [])
        {
            if (IsSensitive(field.Name)) yield return field.Name;

            if (field.MessageType is not null)
            {
                foreach (var nested in SensitiveFields(field.MessageType, depth + 1))
                    yield return nested;
            }
        }
    }

    private static bool IsSensitive(string name)
    {
        var normalized = name.Replace("_", "", StringComparison.Ordinal)
                             .Replace("-", "", StringComparison.Ordinal);
        return SensitiveTokens.Any(token =>
            normalized.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}

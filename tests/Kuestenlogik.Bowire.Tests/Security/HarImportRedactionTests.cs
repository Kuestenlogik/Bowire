// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// #186 HAR import hardening: secret redaction, credential-header detection,
/// and deterministic (idempotent) recording ids.
/// </summary>
public sealed class HarImportRedactionTests
{
    private const string HarWithSecrets = """
        {
          "log": {
            "creator": { "name": "DevTools" },
            "entries": [
              {
                "startedDateTime": "2026-04-01T10:00:00.000Z",
                "time": 10,
                "request": {
                  "method": "GET",
                  "url": "https://api.example.com/orders/42",
                  "headers": [
                    { "name": "Accept", "value": "application/json" },
                    { "name": "Authorization", "value": "Bearer eyJhbGciOi.secret.value" },
                    { "name": "Cookie", "value": "session=abc123; theme=dark" },
                    { "name": "X-Api-Key", "value": "sk_live_9999" }
                  ]
                },
                "response": {
                  "status": 200,
                  "headers": [ { "name": "Set-Cookie", "value": "session=renewed; HttpOnly" } ],
                  "content": { "text": "{\"id\":42}" }
                }
              }
            ]
          }
        }
        """;

    [Fact]
    public void Convert_RedactSecrets_StripsCredentialHeaderValues()
    {
        var rec = BowireHarConverter.Convert(HarWithSecrets, recordingName: null, redactSecrets: true);
        var step = Assert.Single(rec.Steps);

        Assert.Equal(BowireHarConverter.RedactedPlaceholder, step.Metadata!["Authorization"]);
        Assert.Equal(BowireHarConverter.RedactedPlaceholder, step.Metadata!["Cookie"]);
        Assert.Equal(BowireHarConverter.RedactedPlaceholder, step.Metadata!["X-Api-Key"]);
        Assert.Equal(BowireHarConverter.RedactedPlaceholder, step.ResponseHeaders!["Set-Cookie"]);
        // Non-sensitive headers untouched.
        Assert.Equal("application/json", step.Metadata!["Accept"]);
    }

    [Fact]
    public void Convert_WithoutRedaction_KeepsHeaderValues()
    {
        var rec = BowireHarConverter.Convert(HarWithSecrets, recordingName: null, redactSecrets: false);
        var step = Assert.Single(rec.Steps);

        Assert.StartsWith("Bearer ", step.Metadata!["Authorization"], StringComparison.Ordinal);
        Assert.Contains("session=abc123", step.Metadata!["Cookie"], StringComparison.Ordinal);
        // Default overload preserves the original (non-redacting) behaviour.
        var viaDefault = BowireHarConverter.Convert(HarWithSecrets);
        Assert.StartsWith("Bearer ", viaDefault.Steps[0].Metadata!["Authorization"], StringComparison.Ordinal);
    }

    [Fact]
    public void DetectAuthHeaders_ReturnsCanonicalSortedNames()
    {
        var headers = BowireHarConverter.DetectAuthHeaders(HarWithSecrets);
        Assert.Equal(["Authorization", "Cookie", "Set-Cookie", "X-Api-Key"], headers);
    }

    [Fact]
    public void DetectAuthHeaders_NoneOrMalformed_ReturnsEmpty()
    {
        Assert.Empty(BowireHarConverter.DetectAuthHeaders(
            """{ "log": { "entries": [ { "request": { "method": "GET", "url": "https://x/y", "headers": [ { "name": "Accept", "value": "*" } ] } } ] } }"""));
        Assert.Empty(BowireHarConverter.DetectAuthHeaders("not json"));
        Assert.Empty(BowireHarConverter.DetectAuthHeaders(""));
    }

    [Fact]
    public void Convert_SameHar_ProducesDeterministicId()
    {
        var a = BowireHarConverter.Convert(HarWithSecrets);
        var b = BowireHarConverter.Convert(HarWithSecrets);
        Assert.StartsWith("rec_har_", a.Id, StringComparison.Ordinal);
        Assert.Equal(a.Id, b.Id); // idempotent: re-import ⇒ same id (dedupe key)
    }

    [Fact]
    public void Convert_DifferentContentOrName_ProducesDifferentId()
    {
        var baseId = BowireHarConverter.Convert(HarWithSecrets).Id;
        var byName = BowireHarConverter.Convert(HarWithSecrets, recordingName: "Other").Id;
        var byContent = BowireHarConverter.Convert(
            HarWithSecrets.Replace("/orders/42", "/orders/99", StringComparison.Ordinal)).Id;

        Assert.NotEqual(baseId, byName);
        Assert.NotEqual(baseId, byContent);
    }
}

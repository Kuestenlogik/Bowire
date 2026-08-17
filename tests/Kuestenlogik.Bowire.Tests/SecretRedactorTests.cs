// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Xml;
using Kuestenlogik.Bowire.App;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// #361 — unit tests for the run-scoped <see cref="SecretRedactor"/> and its
/// application at the CI output sinks (JUnit, SARIF, GitHub annotations).
/// </summary>
public class SecretRedactorTests
{
    private const string LeakedToken = "leaked-token-c5f9";
    private static readonly string[] OneSecret = ["hunter2password"];
    private static readonly string[] Emptyish = ["", "   ", "\t"];
    private static readonly string[] SubstringPair = ["abc", "abcdef-token"];
    private static readonly string[] TwoSecrets = ["first-secret-aaaa", "second-secret-bbbb"];
    private static readonly string[] LeakedTokenSet = [LeakedToken];
    private static readonly string[] NamesToResolve = ["TOKEN", "EMPTY", "MISSING"];

    [Fact]
    public void Mask_LongValue_KeepsLastFourCharsBehindStars()
    {
        // Longer than eight characters → *** + last 4 so two masked values
        // stay distinguishable in a diff.
        Assert.Equal("***c5f9", SecretRedactor.Mask("super-secret-c5f9"));
    }

    [Fact]
    public void Mask_ShortValue_CollapsesToBareStars()
    {
        // Eight characters or fewer → no tail, or the tail would leak too
        // much of a short secret.
        Assert.Equal("***", SecretRedactor.Mask("12345678"));
        Assert.Equal("***", SecretRedactor.Mask("abc"));
    }

    [Fact]
    public void Redact_ReplacesEveryOccurrenceOfSecretValue()
    {
        var redactor = new SecretRedactor(OneSecret);
        const string text = "token=hunter2password sent; retry with hunter2password";
        var masked = redactor.Redact(text);
        Assert.DoesNotContain("hunter2password", masked, StringComparison.Ordinal);
        Assert.Equal("token=***word sent; retry with ***word", masked);
    }

    [Fact]
    public void Redact_EmptyOrWhitespaceSecret_IsIgnored()
    {
        // Never redact "" — replacing an empty string would blank everything.
        var redactor = new SecretRedactor(Emptyish);
        Assert.True(redactor.IsEmpty);
        Assert.Equal("nothing is masked here", redactor.Redact("nothing is masked here"));
    }

    [Fact]
    public void Redact_LongestValueFirst_AvoidsHalfMaskingASubstringSecret()
    {
        // "abc" is a substring of "abcdef-token". If the short one masked
        // first it would carve the longer secret into "***def-token".
        // Longest-first keeps the whole longer secret contiguous.
        var redactor = new SecretRedactor(SubstringPair);
        var masked = redactor.Redact("value=abcdef-token and abc");
        Assert.DoesNotContain("abcdef-token", masked, StringComparison.Ordinal);
        Assert.Equal("value=***oken and ***", masked);
    }

    [Fact]
    public void Redact_MultipleSecrets_AllMasked()
    {
        var redactor = new SecretRedactor(TwoSecrets);
        var masked = redactor.Redact("a=first-secret-aaaa b=second-secret-bbbb");
        Assert.Equal("a=***aaaa b=***bbbb", masked);
    }

    [Fact]
    public void Redact_NullAndEmpty_PassThrough()
    {
        var redactor = new SecretRedactor(LeakedTokenSet);
        Assert.Null(redactor.Redact(null));
        Assert.Equal(string.Empty, redactor.Redact(string.Empty));
    }

    [Fact]
    public void Empty_IsNoOp()
    {
        Assert.True(SecretRedactor.Empty.IsEmpty);
        Assert.Equal(0, SecretRedactor.Empty.Count);
        Assert.Equal("verbatim s3cr3t-looking text", SecretRedactor.Empty.Redact("verbatim s3cr3t-looking text"));
    }

    [Fact]
    public void FromNames_ResolvesValuesFromEnvAndSkipsMisses()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TOKEN"] = "resolved-token-9999",
            ["EMPTY"] = "",
        };
        var redactor = SecretRedactor.FromNames(NamesToResolve, env);
        Assert.Equal(1, redactor.Count); // only TOKEN registers ("" dropped, MISSING absent)
        Assert.Equal("v=***9999", redactor.Redact("v=resolved-token-9999"));
    }

    [Fact]
    public void JUnitRender_MasksSecretInFailureText()
    {
        var report = MakeFailingReport();
        var redactor = new SecretRedactor(LeakedTokenSet);
        var xml = JUnitReport.Render(report, redactor);

        // Well-formed, the raw secret is gone, the mask is present.
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        Assert.DoesNotContain(LeakedToken, xml, StringComparison.Ordinal);
        Assert.Contains("***c5f9", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void SarifRender_MasksSecretInResultMessage()
    {
        var report = MakeFailingReport();
        var redactor = new SecretRedactor(LeakedTokenSet);
        var sarif = TestSarifReport.Render(report, redactor);

        Assert.DoesNotContain(LeakedToken, sarif, StringComparison.Ordinal);
        Assert.Contains("***c5f9", sarif, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Annotations_MaskSecretInErrorLine()
    {
        var report = new RunReport
        {
            CollectionName = "secrets",
            CollectionPath = "secrets.json",
            StartedAt = DateTime.UtcNow,
            DurationMs = 10,
            FailedTests = 1,
        };
        report.Tests.Add(new TestResult
        {
            Name = "login",
            Service = "/auth",
            Method = "Login",
            Error = "boom with leaked-token-c5f9 inside",
        });

        var redactor = new SecretRedactor(LeakedTokenSet);
        await using var sw = new StringWriter();
        await GitHubAnnotations.WriteAsync(sw, report, redactor);
        var output = sw.ToString();

        Assert.Contains("::error", output, StringComparison.Ordinal);
        Assert.DoesNotContain(LeakedToken, output, StringComparison.Ordinal);
        Assert.Contains("***c5f9", output, StringComparison.Ordinal);
    }

    private static RunReport MakeFailingReport()
    {
        var report = new RunReport
        {
            CollectionName = "secrets",
            CollectionPath = "secrets.json",
            StartedAt = DateTime.UtcNow,
            DurationMs = 10,
            FailedTests = 1,
        };
        var t = new TestResult { Name = "login", Service = "/auth", Method = "Login" };
        t.Assertions.Add(new AssertionResult
        {
            Path = "response.token",
            Op = "eq",
            Expected = "expected",
            ActualText = LeakedToken,
            Passed = false,
        });
        report.Tests.Add(t);
        return report;
    }
}

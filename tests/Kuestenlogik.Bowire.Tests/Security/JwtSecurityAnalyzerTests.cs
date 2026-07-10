// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for the deterministic JWT security analyzer (#105): the rule-based
/// flags an AI layer narrates — alg=none, symmetric HMAC, missing/expired/long
/// exp, missing nbf, scope creep, audience binding, future iat, kid surface.
/// </summary>
public sealed class JwtSecurityAnalyzerTests
{
    private const long Now = 1_700_000_000; // fixed "now" for deterministic time checks

    private static string Jwt(string headerJson, string payloadJson, bool signature = true)
        => $"{B64(headerJson)}.{B64(payloadJson)}.{(signature ? "c2lnbmF0dXJl" : "")}";

    private static string B64(string s)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool Has(JwtAnalysis a, JwtFlagLevel level, string claim)
        => a.Flags.Any(f => f.Level == level && f.Claim == claim);

    [Fact]
    public void AlgNone_FlaggedHigh()
    {
        var a = JwtSecurityAnalyzer.Analyze(Jwt("""{"alg":"none"}""", """{"sub":"x"}""", signature: false), nowUnixSeconds: Now);
        Assert.True(a.Parsed);
        Assert.Equal("none", a.Algorithm);
        Assert.True(Has(a, JwtFlagLevel.High, "alg"));
    }

    [Fact]
    public void Hs256_FlaggedSymmetric()
    {
        var a = JwtSecurityAnalyzer.Analyze(Jwt("""{"alg":"HS256"}""", $$"""{"exp":{{Now + 600}},"nbf":{{Now}},"aud":"svc"}"""), nowUnixSeconds: Now);
        Assert.Contains(a.Flags, f => f.Claim == "alg" && f.Message.Contains("symmetric", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingExp_Medium()
    {
        var a = JwtSecurityAnalyzer.Analyze(Jwt("""{"alg":"RS256"}""", """{"sub":"x"}"""), nowUnixSeconds: Now);
        Assert.True(Has(a, JwtFlagLevel.Medium, "exp"));
    }

    [Fact]
    public void ExpiredExp_Info()
    {
        var a = JwtSecurityAnalyzer.Analyze(Jwt("""{"alg":"RS256"}""", $$"""{"exp":{{Now - 100}}}"""), nowUnixSeconds: Now);
        Assert.Contains(a.Flags, f => f.Claim == "exp" && f.Message.Contains("already expired", StringComparison.Ordinal));
    }

    [Fact]
    public void LongLivedExp_Medium()
    {
        var a = JwtSecurityAnalyzer.Analyze(Jwt("""{"alg":"RS256"}""", $$"""{"iat":{{Now}},"exp":{{Now + 3 * 24 * 3600}}}"""), nowUnixSeconds: Now);
        Assert.Contains(a.Flags, f => f.Claim == "exp" && f.Message.Contains("Long-lived", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingNbf_Low()
    {
        var a = JwtSecurityAnalyzer.Analyze(Jwt("""{"alg":"RS256"}""", $$"""{"exp":{{Now + 600}}}"""), nowUnixSeconds: Now);
        Assert.True(Has(a, JwtFlagLevel.Low, "nbf"));
    }

    [Fact]
    public void ScopeCreep_Medium()
    {
        var a = JwtSecurityAnalyzer.Analyze(Jwt("""{"alg":"RS256"}""", $$"""{"exp":{{Now + 600}},"scope":"read admin"}"""), nowUnixSeconds: Now);
        Assert.True(Has(a, JwtFlagLevel.Medium, "scope"));
    }

    [Fact]
    public void ScpArrayCreep_Medium()
    {
        var a = JwtSecurityAnalyzer.Analyze(Jwt("""{"alg":"RS256"}""", $$"""{"exp":{{Now + 600}},"scp":["read","users.delete"]}"""), nowUnixSeconds: Now);
        Assert.True(Has(a, JwtFlagLevel.Medium, "scope"));
    }

    [Fact]
    public void AudMismatch_Medium()
    {
        var a = JwtSecurityAnalyzer.Analyze(Jwt("""{"alg":"RS256"}""", $$"""{"exp":{{Now + 600}},"aud":"svcA"}"""), expectedAudience: "svcB", nowUnixSeconds: Now);
        Assert.True(Has(a, JwtFlagLevel.Medium, "aud"));
    }

    [Fact]
    public void AudMissing_Low()
    {
        var a = JwtSecurityAnalyzer.Analyze(Jwt("""{"alg":"RS256"}""", $$"""{"exp":{{Now + 600}}}"""), nowUnixSeconds: Now);
        Assert.True(Has(a, JwtFlagLevel.Low, "aud"));
    }

    [Fact]
    public void FutureIat_Info()
    {
        var a = JwtSecurityAnalyzer.Analyze(Jwt("""{"alg":"RS256"}""", $$"""{"iat":{{Now + 3600}},"exp":{{Now + 7200}}}"""), nowUnixSeconds: Now);
        Assert.True(Has(a, JwtFlagLevel.Info, "iat"));
    }

    [Fact]
    public void Kid_Info()
    {
        var a = JwtSecurityAnalyzer.Analyze(Jwt("""{"alg":"RS256","kid":"k1"}""", $$"""{"exp":{{Now + 600}}}"""), nowUnixSeconds: Now);
        Assert.Equal("k1", a.KeyId);
        Assert.True(Has(a, JwtFlagLevel.Info, "kid"));
    }

    [Fact]
    public void Malformed_NotParsed()
    {
        var a = JwtSecurityAnalyzer.Analyze("not-a-jwt", nowUnixSeconds: Now);
        Assert.False(a.Parsed);
        Assert.NotNull(a.ParseError);
    }

    [Fact]
    public void CleanToken_NoMediumOrHigh()
    {
        var token = Jwt("""{"alg":"RS256"}""",
            $$"""{"iat":{{Now}},"nbf":{{Now}},"exp":{{Now + 600}},"aud":"svcB","scope":"read"}""");
        var a = JwtSecurityAnalyzer.Analyze(token, expectedAudience: "svcB", nowUnixSeconds: Now);
        Assert.True(a.Parsed);
        Assert.DoesNotContain(a.Flags, f => f.Level is JwtFlagLevel.Medium or JwtFlagLevel.High);
    }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using Kuestenlogik.Bowire.App;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Direct (in-proc) coverage for the <see cref="JwtCommand"/> static
/// methods. The sibling <see cref="JwtCommandTests"/> validates the
/// helper math against a literal jwt.io token; this file drives the
/// public CLI methods so the orchestration code (argument-error
/// branches, --set parsing, --alg-none vs --secret precedence, decode
/// of every token shape) shows up in the coverage report instead of
/// the spawned-subprocess shadow we get from CLI tests.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class JwtCommandDirectTests
{
    private static readonly string[] s_threeClaims = { "role=admin", "exp=9999999999", "isAdmin=true" };
    private static readonly string[] s_badClaim = { "no-equals-sign" };

    private const string Hs256Token =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9" +
        "." +
        "eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwicm9sZSI6InVzZXIiLCJpYXQiOjE1MTYyMzkwMjJ9" +
        "." +
        "SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

    private static (int code, string stdout, string stderr) Capture(Func<Task<int>> action)
    {
        var origOut = Console.Out;
        var origErr = Console.Error;
        using var sbOut = new StringWriter();
        using var sbErr = new StringWriter();
        Console.SetOut(sbOut);
        Console.SetError(sbErr);
        try
        {
            var code = action().GetAwaiter().GetResult();
            return (code, sbOut.ToString(), sbErr.ToString());
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }

    // ---------------- decode ----------------

    [Fact]
    public void Decode_PrintsHeaderPayloadAndSignatureSummary()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, stdout, _) = Capture(() => JwtCommand.RunDecodeAsync(Hs256Token, ct));
        Assert.Equal(0, code);
        Assert.Contains("Header:", stdout, StringComparison.Ordinal);
        Assert.Contains("Payload:", stdout, StringComparison.Ordinal);
        Assert.Contains("\"HS256\"", stdout, StringComparison.Ordinal);
        Assert.Contains("Signature:", stdout, StringComparison.Ordinal);
        Assert.Contains("Declared alg: HS256", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_AlgNoneToken_PrintsBypassWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        // header: {"alg":"none","typ":"JWT"}  payload: {"role":"admin"}
        var token = "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJyb2xlIjoiYWRtaW4ifQ.";
        var (code, stdout, _) = Capture(() => JwtCommand.RunDecodeAsync(token, ct));
        Assert.Equal(0, code);
        Assert.Contains("alg:none", stdout, StringComparison.Ordinal);
        Assert.Contains("Warning", stdout, StringComparison.Ordinal);
        Assert.Contains("(empty — alg:none)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_EmptyToken_ReturnsUsageError()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _, stderr) = Capture(() => JwtCommand.RunDecodeAsync("", ct));
        Assert.Equal(2, code);
        Assert.Contains("Usage", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_MalformedToken_ReturnsParseError()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _, stderr) = Capture(() => JwtCommand.RunDecodeAsync("onlyone", ct));
        Assert.Equal(1, code);
        Assert.Contains("Could not parse", stderr, StringComparison.Ordinal);
    }

    // ---------------- tamper ----------------

    [Fact]
    public void Tamper_AlgNone_DropsSignatureAndFlipsAlgHeader()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, stdout, _) = Capture(() =>
            JwtCommand.RunTamperAsync(Hs256Token, algNone: true, setClaims: Array.Empty<string>(), hmacSecret: null, ct));
        Assert.Equal(0, code);
        Assert.Contains("alg:none", stdout, StringComparison.Ordinal);
        // Find the tampered token line (3 dot-separated segments, last empty).
        var tampered = stdout.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Count(c => c == '.') == 2 && l.EndsWith('.'));
        Assert.NotNull(tampered);
        var parts = tampered!.Split('.');
        Assert.Empty(parts[2]);
        // Decoded header should declare alg:none.
        var headerJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
        Assert.Contains("\"alg\":\"none\"", headerJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Tamper_HmacSecret_RoundTripsThroughHs256Verifier()
    {
        var ct = TestContext.Current.CancellationToken;
        const string secret = "test-secret-for-coverage";
        var (code, stdout, _) = Capture(() =>
            JwtCommand.RunTamperAsync(Hs256Token, algNone: false, setClaims: Array.Empty<string>(), hmacSecret: secret, ct));
        Assert.Equal(0, code);

        // Pluck the tampered token from stdout (the only 3-segment dot-token line).
        var tampered = stdout.Split('\n')
            .Select(l => l.Trim())
            .First(l => l.Count(c => c == '.') == 2 && !l.EndsWith('.'));
        var parts = tampered.Split('.');

        // Verify with HS256 ourselves — toolkit's signing must match the
        // canonical algorithm or downstream tools can't validate the
        // tampered token at all.
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(parts[0] + "." + parts[1])))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, parts[2]);
    }

    [Fact]
    public void Tamper_SetClaim_AcceptsJsonLiteralsAndStrings()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, stdout, _) = Capture(() =>
            JwtCommand.RunTamperAsync(
                Hs256Token,
                algNone: true,
                setClaims: s_threeClaims,
                hmacSecret: null,
                ct));
        Assert.Equal(0, code);
        var tampered = stdout.Split('\n')
            .Select(l => l.Trim())
            .First(l => l.Count(c => c == '.') == 2 && l.EndsWith('.'));
        var payload = Encoding.UTF8.GetString(Base64UrlDecode(tampered.Split('.')[1]));
        Assert.Contains("\"role\":\"admin\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"exp\":9999999999", payload, StringComparison.Ordinal);
        Assert.Contains("\"isAdmin\":true", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void Tamper_SetClaim_BadFormat_ReturnsArgumentError()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _, stderr) = Capture(() =>
            JwtCommand.RunTamperAsync(Hs256Token, algNone: true, setClaims: s_badClaim, hmacSecret: null, ct));
        Assert.Equal(2, code);
        Assert.Contains("claim=value", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Tamper_NoModeSelected_ReturnsArgumentError()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _, stderr) = Capture(() =>
            JwtCommand.RunTamperAsync(Hs256Token, algNone: false, setClaims: Array.Empty<string>(), hmacSecret: null, ct));
        Assert.Equal(2, code);
        Assert.Contains("No tamper mode", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Tamper_EmptyToken_ReturnsUsageError()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _, stderr) = Capture(() =>
            JwtCommand.RunTamperAsync("", algNone: true, setClaims: Array.Empty<string>(), hmacSecret: null, ct));
        Assert.Equal(2, code);
        Assert.Contains("Usage", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Tamper_MalformedToken_ReturnsParseError()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, _, stderr) = Capture(() =>
            JwtCommand.RunTamperAsync("nodot", algNone: true, setClaims: Array.Empty<string>(), hmacSecret: null, ct));
        Assert.Equal(1, code);
        Assert.Contains("Could not parse", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Tamper_SecretOverridesAlgNone_WhenBothSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var (code, stdout, _) = Capture(() =>
            JwtCommand.RunTamperAsync(Hs256Token, algNone: true, setClaims: Array.Empty<string>(), hmacSecret: "k", ct));
        Assert.Equal(0, code);
        Assert.Contains("HS256", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("alg:none", stdout, StringComparison.Ordinal);
    }

    private static byte[] Base64UrlDecode(string segment)
    {
        var pad = (4 - (segment.Length & 3)) & 3;
        var b64 = segment.Replace('-', '+').Replace('_', '/') + new string('=', pad);
        return Convert.FromBase64String(b64);
    }
}

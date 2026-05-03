// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Rest;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for the hand-rolled AWS Signature v4 signer. Verifies the
/// well-known empty-body hash, the request mutation contract (Authorization
/// + X-Amz-Date + X-Amz-Content-Sha256 set in place), and the canonical
/// behaviour around session tokens, query strings, and POST bodies. We
/// don't reuse the AWS test suite's exact signature strings because they
/// pin a specific timestamp — instead we lock down properties of the
/// output that have to hold for any deterministic clock value.
/// </summary>
public class AwsSigV4SignerTests
{
    private const string TestAccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string TestSecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

    [Fact]
    public async Task Sign_AddsAllRequiredHeaders_OnGetRequest()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://iam.amazonaws.com/?Action=ListUsers&Version=2010-05-08");

        await AwsSigV4Signer.SignAsync(request, TestAccessKey, TestSecretKey, sessionToken: null,
            region: "us-east-1", service: "iam", TestContext.Current.CancellationToken);

        Assert.True(request.Headers.Contains("Authorization"));
        Assert.True(request.Headers.Contains("X-Amz-Date"));
        Assert.True(request.Headers.Contains("X-Amz-Content-Sha256"));
        // No session token supplied → header must NOT be present
        Assert.False(request.Headers.Contains("X-Amz-Security-Token"));

        var auth = request.Headers.GetValues("Authorization").Single();
        Assert.StartsWith("AWS4-HMAC-SHA256 ", auth, StringComparison.Ordinal);
        Assert.Contains("Credential=" + TestAccessKey + "/", auth, StringComparison.Ordinal);
        Assert.Contains("/us-east-1/iam/aws4_request", auth, StringComparison.Ordinal);
        Assert.Contains("SignedHeaders=", auth, StringComparison.Ordinal);
        Assert.Contains("Signature=", auth, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_EmptyBody_UsesWellKnownHash()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.amazonaws.com/");

        await AwsSigV4Signer.SignAsync(request, TestAccessKey, TestSecretKey, sessionToken: null,
            region: "us-east-1", service: "service", TestContext.Current.CancellationToken);

        // SHA256("") = e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
        var contentSha = request.Headers.GetValues("X-Amz-Content-Sha256").Single();
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", contentSha);
    }

    [Fact]
    public async Task Sign_WithSessionToken_AddsSecurityTokenHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.amazonaws.com/");

        await AwsSigV4Signer.SignAsync(request, TestAccessKey, TestSecretKey,
            sessionToken: "FQoGZXIvYXdzEHcaDExample/SessionToken",
            region: "us-east-1", service: "service", TestContext.Current.CancellationToken);

        Assert.True(request.Headers.Contains("X-Amz-Security-Token"));
        var token = request.Headers.GetValues("X-Amz-Security-Token").Single();
        Assert.Equal("FQoGZXIvYXdzEHcaDExample/SessionToken", token);

        // The session token must be included in SignedHeaders so the
        // signature binds it — otherwise it could be tampered with.
        var auth = request.Headers.GetValues("Authorization").Single();
        Assert.Contains("x-amz-security-token", auth, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_PostWithJsonBody_HashesTheBody()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.amazonaws.com/api/items");
        request.Content = new StringContent("{\"name\":\"hello\"}", System.Text.Encoding.UTF8, "application/json");

        await AwsSigV4Signer.SignAsync(request, TestAccessKey, TestSecretKey, sessionToken: null,
            region: "eu-central-1", service: "execute-api", TestContext.Current.CancellationToken);

        var contentSha = request.Headers.GetValues("X-Amz-Content-Sha256").Single();
        // Must be 64 lowercase hex chars and NOT the empty-body sentinel
        Assert.Equal(64, contentSha.Length);
        Assert.All(contentSha, c => Assert.True(char.IsDigit(c) || (c >= 'a' && c <= 'f')));
        Assert.NotEqual("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", contentSha);
    }

    [Fact]
    public async Task Sign_TwoIdenticalRequestsAtSameInstant_ProduceMatchingSignatures()
    {
        // Sanity check that the algorithm is fully deterministic over the
        // input — same access key, secret, region, service, URL, body, and
        // (within the same second) timestamp must produce the same signature.
        // We do best-effort by signing twice in quick succession and
        // tolerating up to one second of clock drift.
        using var r1 = new HttpRequestMessage(HttpMethod.Get, "https://s3.amazonaws.com/bucket/key");
        using var r2 = new HttpRequestMessage(HttpMethod.Get, "https://s3.amazonaws.com/bucket/key");

        await AwsSigV4Signer.SignAsync(r1, TestAccessKey, TestSecretKey, null, "us-east-1", "s3", TestContext.Current.CancellationToken);
        await AwsSigV4Signer.SignAsync(r2, TestAccessKey, TestSecretKey, null, "us-east-1", "s3", TestContext.Current.CancellationToken);

        var d1 = r1.Headers.GetValues("X-Amz-Date").Single();
        var d2 = r2.Headers.GetValues("X-Amz-Date").Single();
        if (d1 == d2)
        {
            Assert.Equal(
                r1.Headers.GetValues("Authorization").Single(),
                r2.Headers.GetValues("Authorization").Single());
        }
        // If the second-aligned timestamps differ we can't compare —
        // both signatures are still well-formed, which is enough.
    }

    [Fact]
    public async Task Sign_ThrowsOnRelativeUri()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/relative/path");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await AwsSigV4Signer.SignAsync(request, TestAccessKey, TestSecretKey, null, "us-east-1", "s3", TestContext.Current.CancellationToken));
    }
}

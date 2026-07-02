// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Kuestenlogik.Bowire.Interceptor;

namespace Kuestenlogik.Bowire.Tests.Interceptor;

/// <summary>
/// Unit coverage for <see cref="BowireInterceptorMiddleware.ClassifyBytes"/> —
/// the pure text-vs-binary body classifier the middleware runs over both
/// the request and response payloads. UTF-8 text lands in the text slot;
/// anything with an embedded NUL (or invalid UTF-8) falls back to base64.
/// Reachable via <c>InternalsVisibleTo</c> so the branch matrix can be
/// pinned without booting a host.
/// </summary>
public sealed class InterceptorClassifyBytesTests
{
    [Fact]
    public void Empty_ReturnsNulls()
    {
        var (text, base64) = BowireInterceptorMiddleware.ClassifyBytes(Array.Empty<byte>());
        Assert.Null(text);
        Assert.Null(base64);
    }

    [Fact]
    public void PlainAscii_IsKeptAsText()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"greeting\":\"hi\"}");
        var (text, base64) = BowireInterceptorMiddleware.ClassifyBytes(bytes);
        Assert.Equal("{\"greeting\":\"hi\"}", text);
        Assert.Null(base64);
    }

    [Fact]
    public void MultiByteUtf8_IsKeptAsText()
    {
        var bytes = Encoding.UTF8.GetBytes("Grüße — Küstenlogik ☂");
        var (text, base64) = BowireInterceptorMiddleware.ClassifyBytes(bytes);
        Assert.Equal("Grüße — Küstenlogik ☂", text);
        Assert.Null(base64);
    }

    [Fact]
    public void EmbeddedNul_FallsBackToBase64()
    {
        var bytes = new byte[] { 0x01, 0x00, 0x02, 0xFF };
        var (text, base64) = BowireInterceptorMiddleware.ClassifyBytes(bytes);
        Assert.Null(text);
        Assert.Equal(Convert.ToBase64String(bytes), base64);
    }

    [Fact]
    public void InvalidUtf8WithoutNul_FallsBackToBase64()
    {
        // 0xFF/0xFE are never valid UTF-8 lead bytes; no NUL present so the
        // NUL fast-path passes and the strict decoder is what rejects it.
        var bytes = new byte[] { 0xFF, 0xFE, 0xFD };
        var (text, base64) = BowireInterceptorMiddleware.ClassifyBytes(bytes);
        Assert.Null(text);
        Assert.Equal(Convert.ToBase64String(bytes), base64);
    }
}

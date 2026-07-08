// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// #410: the standalone <see cref="MockServer"/> serves over HTTPS with a
/// self-signed dev certificate when <see cref="MockServerOptions.Https"/> is set.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback self-signed test cert")]
public sealed class MockServerHttpsTests : IDisposable
{
    private readonly string _tempDir;

    public MockServerHttpsTests()
    {
        _tempDir = SafePath.Combine(Path.GetTempPath(), "bowire-https-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Https_SelfSigned_ServesRecordedResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        var recording = new
        {
            id = "rec_https",
            name = "https",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "s", protocol = "rest", service = "S", method = "M", methodType = "Unary",
                    httpPath = "/secure", httpVerb = "GET", status = "OK",
                    response = """{"tls":true}""",
                },
            },
        };
        var path = SafePath.Combine(_tempDir, "https.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), ct);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Https = true, Watch = false, ReplaySpeed = 0 },
            ct);

        // Trust the self-signed cert for this loopback test only.
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        using var client = new HttpClient(handler);

        var url = new Uri($"https://127.0.0.1:{server.Port}/secure");
        var resp = await client.GetAsync(url, ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("""{"tls":true}""", await resp.Content.ReadAsStringAsync(ct));
    }
}

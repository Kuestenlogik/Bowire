// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.App.Plugins;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for <see cref="OciArtifactFetcher"/> — the OCI Distribution v2
/// pull path that backs <c>bowire plugin install --file oci://…</c>.
/// Covers the pure parsers (reference, auth challenge, layer + index
/// selection) plus the end-to-end fetch flow via a fake
/// <see cref="HttpMessageHandler"/>.
/// </summary>
public sealed class OciArtifactFetcherTests : IDisposable
{
    private readonly string _tempDir;

    public OciArtifactFetcherTests()
    {
        _tempDir = SafePath.Combine(Path.GetTempPath(), $"bowire-oci-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    // ---------- ParseReference ----------

    [Theory]
    [InlineData("ghcr.io/acme/zenoh-sidecar:1.0.0", "ghcr.io", "acme/zenoh-sidecar", "1.0.0")]
    [InlineData("oci://ghcr.io/acme/zenoh-sidecar:1.0.0", "ghcr.io", "acme/zenoh-sidecar", "1.0.0")]
    [InlineData("ghcr.io/acme/zenoh-sidecar", "ghcr.io", "acme/zenoh-sidecar", "latest")]
    [InlineData("ghcr.io/acme/zenoh-sidecar@sha256:abc123", "ghcr.io", "acme/zenoh-sidecar", "sha256:abc123")]
    [InlineData("registry.local:5000/acme/zenoh-sidecar:1.0.0", "registry.local:5000", "acme/zenoh-sidecar", "1.0.0")]
    public void ParseReference_Round_Trips_Common_Shapes(string input, string registry, string repo, string reference)
    {
        var (r, p, refr) = OciArtifactFetcher.ParseReference(input);
        Assert.Equal(registry, r);
        Assert.Equal(repo, p);
        Assert.Equal(reference, refr);
    }

    [Fact]
    public void ParseReference_Defaults_Reference_To_Latest()
    {
        var (_, _, reference) = OciArtifactFetcher.ParseReference("ghcr.io/acme/repo");
        Assert.Equal("latest", reference);
    }

    [Fact]
    public void ParseReference_Throws_For_Empty_Input()
    {
        Assert.Throws<ArgumentException>(() => OciArtifactFetcher.ParseReference(""));
        Assert.Throws<ArgumentException>(() => OciArtifactFetcher.ParseReference("   "));
    }

    [Fact]
    public void ParseReference_Throws_For_Missing_Repo_Path()
    {
        // "ghcr.io" alone has no slash → no repo segment.
        Assert.Throws<ArgumentException>(() => OciArtifactFetcher.ParseReference("ghcr.io"));
    }

    [Fact]
    public void ParseReference_Throws_For_Empty_Registry_Or_Repo()
    {
        Assert.Throws<ArgumentException>(() => OciArtifactFetcher.ParseReference("/repo"));
        Assert.Throws<ArgumentException>(() => OciArtifactFetcher.ParseReference("ghcr.io/"));
    }

    // ---------- ParseWwwAuthenticate ----------

    [Fact]
    public void ParseWwwAuthenticate_Extracts_All_Three_Fields()
    {
        const string Header = """Bearer realm="https://ghcr.io/token",service="ghcr.io",scope="repository:acme/repo:pull" """;
        var (realm, service, scope) = OciArtifactFetcher.ParseWwwAuthenticate(Header);
        Assert.Equal("https://ghcr.io/token", realm);
        Assert.Equal("ghcr.io", service);
        Assert.Equal("repository:acme/repo:pull", scope);
    }

    [Fact]
    public void ParseWwwAuthenticate_Works_Without_Bearer_Prefix()
    {
        const string Header = """realm="https://example/token",service="reg" """;
        var (realm, service, _) = OciArtifactFetcher.ParseWwwAuthenticate(Header);
        Assert.Equal("https://example/token", realm);
        Assert.Equal("reg", service);
    }

    [Fact]
    public void ParseWwwAuthenticate_Returns_Nulls_For_Empty_Or_Missing()
    {
        var (realm, service, scope) = OciArtifactFetcher.ParseWwwAuthenticate(null);
        Assert.Null(realm);
        Assert.Null(service);
        Assert.Null(scope);

        (realm, service, scope) = OciArtifactFetcher.ParseWwwAuthenticate("");
        Assert.Null(realm);
        Assert.Null(service);
        Assert.Null(scope);
    }

    [Fact]
    public void ParseWwwAuthenticate_Returns_Null_For_Truly_Unterminated_Quote()
    {
        // A realm value with no closing quote anywhere in the rest of
        // the header walks to end-of-string without finding one, so
        // the parser hands back null instead of a half-parsed value.
        const string Header = "Bearer realm=\"https://example/token";
        var (realm, _, _) = OciArtifactFetcher.ParseWwwAuthenticate(Header);
        Assert.Null(realm);
    }

    // ---------- SelectLayerDigest ----------

    [Fact]
    public void SelectLayerDigest_Prefers_Zip_MediaType_Over_First_Layer()
    {
        // First layer uses a plain "tar" media type (no "zip" substring)
        // so the iteration falls through to the explicit zip layer.
        const string Manifest = """
        {
            "layers": [
                { "mediaType": "application/vnd.docker.image.rootfs.diff.tar", "digest": "sha256:tar" },
                { "mediaType": "application/zip", "digest": "sha256:zip" }
            ]
        }
        """;
        Assert.Equal("sha256:zip", OciArtifactFetcher.SelectLayerDigest(Manifest));
    }

    [Fact]
    public void SelectLayerDigest_Falls_Back_To_First_Layer_When_No_Zip()
    {
        const string Manifest = """
        {
            "layers": [
                { "mediaType": "application/octet-stream", "digest": "sha256:first" },
                { "mediaType": "application/octet-stream", "digest": "sha256:second" }
            ]
        }
        """;
        Assert.Equal("sha256:first", OciArtifactFetcher.SelectLayerDigest(Manifest));
    }

    [Fact]
    public void SelectLayerDigest_Returns_Null_For_Empty_Layers()
    {
        Assert.Null(OciArtifactFetcher.SelectLayerDigest("""{ "layers": [] }"""));
    }

    [Fact]
    public void SelectLayerDigest_Returns_Null_For_Missing_Layers_Property()
    {
        Assert.Null(OciArtifactFetcher.SelectLayerDigest("""{ "schemaVersion": 2 }"""));
    }

    [Fact]
    public void SelectLayerDigest_Returns_Null_For_Corrupt_Json()
    {
        Assert.Null(OciArtifactFetcher.SelectLayerDigest("{not json"));
    }

    [Fact]
    public void SelectLayerDigest_Skips_Layer_With_Empty_Digest()
    {
        const string Manifest = """
        {
            "layers": [
                { "mediaType": "application/zip", "digest": "" },
                { "mediaType": "application/octet-stream", "digest": "sha256:second" }
            ]
        }
        """;
        Assert.Equal("sha256:second", OciArtifactFetcher.SelectLayerDigest(Manifest));
    }

    // ---------- SelectManifestFromIndex ----------

    [Fact]
    public void SelectManifestFromIndex_Returns_First_Child_Digest()
    {
        const string Index = """
        {
            "manifests": [
                { "digest": "sha256:amd64", "platform": { "architecture": "amd64" } },
                { "digest": "sha256:arm64", "platform": { "architecture": "arm64" } }
            ]
        }
        """;
        Assert.Equal("sha256:amd64", OciArtifactFetcher.SelectManifestFromIndex(Index));
    }

    [Fact]
    public void SelectManifestFromIndex_Returns_Null_For_Image_Manifest_With_Layers()
    {
        // The index detector specifically returns null when "layers" is
        // present so FetchToFile doesn't try to recurse on something
        // that's already an image manifest.
        const string ImageManifest = """{ "layers": [{ "digest": "sha256:foo" }] }""";
        Assert.Null(OciArtifactFetcher.SelectManifestFromIndex(ImageManifest));
    }

    [Fact]
    public void SelectManifestFromIndex_Returns_Null_For_Empty_Manifests()
    {
        Assert.Null(OciArtifactFetcher.SelectManifestFromIndex("""{ "manifests": [] }"""));
    }

    [Fact]
    public void SelectManifestFromIndex_Returns_Null_For_Corrupt_Json()
    {
        Assert.Null(OciArtifactFetcher.SelectManifestFromIndex("{not json"));
    }

    // ---------- FetchToFileAsync (end-to-end via fake handler) ----------

    [Fact]
    public async Task FetchToFileAsync_Happy_Path_Anonymous_Pull()
    {
        // Single image manifest, single layer, anonymous (no 401). The
        // fetcher should write the blob bytes to the destination file.
        const string Manifest = """{"layers":[{"mediaType":"application/zip","digest":"sha256:blob"}]}""";
        var blobBytes = Encoding.UTF8.GetBytes("ZIPBYTES");

        using var http = NewHttpClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            return path switch
            {
                "/v2/acme/zenoh-sidecar/manifests/1.0.0" => Json(Manifest),
                "/v2/acme/zenoh-sidecar/blobs/sha256:blob" => Bytes(blobBytes),
                _ => NotFound(),
            };
        });

        var dest = SafePath.Combine(_tempDir, "out.zip");
        await OciArtifactFetcher.FetchToFileAsync(http, "ghcr.io/acme/zenoh-sidecar:1.0.0", dest, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(dest));
        Assert.Equal(blobBytes, await File.ReadAllBytesAsync(dest, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FetchToFileAsync_Follows_Multi_Arch_Index()
    {
        const string Index = """{"manifests":[{"digest":"sha256:inner","platform":{"architecture":"amd64"}}]}""";
        const string InnerManifest = """{"layers":[{"mediaType":"application/zip","digest":"sha256:blob"}]}""";

        using var http = NewHttpClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            return path switch
            {
                "/v2/acme/repo/manifests/1.0.0" => Json(Index),
                "/v2/acme/repo/manifests/sha256:inner" => Json(InnerManifest),
                "/v2/acme/repo/blobs/sha256:blob" => Bytes(Encoding.UTF8.GetBytes("inner-blob")),
                _ => NotFound(),
            };
        });

        var dest = SafePath.Combine(_tempDir, "out.zip");
        await OciArtifactFetcher.FetchToFileAsync(http, "ghcr.io/acme/repo:1.0.0", dest, TestContext.Current.CancellationToken);

        Assert.Equal("inner-blob", await File.ReadAllTextAsync(dest, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FetchToFileAsync_Performs_401_Bearer_Token_Dance()
    {
        // First manifest call returns 401 with a WWW-Authenticate; the
        // fetcher should hit /token with the right scope, then retry
        // the original request with the Bearer header.
        const string Manifest = """{"layers":[{"mediaType":"application/zip","digest":"sha256:blob"}]}""";
        var seenAuthHeaders = new List<string?>();

        using var http = NewHttpClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var auth = req.Headers.Authorization?.Parameter;

            if (path == "/v2/acme/repo/manifests/1.0.0")
            {
                seenAuthHeaders.Add(auth);
                if (auth == "test-token")
                    return Json(Manifest);
                var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                resp.Headers.TryAddWithoutValidation("WWW-Authenticate",
                    """Bearer realm="https://ghcr.io/token",service="ghcr.io",scope="repository:acme/repo:pull" """);
                return resp;
            }
            if (req.RequestUri.Host == "ghcr.io" && path == "/token")
            {
                return Json("""{"token":"test-token"}""");
            }
            if (path == "/v2/acme/repo/blobs/sha256:blob" && auth == "test-token")
                return Bytes(Encoding.UTF8.GetBytes("authd-blob"));
            return NotFound();
        });

        var dest = SafePath.Combine(_tempDir, "out.zip");
        await OciArtifactFetcher.FetchToFileAsync(http, "ghcr.io/acme/repo:1.0.0", dest, TestContext.Current.CancellationToken);

        Assert.Equal("authd-blob", await File.ReadAllTextAsync(dest, TestContext.Current.CancellationToken));
        // First call had no token; retry carried "test-token".
        Assert.Equal(new[] { (string?)null, "test-token" }, seenAuthHeaders);
    }

    [Fact]
    public async Task FetchToFileAsync_Uses_AccessToken_Field_When_Token_Missing()
    {
        // GHCR returns "token"; Docker Hub returns "access_token". Both
        // should work. Same flow, swap the JSON shape.
        const string Manifest = """{"layers":[{"mediaType":"application/zip","digest":"sha256:blob"}]}""";

        using var http = NewHttpClient(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var auth = req.Headers.Authorization?.Parameter;

            if (path == "/v2/acme/repo/manifests/1.0.0")
            {
                if (auth == "access-token") return Json(Manifest);
                var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                resp.Headers.TryAddWithoutValidation("WWW-Authenticate",
                    """Bearer realm="https://auth.docker.io/token" """);
                return resp;
            }
            if (req.RequestUri.Host == "auth.docker.io" && path == "/token")
                return Json("""{"access_token":"access-token"}""");
            if (path == "/v2/acme/repo/blobs/sha256:blob" && auth == "access-token")
                return Bytes(Encoding.UTF8.GetBytes("ok"));
            return NotFound();
        });

        var dest = SafePath.Combine(_tempDir, "out.zip");
        await OciArtifactFetcher.FetchToFileAsync(http, "ghcr.io/acme/repo:1.0.0", dest, TestContext.Current.CancellationToken);
        Assert.Equal("ok", await File.ReadAllTextAsync(dest, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FetchToFileAsync_Throws_When_401_Has_No_Usable_Challenge()
    {
        // 401 without a realm gives the fetcher nothing to retry against;
        // it surfaces a clear InvalidOperationException so the install
        // CLI can render a "configure credentials" hint.
        using var http = NewHttpClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var dest = SafePath.Combine(_tempDir, "out.zip");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OciArtifactFetcher.FetchToFileAsync(http, "ghcr.io/acme/repo:1.0.0", dest, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FetchToFileAsync_Throws_When_Manifest_Has_No_Layers()
    {
        // Artifact-format manifest without a layer Bowire could pull
        // → the InvalidOperationException maps to "OCI artifact has no
        // usable layer" so the user knows the artifact isn't a sidecar.
        using var http = NewHttpClient(_ => Json("""{"layers":[]}"""));

        var dest = SafePath.Combine(_tempDir, "out.zip");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OciArtifactFetcher.FetchToFileAsync(http, "ghcr.io/acme/repo:1.0.0", dest, TestContext.Current.CancellationToken));
        Assert.Contains("layer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- fakery helpers ----------

    private static HttpClient NewHttpClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        // CA2000: HttpClient owns the handler and disposes it.
#pragma warning disable CA2000
        return new HttpClient(new FakeHttpMessageHandler(handler));
#pragma warning restore CA2000
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}

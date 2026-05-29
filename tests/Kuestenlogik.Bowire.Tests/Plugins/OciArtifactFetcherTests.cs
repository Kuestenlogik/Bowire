// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Plugins;
using Xunit;

namespace Kuestenlogik.Bowire.Tests.Plugins;

/// <summary>
/// Unit coverage for the pure parsers behind <c>bowire plugin install
/// --file oci://…</c>. The HTTP / token dance itself needs a live
/// registry and is left to manual / e2e verification; these lock down
/// the reference-, challenge-, and manifest-parsing that the fetch
/// builds on.
/// </summary>
public sealed class OciArtifactFetcherTests
{
    [Theory]
    [InlineData("oci://ghcr.io/acme/zenoh-sidecar:1.0.0", "ghcr.io", "acme/zenoh-sidecar", "1.0.0")]
    [InlineData("ghcr.io/acme/zenoh-sidecar:1.0.0", "ghcr.io", "acme/zenoh-sidecar", "1.0.0")]
    [InlineData("oci://ghcr.io/acme/zenoh-sidecar", "ghcr.io", "acme/zenoh-sidecar", "latest")]
    [InlineData("oci://registry.example.com/team/sub/repo:v2", "registry.example.com", "team/sub/repo", "v2")]
    public void ParseReference_splits_registry_repo_tag(string input, string registry, string repo, string reference)
    {
        var (r, p, t) = OciArtifactFetcher.ParseReference(input);
        Assert.Equal(registry, r);
        Assert.Equal(repo, p);
        Assert.Equal(reference, t);
    }

    [Fact]
    public void ParseReference_keeps_registry_port_distinct_from_tag()
    {
        var (registry, repo, reference) = OciArtifactFetcher.ParseReference("oci://localhost:5000/acme/sidecar:dev");
        Assert.Equal("localhost:5000", registry);
        Assert.Equal("acme/sidecar", repo);
        Assert.Equal("dev", reference);
    }

    [Fact]
    public void ParseReference_handles_digest()
    {
        var (registry, repo, reference) = OciArtifactFetcher.ParseReference(
            "oci://ghcr.io/acme/sidecar@sha256:abc123");
        Assert.Equal("ghcr.io", registry);
        Assert.Equal("acme/sidecar", repo);
        Assert.Equal("sha256:abc123", reference);
    }

    [Fact]
    public void ParseReference_digest_on_ported_registry()
    {
        var (registry, repo, reference) = OciArtifactFetcher.ParseReference(
            "localhost:5000/acme/sidecar@sha256:deadbeef");
        Assert.Equal("localhost:5000", registry);
        Assert.Equal("acme/sidecar", repo);
        Assert.Equal("sha256:deadbeef", reference);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseReference_rejects_empty(string input)
        => Assert.Throws<ArgumentException>(() => OciArtifactFetcher.ParseReference(input));

    [Theory]
    [InlineData("ghcr.io")]            // no repo path at all
    [InlineData("oci://just-a-name")]  // missing '/'
    public void ParseReference_rejects_missing_repo(string input)
        => Assert.Throws<ArgumentException>(() => OciArtifactFetcher.ParseReference(input));

    [Fact]
    public void ParseWwwAuthenticate_extracts_all_fields()
    {
        var (realm, service, scope) = OciArtifactFetcher.ParseWwwAuthenticate(
            "Bearer realm=\"https://ghcr.io/token\",service=\"ghcr.io\",scope=\"repository:acme/sidecar:pull\"");
        Assert.Equal("https://ghcr.io/token", realm);
        Assert.Equal("ghcr.io", service);
        Assert.Equal("repository:acme/sidecar:pull", scope);
    }

    [Fact]
    public void ParseWwwAuthenticate_tolerates_missing_optional_fields()
    {
        var (realm, service, scope) = OciArtifactFetcher.ParseWwwAuthenticate(
            "Bearer realm=\"https://auth.docker.io/token\"");
        Assert.Equal("https://auth.docker.io/token", realm);
        Assert.Null(service);
        Assert.Null(scope);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ParseWwwAuthenticate_returns_nulls_for_empty(string? input)
    {
        var (realm, service, scope) = OciArtifactFetcher.ParseWwwAuthenticate(input);
        Assert.Null(realm);
        Assert.Null(service);
        Assert.Null(scope);
    }

    [Fact]
    public void SelectLayerDigest_prefers_zip_media_type()
    {
        const string manifest = """
            {
              "config": { "digest": "sha256:config" },
              "layers": [
                { "mediaType": "application/vnd.oci.image.layer.v1.tar", "digest": "sha256:tar" },
                { "mediaType": "application/zip", "digest": "sha256:zip" }
              ]
            }
            """;
        Assert.Equal("sha256:zip", OciArtifactFetcher.SelectLayerDigest(manifest));
    }

    [Fact]
    public void SelectLayerDigest_falls_back_to_first_layer()
    {
        const string manifest = """
            { "layers": [ { "mediaType": "application/octet-stream", "digest": "sha256:first" } ] }
            """;
        Assert.Equal("sha256:first", OciArtifactFetcher.SelectLayerDigest(manifest));
    }

    [Theory]
    [InlineData("{ \"layers\": [] }")]
    [InlineData("{ \"manifests\": [] }")]
    [InlineData("not json")]
    public void SelectLayerDigest_returns_null_without_usable_layer(string manifest)
        => Assert.Null(OciArtifactFetcher.SelectLayerDigest(manifest));

    [Fact]
    public void SelectManifestFromIndex_returns_first_child_for_index()
    {
        const string index = """
            {
              "mediaType": "application/vnd.oci.image.index.v1+json",
              "manifests": [
                { "digest": "sha256:amd64", "platform": { "architecture": "amd64" } },
                { "digest": "sha256:arm64", "platform": { "architecture": "arm64" } }
              ]
            }
            """;
        Assert.Equal("sha256:amd64", OciArtifactFetcher.SelectManifestFromIndex(index));
    }

    [Fact]
    public void SelectManifestFromIndex_returns_null_for_image_manifest()
    {
        const string manifest = """
            { "layers": [ { "digest": "sha256:layer" } ] }
            """;
        Assert.Null(OciArtifactFetcher.SelectManifestFromIndex(manifest));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("garbage")]
    public void SelectManifestFromIndex_returns_null_for_unrelated_json(string manifest)
        => Assert.Null(OciArtifactFetcher.SelectManifestFromIndex(manifest));
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for the <c>bowire vulndb</c> template-cache commands (#26) —
/// <see cref="VulnDbCache"/> path/index helpers, <see cref="VulnDbUpdateCommand"/>
/// (directory / tarball / URL / GitHub-release sources), <see cref="VulnDbListCommand"/>,
/// and the <see cref="ScanCommand"/> default-cache fallback. All offline: the
/// network paths use an injected <see cref="HttpMessageHandler"/>, everything
/// else uses temp directories, so nothing touches the real <c>~/.bowire/vulndb</c>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed",
    Justification = "Every RunAsync task is synchronously joined via GetAwaiter().GetResult() inside Capture before the StringWriter / StubHandler leaves scope, so the disposables stay live for the whole task.")]
public sealed class VulnDbCommandTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "bowire-vulndbtest-" + Guid.NewGuid().ToString("N"));

    public VulnDbCommandTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private static (int code, string stdout, string stderr) Capture(Func<StringWriter, StringWriter, Task<int>> action)
    {
        using var sbOut = new StringWriter();
        using var sbErr = new StringWriter();
        var code = action(sbOut, sbErr).GetAwaiter().GetResult();
        return (code, sbOut.ToString(), sbErr.ToString());
    }

    // A minimal but valid attack template — enough that scan loads it and the
    // index/list surfaces the metadata.
    private const string SampleTemplate = """
        {
          "id": "bwr-rest-999-teapot",
          "name": "Server admits to being a teapot",
          "attack": true,
          "vulnerability": { "id": "BWR-REST-999", "severity": "low", "protocols": ["rest"] },
          "steps": [ { "id": "probe-1", "protocol": "rest", "httpVerb": "GET", "httpPath": "/probe" } ],
          "vulnerableWhen": { "status": 418 }
        }
        """;

    private const string SampleIndex = """
        {
          "schema": 1,
          "generatedAt": 0,
          "count": 1,
          "templates": [
            { "id": "bwr-rest-999-teapot", "path": "rest/teapot.json", "name": "Server admits to being a teapot",
              "protocol": "rest", "protocols": ["rest"], "severity": "low", "cvss": 3.1, "cwe": "CWE-000", "owaspApi": "API8-2023-SECMISCONF" }
          ]
        }
        """;

    /// <summary>Build a source tree (templates/rest/teapot.json + optional index) under a fresh dir.</summary>
    private string MakeSourceDir(bool withIndex = true)
    {
        var src = Path.Combine(_tmp, "src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(src, "templates", "rest"));
        File.WriteAllText(Path.Combine(src, "templates", "rest", "teapot.json"), SampleTemplate);
        if (withIndex) File.WriteAllText(Path.Combine(src, "templates-index.json"), SampleIndex);
        return src;
    }

    /// <summary>gzip-tar a source dir into a .tar.gz file, mirroring the release asset shape.</summary>
    private string MakeTarball()
    {
        var src = MakeSourceDir();
        var tgz = Path.Combine(_tmp, "tarball-" + Guid.NewGuid().ToString("N") + ".tar.gz");
        using (var fs = File.Create(tgz))
        using (var gz = new GZipStream(fs, CompressionMode.Compress))
        {
            TarFile.CreateFromDirectory(src, gz, includeBaseDirectory: false);
        }
        return tgz;
    }

    // ---------------- VulnDbCache ----------------

    [Fact]
    public void DefaultRoot_ends_with_bowire_vulndb()
    {
        Assert.EndsWith(Path.Combine(".bowire", "vulndb"), VulnDbCache.DefaultRoot(), StringComparison.Ordinal);
    }

    [Fact]
    public void HasTemplates_false_when_empty_true_after_populate()
    {
        var root = Path.Combine(_tmp, "cache");
        Assert.False(VulnDbCache.HasTemplates(root));
        Assert.Equal(0, VulnDbCache.CountTemplates(root));

        Directory.CreateDirectory(Path.Combine(root, "templates", "rest"));
        File.WriteAllText(Path.Combine(root, "templates", "rest", "teapot.json"), SampleTemplate);
        Assert.True(VulnDbCache.HasTemplates(root));
        Assert.Equal(1, VulnDbCache.CountTemplates(root));
    }

    [Fact]
    public void EnumerateTemplates_prefers_index()
    {
        var root = Path.Combine(_tmp, "cache-idx");
        Directory.CreateDirectory(Path.Combine(root, "templates", "rest"));
        File.WriteAllText(Path.Combine(root, "templates", "rest", "teapot.json"), SampleTemplate);
        File.WriteAllText(VulnDbCache.IndexPath(root), SampleIndex);

        var rows = VulnDbCache.EnumerateTemplates(root);
        var row = Assert.Single(rows);
        Assert.Equal("bwr-rest-999-teapot", row.Id);
        Assert.Equal("rest", row.Protocol);
        Assert.Equal("low", row.Severity);
    }

    [Fact]
    public void EnumerateTemplates_walks_tree_when_index_absent()
    {
        var root = Path.Combine(_tmp, "cache-noidx");
        Directory.CreateDirectory(Path.Combine(root, "templates", "rest"));
        File.WriteAllText(Path.Combine(root, "templates", "rest", "teapot.json"), SampleTemplate);

        var rows = VulnDbCache.EnumerateTemplates(root);
        var row = Assert.Single(rows);
        Assert.Equal("bwr-rest-999-teapot", row.Id);
        Assert.Equal("rest", row.Protocol);
        Assert.Equal("low", row.Severity);
    }

    // ---------------- update: directory source ----------------

    [Fact]
    public void Update_from_directory_populates_cache()
    {
        var src = MakeSourceDir();
        var dest = Path.Combine(_tmp, "dest-dir");

        var (code, stdout, _) = Capture((o, e) => VulnDbUpdateCommand.RunAsync(
            new VulnDbUpdateOptions { Source = src, Dest = dest }, TestContext.Current.CancellationToken, o, e));

        Assert.Equal(0, code);
        Assert.Contains("Updated 1 template(s)", stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dest, "templates", "rest", "teapot.json")));
        Assert.True(File.Exists(VulnDbCache.IndexPath(dest)));
    }

    [Fact]
    public void Update_replaces_stale_templates()
    {
        var dest = Path.Combine(_tmp, "dest-stale");
        // Pre-seed a template that is NOT in the incoming source.
        Directory.CreateDirectory(Path.Combine(dest, "templates", "grpc"));
        File.WriteAllText(Path.Combine(dest, "templates", "grpc", "old.json"), SampleTemplate);

        var src = MakeSourceDir();
        var (code, _, _) = Capture((o, e) => VulnDbUpdateCommand.RunAsync(
            new VulnDbUpdateOptions { Source = src, Dest = dest }, TestContext.Current.CancellationToken, o, e));

        Assert.Equal(0, code);
        // The stale template is gone — templates/ was swapped wholesale.
        Assert.False(File.Exists(Path.Combine(dest, "templates", "grpc", "old.json")));
        Assert.True(File.Exists(Path.Combine(dest, "templates", "rest", "teapot.json")));
    }

    // ---------------- update: tarball source ----------------

    [Fact]
    public void Update_from_tarball_file_extracts_cache()
    {
        var tgz = MakeTarball();
        var dest = Path.Combine(_tmp, "dest-tgz");

        var (code, stdout, _) = Capture((o, e) => VulnDbUpdateCommand.RunAsync(
            new VulnDbUpdateOptions { Source = tgz, Dest = dest }, TestContext.Current.CancellationToken, o, e));

        Assert.Equal(0, code);
        Assert.Contains("Updated 1 template(s)", stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dest, "templates", "rest", "teapot.json")));
    }

    // ---------------- update: error cases ----------------

    [Fact]
    public void Update_missing_source_returns_usage_error()
    {
        var dest = Path.Combine(_tmp, "dest-missing");
        var (code, _, stderr) = Capture((o, e) => VulnDbUpdateCommand.RunAsync(
            new VulnDbUpdateOptions { Source = Path.Combine(_tmp, "does-not-exist"), Dest = dest },
            TestContext.Current.CancellationToken, o, e));

        Assert.Equal(2, code);
        Assert.Contains("Source not found", stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(dest, "templates")));
    }

    [Fact]
    public void Update_source_without_templates_leaves_cache_unchanged()
    {
        // Source dir exists but has no templates/ subtree.
        var src = Path.Combine(_tmp, "src-empty");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "readme.txt"), "no templates here");

        var dest = Path.Combine(_tmp, "dest-preserve");
        Directory.CreateDirectory(Path.Combine(dest, "templates", "rest"));
        File.WriteAllText(Path.Combine(dest, "templates", "rest", "keep.json"), SampleTemplate);

        var (code, _, stderr) = Capture((o, e) => VulnDbUpdateCommand.RunAsync(
            new VulnDbUpdateOptions { Source = src, Dest = dest }, TestContext.Current.CancellationToken, o, e));

        Assert.Equal(1, code);
        Assert.Contains("No templates/ directory", stderr, StringComparison.Ordinal);
        // Existing cache is untouched.
        Assert.True(File.Exists(Path.Combine(dest, "templates", "rest", "keep.json")));
    }

    // ---------------- update: URL + GitHub-release (injected handler) ----------------

    [Fact]
    public void Update_from_url_downloads_and_extracts()
    {
        var tarballBytes = File.ReadAllBytes(MakeTarball());
        using var handler = new StubHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tarballBytes) });
        var dest = Path.Combine(_tmp, "dest-url");

        var (code, stdout, _) = Capture((o, e) => VulnDbUpdateCommand.RunAsync(
            new VulnDbUpdateOptions { Source = "https://example.test/templates.tar.gz", Dest = dest },
            TestContext.Current.CancellationToken, o, e, handler));

        Assert.Equal(0, code);
        Assert.Contains("Updated 1 template(s)", stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dest, "templates", "rest", "teapot.json")));
    }

    [Fact]
    public void Update_default_resolves_github_release_then_downloads()
    {
        var tarballBytes = File.ReadAllBytes(MakeTarball());
        const string releaseJson = """
            { "tag_name": "v9.9.9", "assets": [
                { "name": "templates-index.json", "browser_download_url": "https://example.test/index.json" },
                { "name": "bowire-vulndb-templates-v9.9.9.tar.gz", "browser_download_url": "https://example.test/tarball" }
            ] }
            """;
        using var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("api.github.com", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson, Encoding.UTF8) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tarballBytes) };
        });
        var dest = Path.Combine(_tmp, "dest-gh");

        var (code, stdout, _) = Capture((o, e) => VulnDbUpdateCommand.RunAsync(
            new VulnDbUpdateOptions { Dest = dest }, TestContext.Current.CancellationToken, o, e, handler));

        Assert.Equal(0, code);
        Assert.Contains("v9.9.9", stdout, StringComparison.Ordinal);
        Assert.Contains("Updated 1 template(s)", stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dest, "templates", "rest", "teapot.json")));
    }

    [Fact]
    public void Update_default_release_without_tarball_asset_fails()
    {
        const string releaseJson = """{ "tag_name": "v9.9.9", "assets": [] }""";
        using var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson, Encoding.UTF8) });
        var dest = Path.Combine(_tmp, "dest-noasset");

        var (code, _, stderr) = Capture((o, e) => VulnDbUpdateCommand.RunAsync(
            new VulnDbUpdateOptions { Dest = dest }, TestContext.Current.CancellationToken, o, e, handler));

        Assert.Equal(1, code);
        Assert.Contains("no bowire-vulndb-templates-*.tar.gz asset", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Update_pinned_ref_hits_the_tag_endpoint_and_reports_it()
    {
        var tarballBytes = File.ReadAllBytes(MakeTarball());
        string? apiUrl = null;
        const string releaseJson = """
            { "tag_name": "v0.1.0", "assets": [
                { "name": "bowire-vulndb-templates-v0.1.0.tar.gz", "browser_download_url": "https://example.test/tarball" }
            ] }
            """;
        using var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("api.github.com", StringComparison.Ordinal))
            {
                apiUrl = url;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson, Encoding.UTF8) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tarballBytes) };
        });
        var dest = Path.Combine(_tmp, "dest-ref");

        var (code, stdout, _) = Capture((o, e) => VulnDbUpdateCommand.RunAsync(
            new VulnDbUpdateOptions { Ref = "v0.1.0", Dest = dest }, TestContext.Current.CancellationToken, o, e, handler));

        Assert.Equal(0, code);
        // Pinned ref resolves via releases/tags/<tag>, not releases/latest.
        Assert.NotNull(apiUrl);
        Assert.Contains("/releases/tags/v0.1.0", apiUrl, StringComparison.Ordinal);
        Assert.Contains("Resolving Bowire.VulnDb release v0.1.0", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_fetch_failure_returns_exit_1_gracefully()
    {
        using var handler = new StubHandler(_ => throw new HttpRequestException("connection reset"));
        var dest = Path.Combine(_tmp, "dest-fetchfail");

        var (code, _, stderr) = Capture((o, e) => VulnDbUpdateCommand.RunAsync(
            new VulnDbUpdateOptions { Source = "https://example.test/x.tar.gz", Dest = dest },
            TestContext.Current.CancellationToken, o, e, handler));

        Assert.Equal(1, code);
        Assert.Contains("Could not fetch templates", stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(dest, "templates")));
    }

    [Fact]
    public void Update_from_indexless_source_clears_a_stale_index()
    {
        // First populate a cache WITH an index (7 phantom entries not on disk
        // after the next update).
        var dest = Path.Combine(_tmp, "dest-staleidx");
        Capture((o, e) => VulnDbUpdateCommand.RunAsync(
            new VulnDbUpdateOptions { Source = MakeSourceDir(withIndex: true), Dest = dest },
            TestContext.Current.CancellationToken, o, e));
        Assert.True(File.Exists(VulnDbCache.IndexPath(dest)));

        // Now update from an index-less source (a bare checkout).
        var (code, _, _) = Capture((o, e) => VulnDbUpdateCommand.RunAsync(
            new VulnDbUpdateOptions { Source = MakeSourceDir(withIndex: false), Dest = dest },
            TestContext.Current.CancellationToken, o, e));

        Assert.Equal(0, code);
        // The stale index is gone, so list walks the tree instead of reporting
        // phantom templates.
        Assert.False(File.Exists(VulnDbCache.IndexPath(dest)));
        Assert.True(File.Exists(Path.Combine(dest, "templates", "rest", "teapot.json")));
    }

    // ---------------- list ----------------

    [Fact]
    public void List_empty_cache_hints_update()
    {
        var (code, _, stderr) = Capture((o, e) => VulnDbListCommand.RunAsync(
            new VulnDbListOptions { Dest = Path.Combine(_tmp, "nope") }, TestContext.Current.CancellationToken, o, e));

        Assert.Equal(0, code);
        Assert.Contains("bowire vulndb update", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void List_populated_cache_prints_rows_and_count()
    {
        var dest = Path.Combine(_tmp, "list-cache");
        Capture((o, e) => VulnDbUpdateCommand.RunAsync(
            new VulnDbUpdateOptions { Source = MakeSourceDir(), Dest = dest }, TestContext.Current.CancellationToken, o, e));

        var (code, stdout, _) = Capture((o, e) => VulnDbListCommand.RunAsync(
            new VulnDbListOptions { Dest = dest }, TestContext.Current.CancellationToken, o, e));

        Assert.Equal(0, code);
        Assert.Contains("1 template(s)", stdout, StringComparison.Ordinal);
        Assert.Contains("bwr-rest-999-teapot", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void List_protocol_filter_excludes_other_protocols()
    {
        var dest = Path.Combine(_tmp, "list-filter");
        Capture((o, e) => VulnDbUpdateCommand.RunAsync(
            new VulnDbUpdateOptions { Source = MakeSourceDir(), Dest = dest }, TestContext.Current.CancellationToken, o, e));

        var (code, stdout, _) = Capture((o, e) => VulnDbListCommand.RunAsync(
            new VulnDbListOptions { Dest = dest, Protocol = "grpc" }, TestContext.Current.CancellationToken, o, e));

        Assert.Equal(0, code);
        Assert.Contains("No 'grpc' templates", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("bwr-rest-999-teapot", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void List_protocol_filter_includes_matching_rows()
    {
        var dest = Path.Combine(_tmp, "list-match");
        Capture((o, e) => VulnDbUpdateCommand.RunAsync(
            new VulnDbUpdateOptions { Source = MakeSourceDir(), Dest = dest }, TestContext.Current.CancellationToken, o, e));

        var (code, stdout, _) = Capture((o, e) => VulnDbListCommand.RunAsync(
            new VulnDbListOptions { Dest = dest, Protocol = "rest" }, TestContext.Current.CancellationToken, o, e));

        Assert.Equal(0, code);
        Assert.Contains("1 template(s)", stdout, StringComparison.Ordinal);
        Assert.Contains("bwr-rest-999-teapot", stdout, StringComparison.Ordinal);
    }

    // ---------------- scan default-cache fallback (offline resolver) ----------------
    // The full scan-with-cache path (loads cache templates + probes a live
    // target) is exercised in the end-to-end verification, not here; these
    // pin the pure resolution gate without touching the network.

    [Fact]
    public void ResolveCacheTemplatesDir_returns_cache_when_no_source_given()
    {
        var dest = Path.Combine(_tmp, "scan-cache");
        Capture((o, e) => VulnDbUpdateCommand.RunAsync(
            new VulnDbUpdateOptions { Source = MakeSourceDir(), Dest = dest }, TestContext.Current.CancellationToken, o, e));

        var resolved = ScanCommand.ResolveCacheTemplatesDir(new ScanOptions
        {
            Target = "http://example.test/",
            VulnDbCacheRoot = dest,
        });

        Assert.Equal(Path.Combine(dest, "templates"), resolved);
    }

    [Fact]
    public void ResolveCacheTemplatesDir_null_when_explicit_templates_given()
    {
        var dest = Path.Combine(_tmp, "scan-cache-ignored");
        Capture((o, e) => VulnDbUpdateCommand.RunAsync(
            new VulnDbUpdateOptions { Source = MakeSourceDir(), Dest = dest }, TestContext.Current.CancellationToken, o, e));

        var emptyTemplates = Path.Combine(_tmp, "explicit-empty");
        Directory.CreateDirectory(emptyTemplates);

        // An explicit --templates wins even when a populated cache exists.
        var resolved = ScanCommand.ResolveCacheTemplatesDir(new ScanOptions
        {
            Target = "http://example.test/",
            Templates = emptyTemplates,
            VulnDbCacheRoot = dest,
        });

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveCacheTemplatesDir_null_when_cache_empty()
    {
        var resolved = ScanCommand.ResolveCacheTemplatesDir(new ScanOptions
        {
            Target = "http://example.test/",
            VulnDbCacheRoot = Path.Combine(_tmp, "empty-cache"),
        });

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveCacheTemplatesDir_null_when_single_template_given()
    {
        var dest = Path.Combine(_tmp, "scan-cache-template");
        Capture((o, e) => VulnDbUpdateCommand.RunAsync(
            new VulnDbUpdateOptions { Source = MakeSourceDir(), Dest = dest }, TestContext.Current.CancellationToken, o, e));

        // A single explicit --template also suppresses the cache fallback.
        var resolved = ScanCommand.ResolveCacheTemplatesDir(new ScanOptions
        {
            Target = "http://example.test/",
            Template = Path.Combine(dest, "templates", "rest", "teapot.json"),
            VulnDbCacheRoot = dest,
        });

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData("nuclei")]
    [InlineData("suite")]
    public void ResolveCacheTemplatesDir_null_when_other_source_given(string kind)
    {
        var dest = Path.Combine(_tmp, "scan-cache-" + kind);
        Capture((o, e) => VulnDbUpdateCommand.RunAsync(
            new VulnDbUpdateOptions { Source = MakeSourceDir(), Dest = dest }, TestContext.Current.CancellationToken, o, e));

        var options = kind == "nuclei"
            ? new ScanOptions { Target = "http://example.test/", Nuclei = _tmp, VulnDbCacheRoot = dest }
            : new ScanOptions { Target = "http://example.test/", Suite = "owasp-api", VulnDbCacheRoot = dest };

        Assert.Null(ScanCommand.ResolveCacheTemplatesDir(options));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(route(request));
    }
}

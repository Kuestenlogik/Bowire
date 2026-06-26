// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Integration coverage for <c>BowireWorkspaceEndpoints</c> (#194 / #242).
/// The endpoint reads / writes <c>.bww</c> from
/// <see cref="Directory.GetCurrentDirectory()"/>, so each test pins the
/// process cwd to a per-test scratch directory. The folder-open
/// endpoints rely on <see cref="BowireUserContext"/>, which is swapped
/// to a temp store for the duration of the test.
/// </summary>
[Collection("BowireUserContext")]
public sealed class BowireWorkspaceEndpointTests : IDisposable
{
    private readonly IBowireUserStore _originalStore;
    private readonly string _originalCwd;
    private readonly string _scratchDir;

    public BowireWorkspaceEndpointTests()
    {
        _originalStore = BowireUserContext.Current;
        _originalCwd = Directory.GetCurrentDirectory();
        _scratchDir = Path.Combine(Path.GetTempPath(), $"bowire-workspace-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
        Directory.SetCurrentDirectory(_scratchDir);
        BowireUserContext.Current = new TempStore(_scratchDir);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCwd);
        BowireUserContext.Current = _originalStore;
        try { Directory.Delete(_scratchDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    // ----- GET /api/workspace ----------------------------------------

    [Fact]
    public async Task GET_returns_empty_defaults_when_no_bww_file()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/workspace", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("urls").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("urls").GetArrayLength());
    }

    [Fact]
    public async Task GET_returns_persisted_workspace()
    {
        // Seed the disk file directly so we exercise the read path.
        var bwwPath = Path.Combine(_scratchDir, ".bww");
        await File.WriteAllTextAsync(bwwPath, """
        {
          "workspaceFormatVersion": 1,
          "urls": ["https://api.example.com"],
          "globals": { "env": "staging" }
        }
        """, TestContext.Current.CancellationToken);

        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/workspace", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("https://api.example.com",
            doc.RootElement.GetProperty("urls")[0].GetString());
        Assert.Equal("staging",
            doc.RootElement.GetProperty("globals").GetProperty("env").GetString());
    }

    [Fact]
    public async Task GET_recovers_from_corrupt_bww()
    {
        // Corrupt JSON on disk must surface as empty defaults rather
        // than a 500 — the workbench can re-save over it.
        var bwwPath = Path.Combine(_scratchDir, ".bww");
        await File.WriteAllTextAsync(bwwPath, "{ broken", TestContext.Current.CancellationToken);

        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var resp = await client.GetAsync(
            new Uri("/api/workspace", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(0, doc.RootElement.GetProperty("urls").GetArrayLength());
    }

    // ----- PUT /api/workspace ----------------------------------------

    [Fact]
    public async Task PUT_round_trips_workspace_file()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        var urlsList = new List<string> { "https://round-trip.example.com" };
        using var put = await client.PutAsJsonAsync(
            new Uri("/api/workspace", UriKind.Relative),
            new
            {
                workspaceFormatVersion = 1,
                urls = urlsList,
                globals = new Dictionary<string, string> { ["env"] = "dev" },
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var putBody = await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var putDoc = JsonDocument.Parse(putBody);
        Assert.True(putDoc.RootElement.GetProperty("saved").GetBoolean());

        // File now exists at <cwd>/.bww.
        Assert.True(File.Exists(Path.Combine(_scratchDir, ".bww")));

        using var get = await client.GetAsync(
            new Uri("/api/workspace", UriKind.Relative),
            TestContext.Current.CancellationToken);
        var body = await get.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("https://round-trip.example.com",
            doc.RootElement.GetProperty("urls")[0].GetString());
    }

    [Fact]
    public async Task PUT_invalid_json_returns_400_problem_details()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();

        using var content = new StringContent("{ broken", Encoding.UTF8, "application/json");
        using var resp = await client.PutAsync(
            new Uri("/api/workspace", UriKind.Relative),
            content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("urn:bowire:workspace:save-failed",
            doc.RootElement.GetProperty("type").GetString());
    }

    // ----- GET /api/workspace/can-open-folder ------------------------

    [Fact]
    public async Task CanOpenFolder_returns_embedded_reason_in_embedded_mode()
    {
        using var host = await BuildHost(BowireMode.Embedded);
        var client = host.GetTestClient();

        var payload = await client.GetFromJsonAsync<JsonElement>(
            "/api/workspace/can-open-folder",
            TestContext.Current.CancellationToken);

        Assert.False(payload.GetProperty("available").GetBoolean());
        Assert.Equal("embedded", payload.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task CanOpenFolder_returns_available_in_standalone_mode()
    {
        using var host = await BuildHost(BowireMode.Standalone);
        var client = host.GetTestClient();

        var payload = await client.GetFromJsonAsync<JsonElement>(
            "/api/workspace/can-open-folder",
            TestContext.Current.CancellationToken);

        Assert.True(payload.GetProperty("available").GetBoolean());
    }

    // ----- POST /api/workspace/open-folder ---------------------------

    [Fact]
    public async Task OpenFolder_returns_403_problem_in_embedded_mode()
    {
        using var host = await BuildHost(BowireMode.Embedded);
        var client = host.GetTestClient();

        using var emptyBody = new StringContent(string.Empty);
        using var resp = await client.PostAsync(
            new Uri("/api/workspace/open-folder", UriKind.Relative),
            emptyBody, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("urn:bowire:workspace:open-folder-not-available",
            doc.RootElement.GetProperty("type").GetString());
        Assert.Contains("standalone",
            doc.RootElement.GetProperty("title").GetString() ?? "",
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenFolder_returns_500_when_resolved_path_fails_regex_guard()
    {
        // The regex sink-guard rejects any resolved path containing
        // characters outside [A-Za-z0-9_\-./\\:] — a temp root with a
        // space passes Directory.CreateDirectory but trips the
        // SafeResolvedPathPattern check, falling into the
        // open-folder-failed problem-details branch instead of reaching
        // Process.Start. Exercises the IsMatch === false catch path
        // without ever launching explorer.exe.
        var spacedRoot = Path.Combine(Path.GetTempPath(), $"bowire workspace test {Guid.NewGuid():N}");
        Directory.CreateDirectory(spacedRoot);
        var previousStore = BowireUserContext.Current;
        BowireUserContext.Current = new TempStore(spacedRoot);
        try
        {
            using var host = await BuildHost(BowireMode.Standalone);
            var client = host.GetTestClient();

            using var emptyBody = new StringContent(string.Empty);
            using var resp = await client.PostAsync(
                new Uri("/api/workspace/open-folder", UriKind.Relative),
                emptyBody, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("urn:bowire:workspace:open-folder-failed",
                doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("InvalidOperationException",
                doc.RootElement.GetProperty("exceptionType").GetString());
        }
        finally
        {
            BowireUserContext.Current = previousStore;
            try { Directory.Delete(spacedRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task OpenFolder_returns_500_when_workspaceId_resolves_outside_user_root()
    {
        // Two-root store: userRoot=A, target=B/<sanitised-id>. The
        // resolved.StartsWith(userRoot) check in LaunchPlatformFileManager
        // fails, throws InvalidOperationException, surfaces as 500.
        // Both roots also have spaces so even if the prefix check passed
        // the regex would still trip — we never call Process.Start.
        var rootA = Path.Combine(Path.GetTempPath(), $"bowire wsroot a {Guid.NewGuid():N}");
        var rootB = Path.Combine(Path.GetTempPath(), $"bowire wsroot b {Guid.NewGuid():N}");
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
        var previousStore = BowireUserContext.Current;
        BowireUserContext.Current = new SplitRootStore(emptyRoot: rootA, otherRoot: rootB);
        try
        {
            using var host = await BuildHost(BowireMode.Standalone);
            var client = host.GetTestClient();

            using var emptyBody = new StringContent(string.Empty);
            using var resp = await client.PostAsync(
                new Uri("/api/workspace/open-folder?workspaceId=ws-1", UriKind.Relative),
                emptyBody, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("urn:bowire:workspace:open-folder-failed",
                doc.RootElement.GetProperty("type").GetString());
        }
        finally
        {
            BowireUserContext.Current = previousStore;
            try { Directory.Delete(rootA, recursive: true); } catch { /* best-effort */ }
            try { Directory.Delete(rootB, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task OpenFolder_returns_500_when_workspaceId_sanitises_to_empty()
    {
        // workspaceId composed entirely of disallowed chars sanitises
        // to "" → endpoint takes the fall-back BowireUserContext.GetUserPath("")
        // branch. We use a spaced root so the regex trip stops the
        // explorer launch.
        var spacedRoot = Path.Combine(Path.GetTempPath(), $"bowire empty ws {Guid.NewGuid():N}");
        Directory.CreateDirectory(spacedRoot);
        var previousStore = BowireUserContext.Current;
        BowireUserContext.Current = new TempStore(spacedRoot);
        try
        {
            using var host = await BuildHost(BowireMode.Standalone);
            var client = host.GetTestClient();

            using var emptyBody = new StringContent(string.Empty);
            // "..." sanitises to empty (every char rejected).
            using var resp = await client.PostAsync(
                new Uri("/api/workspace/open-folder?workspaceId=...", UriKind.Relative),
                emptyBody, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        }
        finally
        {
            BowireUserContext.Current = previousStore;
            try { Directory.Delete(spacedRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ----- Host builders ---------------------------------------------

    private async Task<IHost> BuildHost(BowireMode mode = BowireMode.Standalone)
    {
        var capturedScratch = _scratchDir;
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .UseContentRoot(capturedScratch)
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireWorkspaceEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s =>
                   {
                       s.AddRouting();
                       s.Configure<BowireOptions>(o => o.Mode = mode);
                   });
            })
            .Build();
        await host.StartAsync();
        return host;
    }

    private sealed class TempStore(string root) : IBowireUserStore
    {
        public string GetUserPath(string filename) => Path.Combine(root, filename);
    }

    /// <summary>
    /// Two-root store for the "resolved path escapes user root" branch
    /// exercise. <c>GetUserPath("")</c> returns one root and
    /// <c>GetUserPath(other-input)</c> returns another so the resolved
    /// path won't start with the empty-input root and the
    /// LaunchPlatformFileManager guard trips.
    /// </summary>
    private sealed class SplitRootStore(string emptyRoot, string otherRoot) : IBowireUserStore
    {
        public string GetUserPath(string filename) =>
            string.IsNullOrEmpty(filename)
                ? emptyRoot
                : Path.Combine(otherRoot, filename);
    }
}

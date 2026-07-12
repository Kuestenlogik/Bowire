// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Endpoints;

/// <summary>
/// Integration coverage for <see cref="BowireWorkspaceEndpoints"/> — the .bww
/// load/save round-trip plus the standalone-only folder-open / disk-purge gates.
/// The .bww file lives next to <c>Directory.GetCurrentDirectory()</c>, so the
/// host helper flips CWD to a per-test temp dir; joins CwdSerialised for that
/// process-global mutation.
/// </summary>
[Collection("CwdSerialised")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope — app + client disposed by the caller.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic.")]
public sealed class BowireWorkspaceEndpointsTests
{
    private static readonly Uri WorkspaceUri = new("/api/workspace", UriKind.Relative);
    private static readonly Uri CanOpenUri = new("/api/workspace/can-open-folder", UriKind.Relative);

    private sealed record Host(WebApplication App, HttpClient Http, string TempDir, string PrevCwd) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.DisposeAsync().ConfigureAwait(false);
            Directory.SetCurrentDirectory(PrevCwd);
            try { Directory.Delete(TempDir, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    private static async Task<Host> StartAsync(CancellationToken ct, BowireMode mode = BowireMode.Standalone)
    {
        var prevCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), "bowire-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Directory.SetCurrentDirectory(tempDir);

        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        b.Services.Configure<BowireOptions>(o => o.Mode = mode);
        var app = b.Build();
        app.MapBowireWorkspaceEndpoints("");
        await app.StartAsync(ct).ConfigureAwait(false);
        return new Host(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, tempDir, prevCwd);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
    private static async Task<JsonElement> ReadJson(HttpResponseMessage r, CancellationToken ct) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.Clone();

    // ------------------------------- GET / PUT -------------------------------

    [Fact]
    public async Task Get_returns_empty_default_when_no_file()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        using var resp = await h.Http.GetAsync(WorkspaceUri, ct);
        resp.EnsureSuccessStatusCode();
        var body = await ReadJson(resp, ct);
        Assert.Equal(1, body.GetProperty("workspaceFormatVersion").GetInt32());
        Assert.Equal(0, body.GetProperty("urls").GetArrayLength());
    }

    [Fact]
    public async Task Put_then_get_round_trips_the_workspace()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);

        using (var put = await h.Http.PutAsync(WorkspaceUri, Json("""
            { "workspaceFormatVersion": 1, "urls": ["https://api.example.com"], "globals": { "k": "v" } }
            """), ct))
        {
            put.EnsureSuccessStatusCode();
            Assert.True((await ReadJson(put, ct)).GetProperty("saved").GetBoolean());
        }

        using var get = await h.Http.GetAsync(WorkspaceUri, ct);
        var body = await ReadJson(get, ct);
        Assert.Equal("https://api.example.com", body.GetProperty("urls")[0].GetString());
        Assert.Equal("v", body.GetProperty("globals").GetProperty("k").GetString());
    }

    [Fact]
    public async Task Put_malformed_json_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        using var resp = await h.Http.PutAsync(WorkspaceUri, Json("{ not json"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Get_corrupt_file_falls_back_to_empty_default()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        await File.WriteAllTextAsync(Path.Combine(h.TempDir, ".bww"), "{ this is not valid json", ct);

        using var resp = await h.Http.GetAsync(WorkspaceUri, ct);
        resp.EnsureSuccessStatusCode();
        Assert.Equal(0, (await ReadJson(resp, ct)).GetProperty("urls").GetArrayLength());
    }

    // ------------------------------- capability + gates -------------------------------

    [Fact]
    public async Task CanOpenFolder_is_available_in_standalone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct, BowireMode.Standalone);
        using var resp = await h.Http.GetAsync(CanOpenUri, ct);
        resp.EnsureSuccessStatusCode();
        Assert.True((await ReadJson(resp, ct)).GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task CanOpenFolder_is_unavailable_when_embedded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct, BowireMode.Embedded);
        using var resp = await h.Http.GetAsync(CanOpenUri, ct);
        resp.EnsureSuccessStatusCode();
        var body = await ReadJson(resp, ct);
        Assert.False(body.GetProperty("available").GetBoolean());
        Assert.Equal("embedded", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Purge_is_forbidden_when_embedded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct, BowireMode.Embedded);
        using var resp = await h.Http.DeleteAsync(new Uri("/api/workspace/ws123", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Purge_bad_id_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct, BowireMode.Standalone);
        // "@@@" sanitises to empty (all non-alnum stripped) → refuse.
        using var resp = await h.Http.DeleteAsync(new Uri("/api/workspace/@@@", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task OpenFolder_is_forbidden_when_embedded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct, BowireMode.Embedded);
        using var resp = await h.Http.PostAsync(new Uri("/api/workspace/open-folder", UriKind.Relative), content: null, ct);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}

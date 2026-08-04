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
/// Integration coverage for <see cref="BowireSchemaChangeEndpoints"/> (#185) —
/// the append / hydrate / mark-read surface behind the workbench's
/// schema-change pill. The DI-registered store is pinned to a per-test
/// temp file, so nothing reads or writes the developer's real
/// <c>~/.bowire</c> tree and no test collection is needed.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope — app + client disposed by the caller.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic.")]
public sealed class BowireSchemaChangeEndpointsTests
{
    private static readonly Uri ChangesUri = new("/api/schema-changes?workspaceId=ws1", UriKind.Relative);
    private static readonly Uri ReadUri = new("/api/schema-changes/read?workspaceId=ws1", UriKind.Relative);

    private sealed record Host(WebApplication App, HttpClient Http, string TempDir) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await App.DisposeAsync().ConfigureAwait(false);
            try { Directory.Delete(TempDir, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    private static async Task<Host> StartAsync(CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bowire-schemachange-ep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        b.Services.AddSingleton<SchemaChangeLogStore>();
        var app = b.Build();
        app.Services.GetRequiredService<SchemaChangeLogStore>()
            .OverrideStorePathForTesting(Path.Combine(tempDir, "log.json"));
        app.MapBowireSchemaChangeEndpoints("");
        await app.StartAsync(ct).ConfigureAwait(false);
        return new Host(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, tempDir);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
    private static async Task<JsonElement> ReadJson(HttpResponseMessage r, CancellationToken ct) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.Clone();

    [Fact]
    public async Task Get_returns_the_empty_envelope_when_nothing_was_logged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        using var resp = await h.Http.GetAsync(ChangesUri, ct);
        resp.EnsureSuccessStatusCode();
        var body = await ReadJson(resp, ct);
        Assert.Equal(0, body.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Post_then_get_round_trips_the_change_entries()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);

        using (var post = await h.Http.PostAsync(ChangesUri, Json("""
            { "entries": [
                { "at": "2026-08-04T10:00:00Z", "type": "added", "service": "Orders", "method": "Orders/Cancel" },
                { "at": "2026-08-04T10:00:00Z", "type": "signature", "service": "Orders",
                  "method": "GET /orders", "detail": "request shape changed" }
            ] }
            """), ct))
        {
            post.EnsureSuccessStatusCode();
            Assert.Equal(2, (await ReadJson(post, ct)).GetProperty("entries").GetArrayLength());
        }

        using var get = await h.Http.GetAsync(ChangesUri, ct);
        var body = await ReadJson(get, ct);
        Assert.Equal(2, body.GetProperty("entries").GetArrayLength());
        Assert.Equal("Orders/Cancel", body.GetProperty("entries")[0].GetProperty("method").GetString());
        Assert.Equal("request shape changed", body.GetProperty("entries")[1].GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Post_an_unknown_change_type_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        using var resp = await h.Http.PostAsync(ChangesUri, Json("""
            { "entries": [ { "type": "exploded", "service": "Orders" } ] }
            """), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_without_entries_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        using var resp = await h.Http.PostAsync(ChangesUri, Json("{}"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_malformed_json_is_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        using var resp = await h.Http.PostAsync(ChangesUri, Json("{ not json"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_a_null_array_element_is_400_not_500()
    {
        // System.Text.Json materialises a JSON null element as a null
        // entry — must land on the ArgumentException → 400 path.
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        using var resp = await h.Http.PostAsync(ChangesUri, Json("""
            { "entries": [ null ] }
            """), ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_with_a_non_json_content_type_is_415_not_500()
    {
        // ReadFromJsonAsync throws InvalidOperationException (not
        // JsonException) here — the up-front gate keeps garbage a 4xx.
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        using var content = new StringContent("""{ "entries": [] }""", Encoding.UTF8, "text/plain");
        using var resp = await h.Http.PostAsync(ChangesUri, content, ct);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, resp.StatusCode);
    }

    [Fact]
    public async Task Post_stamps_entries_with_the_server_clock()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);
        using var post = await h.Http.PostAsync(ChangesUri, Json("""
            { "entries": [ { "at": "2030-01-01T00:00:00Z", "type": "added", "service": "Orders" } ] }
            """), ct);
        post.EnsureSuccessStatusCode();
        var at = DateTimeOffset.Parse(
            (await ReadJson(post, ct)).GetProperty("entries")[0].GetProperty("at").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(at <= DateTimeOffset.UtcNow.AddMinutes(1),
            "the client's future-dated stamp must not survive the append");
    }

    [Fact]
    public async Task Mark_read_moves_the_watermark_past_every_current_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await StartAsync(ct);

        using (var post = await h.Http.PostAsync(ChangesUri, Json("""
            { "entries": [ { "type": "removed", "service": "Legacy" } ] }
            """), ct))
        {
            post.EnsureSuccessStatusCode();
        }

        using (var read = await h.Http.PostAsync(ReadUri, content: null, ct))
        {
            read.EnsureSuccessStatusCode();
            var body = await ReadJson(read, ct);
            var lastReadAt = DateTimeOffset.Parse(
                body.GetProperty("lastReadAt").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);
            var entryAt = DateTimeOffset.Parse(
                body.GetProperty("entries")[0].GetProperty("at").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(lastReadAt >= entryAt, "everything logged before the read must count as read");
        }

        using var get = await h.Http.GetAsync(ChangesUri, ct);
        Assert.True((await ReadJson(get, ct)).TryGetProperty("lastReadAt", out _),
            "the watermark must persist");
    }
}

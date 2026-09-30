// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Protocol.WebSocket.Tests;

/// <summary>
/// #681 — the environment's cookie jar reaches the WebSocket upgrade, where
/// session auth for a socket API is decided; and a cookie the handshake sets
/// lands in the jar.
/// </summary>
public sealed class WebSocketCookieJarTests : IDisposable
{
    private readonly IBowireUserStore _originalStore;
    private readonly string _root;

    public WebSocketCookieJarTests()
    {
        _originalStore = BowireUserContext.Current;
        _root = Path.Combine(Path.GetTempPath(), "bowire-ws-cookies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        BowireUserContext.Current = new TempUserStore(_root);
    }

    public void Dispose()
    {
        BowireUserContext.Current = _originalStore;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task The_Upgrade_Carries_The_Jar_And_Keeps_What_The_Handshake_Sets()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseWebSockets();
        string? seenCookie = null;
        app.Map("/ws", async (HttpContext ctx) =>
        {
            seenCookie = ctx.Request.Headers.Cookie.ToString();
            ctx.Response.Headers.SetCookie = $"refreshed=yes; Path=/; Expires={DateTime.UtcNow.AddHours(1):R}";
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", ctx.RequestAborted);
        });
        await app.StartAsync(ct);

        var envId = "ws-env-" + Guid.NewGuid().ToString("N")[..6];
        var origin = new Uri(app.Urls.First());
        CookieJar.For(envId).Set(new CookieSnapshot(origin.Host, "/", "session", "s3cret", DateTime.Now.AddHours(1), false, true));

        var wsUri = new UriBuilder(origin) { Scheme = "ws", Path = "/ws" }.Uri;
        await using (var channel = await WebSocketBowireChannel.CreateAsync(
            wsUri, new Dictionary<string, string> { [CookieJar.MarkerKey] = envId }, null, ct))
        {
            Assert.NotNull(channel);
        }

        Assert.Contains("session=s3cret", seenCookie, StringComparison.Ordinal);
        Assert.Contains(CookieJar.Snapshot(envId), c => c.Name == "refreshed" && c.Value == "yes");
    }

    private sealed class TempUserStore(string root) : IBowireUserStore
    {
        public string GetUserPath(string filename) => Path.Combine(root, filename);
    }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Net;
using Kuestenlogik.Bowire.Plugins;
using Kuestenlogik.Bowire.Tests.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Auth;

/// <summary>
/// The cookie manager (#681): a login's cookie is replayed and survives a
/// restart, session cookies do not, and RFC 6265 scoping — domain, path,
/// Secure, expiry — holds. Values stay out of HAR exports by default.
/// </summary>
[Collection(BowireNetworkCollectionDefinition.Name)]
public sealed class CookieJarTests : IDisposable
{
    private readonly IBowireUserStore _originalStore;
    private readonly string _root;

    public CookieJarTests()
    {
        _originalStore = BowireUserContext.Current;
        _root = Path.Combine(Path.GetTempPath(), "bowire-cookies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        BowireUserContext.Current = new TempUserStore(_root);
        CookieJar.ResetForTests();
    }

    public void Dispose()
    {
        CookieJar.ResetForTests();
        BowireUserContext.Current = _originalStore;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Uri Api = new("https://api.example.test/login");

    private static string Persistent(string name, string value, string extra = "") =>
        $"{name}={value}; Path=/; Expires={DateTime.UtcNow.AddDays(1):R}{extra}";

    [Fact]
    public void A_Persistent_Cookie_Survives_A_Restart_And_A_Session_Cookie_Does_Not()
    {
        CookieJar.For("staging").Accept(Api, [Persistent("sid", "abc", "; Secure; HttpOnly; SameSite=Lax"), "tmp=1; Path=/"]);

        CookieJar.ResetForTests();   // a new process
        var reloaded = CookieJar.For("staging").Snapshot();

        var sid = Assert.Single(reloaded);
        Assert.Equal("sid", sid.Name);
        Assert.Equal("abc", sid.Value);
        Assert.True(sid.Secure);
        Assert.True(sid.HttpOnly);
        Assert.Equal("Lax", sid.SameSite);
        Assert.False(sid.Session);
    }

    [Fact]
    public void Each_Workspace_And_Environment_Has_Its_Own_Jar()
    {
        CookieJar.For("staging").Accept(Api, [Persistent("sid", "staging")]);
        CookieJar.For("prod").Accept(Api, [Persistent("sid", "prod")]);
        using (BowirePluginSettingsScope.Enter("ws_other"))
        {
            Assert.Empty(CookieJar.For("staging").Snapshot());
        }
        Assert.Equal("staging", Assert.Single(CookieJar.For("staging").Snapshot()).Value);
        Assert.Equal("prod", Assert.Single(CookieJar.For("prod").Snapshot()).Value);
    }

    [Fact]
    public void Scoping_Follows_Rfc6265()
    {
        var jar = CookieJar.For("scoping");
        jar.Accept(new Uri("https://api.example.test/"), [
            Persistent("secure", "1", "; Secure"),
            "scoped=1; Path=/admin",
            $"gone=1; Path=/; Expires={DateTime.UtcNow.AddDays(-1):R}",
        ]);

        Assert.Contains("secure=1", jar.CookieHeaderFor(new Uri("https://api.example.test/")), StringComparison.Ordinal);
        Assert.DoesNotContain("secure=1", jar.CookieHeaderFor(new Uri("http://api.example.test/")), StringComparison.Ordinal);
        Assert.Contains("scoped=1", jar.CookieHeaderFor(new Uri("https://api.example.test/admin/users")), StringComparison.Ordinal);
        Assert.DoesNotContain("scoped=1", jar.CookieHeaderFor(new Uri("https://api.example.test/public")), StringComparison.Ordinal);
        Assert.DoesNotContain("gone", jar.CookieHeaderFor(new Uri("https://api.example.test/")), StringComparison.Ordinal);
        Assert.Equal(string.Empty, jar.CookieHeaderFor(new Uri("https://other.example.test/")));
        Assert.DoesNotContain(jar.Snapshot(), c => c.Name == "gone");
    }

    [Fact]
    public void A_Cookie_For_Another_Domain_Is_Rejected()
    {
        var jar = CookieJar.For("cross");
        jar.Accept(new Uri("https://api.example.test/"), ["evil=1; Domain=bank.test; Path=/"]);
        Assert.Empty(jar.Snapshot());
    }

    [Fact]
    public void The_Manager_Adds_Edits_Removes_And_Clears()
    {
        var jar = CookieJar.For("manager");
        jar.Set(new CookieSnapshot("api.example.test", "/", "a", "1", DateTime.Now.AddDays(1), true, false) { SameSite = "Strict" });
        jar.Set(new CookieSnapshot("api.example.test", "/", "b", "2", DateTime.MinValue, false, false) { Session = true });
        jar.Set(new CookieSnapshot("other.test", "/", "c", "3", DateTime.Now.AddDays(1), false, false));

        jar.Set(new CookieSnapshot("api.example.test", "/", "a", "edited", DateTime.Now.AddDays(1), true, false) { SameSite = "Strict" });
        Assert.Equal("edited", jar.Snapshot().Single(c => c.Name == "a").Value);
        Assert.Equal("Strict", jar.Snapshot().Single(c => c.Name == "a").SameSite);

        Assert.True(jar.Remove("api.example.test", "/", "b"));
        Assert.DoesNotContain(jar.Snapshot(), c => c.Name == "b");

        Assert.Equal(1, jar.ClearDomain("other.test"));
        Assert.Equal(["a"], jar.Snapshot().Select(c => c.Name));

        Assert.Equal(1, jar.Clear());
        Assert.Empty(jar.Snapshot());
        CookieJar.ResetForTests();
        Assert.Empty(CookieJar.For("manager").Snapshot());
    }

    [Fact]
    public async Task A_Login_Cookie_Is_Replayed_On_The_Next_Call_And_Only_With_The_Jar()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.MapPost("/login", (HttpContext ctx) =>
        {
            ctx.Response.Headers.SetCookie = $"session=s3cret; Path=/; HttpOnly; SameSite=Strict; Expires={DateTime.UtcNow.AddHours(1):R}";
            return "ok";
        });
        app.MapGet("/me", (HttpContext ctx) => ctx.Request.Cookies.TryGetValue("session", out var s) ? s : "anonymous");
        await app.StartAsync(Ct);
        var baseUrl = app.Urls.First();
        var marker = new Dictionary<string, string> { [CookieJar.MarkerKey] = "staging" };

        using var client = BowireHttpClientFactory.Create(null, "rest", TimeSpan.FromSeconds(10));
        using (var login = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/login"))
        {
            BowireHttpAuth.Attach(login, marker);
            using var _ = await client.SendAsync(login, Ct);
        }
        using (var me = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/me"))
        {
            BowireHttpAuth.Attach(me, marker);
            using var response = await client.SendAsync(me, Ct);
            Assert.Equal("s3cret", await response.Content.ReadAsStringAsync(Ct));
        }

        // The same shared client without the marker sends no cookie: the
        // factory's handlers keep none of their own.
        using (var anonymous = await client.GetAsync(new Uri(baseUrl + "/me"), Ct))
        {
            Assert.Equal("anonymous", await anonymous.Content.ReadAsStringAsync(Ct));
        }

        // Visible in the manager, and after a restart.
        CookieJar.ResetForTests();
        var stored = Assert.Single(CookieJar.Snapshot("staging"));
        Assert.Equal("session", stored.Name);
        Assert.Equal("Strict", stored.SameSite);
    }

    [Fact]
    public void Har_Export_Redacts_Cookie_Values_Unless_Told_Otherwise()
    {
        var recording = new BowireRecording
        {
            Steps =
            [
                new BowireRecordingStep
                {
                    Protocol = "rest", Service = "Api", Method = "Me", HttpVerb = "GET", HttpPath = "/me",
                    Metadata = new Dictionary<string, string> { ["Cookie"] = "session=s3cret", ["X-Trace"] = "1" },
                    ResponseHeaders = new Dictionary<string, string> { ["Set-Cookie"] = "session=s3cret; Path=/" },
                },
            ],
        };

        var redacted = BowireHarConverter.ToHar(recording);
        Assert.DoesNotContain("s3cret", redacted, StringComparison.Ordinal);
        Assert.Contains(BowireHarConverter.RedactedPlaceholder, redacted, StringComparison.Ordinal);
        Assert.Contains("X-Trace", redacted, StringComparison.Ordinal);

        Assert.Contains("s3cret", BowireHarConverter.ToHar(recording, redactCookies: false), StringComparison.Ordinal);
    }

    private sealed class TempUserStore(string root) : IBowireUserStore
    {
        public string GetUserPath(string filename) => Path.Combine(root, filename);
    }
}

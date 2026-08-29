// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// One person looking at another person's workbench (#98).
/// </summary>
/// <remarks>
/// The whole design rests on one claim: the cookie says <em>whose</em> slot to
/// open and never <em>who may</em> open it, because the authority is
/// re-derived from the caller's own token on every request. These tests are
/// where that claim is either true or not.
/// </remarks>
public sealed class BowireImpersonationTests : IAsyncLifetime
{
    private const string Admin = "grace@example.com";
    private const string Ada = "ada@example.com";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-actas-" + Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private BowireAuditLog _audit = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _audit = new BowireAuditLog(_root);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        // A resolver in the container rather than a swapped global: the
        // tenancy reads DI first, so the test never touches process state.
        builder.Services.AddSingleton<IBowirePathResolver>(new FixedRoot(_root));
        builder.Services.AddBowireTenancy(new ConfigurationBuilder().Build(), o => o.Enabled = true);
        builder.Services.Replace(ServiceDescriptor.Singleton<IBowireUserDirectory>(
            new FakeDirectory([Admin], [Admin, Ada])));
        builder.Services.Replace(ServiceDescriptor.Singleton(_audit));

        _app = builder.Build();

        // Stands in for the auth provider: whoever the header names is the
        // authenticated caller.
        _app.Use(async (context, next) =>
        {
            var who = context.Request.Headers["X-Test-Subject"].ToString();
            if (!string.IsNullOrEmpty(who))
            {
                context.User = new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim("sub", who)], "test"));
            }
            await next(context);
        });

        _app.UseBowireTenancy();
        _app.MapBowireImpersonationEndpoints("");
        _app.MapGet("/whoami", () => Results.Ok(new
        {
            served = BowireTenancy.CurrentSubject,
            actor = BowireImpersonation.Current?.Actor,
        }));
        _app.MapPost("/change-something", () => Results.NoContent());

        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestServer().CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- plumbing ----

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private async Task<HttpResponseMessage> Send(
        HttpMethod method, string path, string? who, string? cookie = null, string? body = null)
    {
        using var request = new HttpRequestMessage(method, Url(path));
        if (who is not null) request.Headers.Add("X-Test-Subject", who);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        request.Content?.Dispose();
        return response;
    }

    private static string? SetCookie(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(BowireImpersonation.CookieName, StringComparison.Ordinal))
            : null;

    /// <summary>The cookie header a browser would send back after this response.</summary>
    private static string? CookieFrom(HttpResponseMessage response)
    {
        var header = SetCookie(response);
        if (header is null) return null;

        var pair = header.Split(';')[0];
        // A deletion carries an empty value and an expiry in the past; the
        // browser would send nothing at all.
        return pair.EndsWith('=') ? null : pair;
    }

    private async Task<(string? Served, string? Actor)> WhoAmI(string who, string? cookie = null)
    {
        var response = await Send(HttpMethod.Get, "/whoami", who, cookie);
        var body = JsonSerializer.Deserialize<JsonElement>(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (Value(body, "served"), Value(body, "actor"));
    }

    private static string? Value(JsonElement body, string name)
        => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private async Task<string> BeginAs(string admin, string target)
    {
        var response = await Send(HttpMethod.Post, "/api/impersonation", admin,
            body: $$"""{ "subject": "{{target}}" }""");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return CookieFrom(response)!;
    }

    // ---- who may start one ----

    [Fact]
    public async Task An_Ordinary_User_Cannot_Act_As_Anybody()
    {
        var response = await Send(HttpMethod.Post, "/api/impersonation", Ada,
            body: $$"""{ "subject": "{{Admin}}" }""");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(SetCookie(response));
    }

    [Fact]
    public async Task An_Unauthenticated_Caller_Cannot_Either()
    {
        var response = await Send(HttpMethod.Post, "/api/impersonation", who: null,
            body: $$"""{ "subject": "{{Ada}}" }""");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_Administrator_Can()
    {
        var response = await Send(HttpMethod.Post, "/api/impersonation", Admin,
            body: $$"""{ "subject": "{{Ada}}" }""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull(SetCookie(response));
    }

    [Fact]
    public async Task Somebody_The_Directory_Does_Not_Know_Is_Refused()
    {
        // Otherwise an administrator opens a slot for a subject nobody has,
        // which looks like an empty workbench rather than like a typo.
        var response = await Send(HttpMethod.Post, "/api/impersonation", Admin,
            body: """{ "subject": "nobody@example.com" }""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(SetCookie(response));
    }

    [Fact]
    public async Task Acting_As_Yourself_Just_Clears()
    {
        var response = await Send(HttpMethod.Post, "/api/impersonation", Admin,
            body: $$"""{ "subject": "{{Admin}}" }""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(CookieFrom(response));
    }

    [Fact]
    public async Task A_Body_That_Is_Not_Json_Is_A_400()
    {
        var response = await Send(HttpMethod.Post, "/api/impersonation", Admin, body: "{ not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- what the cookie does, and does not, do ----

    [Fact]
    public async Task With_The_Cookie_An_Administrator_Is_Served_The_Other_Slot()
    {
        var cookie = await BeginAs(Admin, Ada);

        var (served, actor) = await WhoAmI(Admin, cookie);

        Assert.Equal(Ada, served);
        Assert.Equal(Admin, actor);
    }

    [Fact]
    public async Task The_Same_Cookie_In_Somebody_Elses_Hands_Does_Nothing()
    {
        // The whole security argument. The cookie names a slot; the authority
        // comes from the caller's own token, re-checked on this request.
        var stolen = await BeginAs(Admin, Ada);

        var (served, actor) = await WhoAmI("mallory@example.com", stolen);

        Assert.Equal("mallory@example.com", served);
        Assert.Null(actor);
    }

    [Fact]
    public async Task A_Caller_Who_Is_Not_An_Administrator_Has_The_Cookie_Taken_Away()
    {
        // Rather than ignored: the workbench must stop showing a banner for
        // something that is not happening.
        var stolen = await BeginAs(Admin, Ada);

        var response = await Send(HttpMethod.Get, "/whoami", Ada, stolen);

        Assert.Null(CookieFrom(response));
    }

    [Fact]
    public async Task Without_The_Cookie_An_Administrator_Is_Themselves()
    {
        await BeginAs(Admin, Ada);

        var (served, actor) = await WhoAmI(Admin);

        Assert.Equal(Admin, served);
        Assert.Null(actor);
    }

    [Fact]
    public async Task Ending_It_Puts_Them_Back()
    {
        var cookie = await BeginAs(Admin, Ada);

        var ended = await Send(HttpMethod.Delete, "/api/impersonation", Admin, cookie);
        var (served, _) = await WhoAmI(Admin);

        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        Assert.Null(CookieFrom(ended));
        Assert.Equal(Admin, served);
    }

    [Fact]
    public async Task Ending_It_Works_Even_For_Somebody_Who_Is_No_Longer_An_Administrator()
    {
        // Not admin-gated on purpose: an administrator who lost the role
        // mid-session must still be able to get back to their own workbench.
        var response = await Send(HttpMethod.Delete, "/api/impersonation", Ada);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ---- the picker's list ----

    [Fact]
    public async Task Only_An_Administrator_Can_See_Who_Else_Exists()
    {
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(HttpMethod.Get, "/api/users", Ada)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await Send(HttpMethod.Get, "/api/users", Admin)).StatusCode);
    }

    [Fact]
    public async Task An_Administrator_In_A_Session_Is_Still_An_Administrator()
    {
        // While impersonating, the tenancy scope names the target. A check
        // written against it would decide an administrator acting as an
        // ordinary user is an ordinary user — and they could not get out.
        var cookie = await BeginAs(Admin, Ada);

        var response = await Send(HttpMethod.Get, "/api/users", Admin, cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- the record ----

    [Fact]
    public async Task Starting_And_Ending_Are_Both_Recorded_With_Both_Identities()
    {
        var cookie = await BeginAs(Admin, Ada);
        await Send(HttpMethod.Delete, "/api/impersonation", Admin, cookie);

        var lines = _audit.Lines();

        Assert.Equal(2, lines.Count);
        Assert.Contains("begin", lines[0], StringComparison.Ordinal);
        Assert.Contains("end", lines[1], StringComparison.Ordinal);
        Assert.All(lines, l =>
        {
            Assert.Contains(Admin, l, StringComparison.Ordinal);
            Assert.Contains(Ada, l, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_Change_Made_On_Somebody_Elses_Behalf_Is_Recorded()
    {
        var cookie = await BeginAs(Admin, Ada);

        await Send(HttpMethod.Post, "/change-something", Admin, cookie);

        var recorded = _audit.Lines().Single(l => l.Contains("/change-something", StringComparison.Ordinal));
        Assert.Contains("POST", recorded, StringComparison.Ordinal);
        Assert.Contains(Admin, recorded, StringComparison.Ordinal);
        Assert.Contains(Ada, recorded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_Is_Not_Recorded()
    {
        // Burying the handful of lines that matter under a day of navigation
        // is how an audit trail stops being readable.
        var cookie = await BeginAs(Admin, Ada);

        await WhoAmI(Admin, cookie);

        Assert.DoesNotContain(_audit.Lines(), l => l.Contains("/whoami", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_Is_Recorded_When_Nobody_Is_Impersonating()
    {
        await Send(HttpMethod.Post, "/change-something", Admin);

        Assert.Empty(_audit.Lines());
    }

    // ---- doubles ----

    private sealed class FixedRoot(string root) : IBowirePathResolver
    {
        public string Root(BowireStorageScope scope) => root;

        public string Resolve(BowireStorageScope scope, params string[] segments)
            => segments is null || segments.Length == 0 ? root : Path.Combine(root, Path.Combine(segments));
    }

    private sealed class FakeDirectory(HashSet<string> admins, string[] known) : IBowireUserDirectory
    {
        public BowireUserProfile Describe(ClaimsPrincipal? principal, string subject)
            => new() { Subject = subject, IsAdmin = admins.Contains(subject) };

        public IReadOnlyList<BowireUserProfile> Search(string? term, int limit)
            => known
                .Where(k => string.IsNullOrEmpty(term)
                    || k.Contains(term, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .Select(k => new BowireUserProfile { Subject = k, IsAdmin = admins.Contains(k) })
                .ToList();
    }
}

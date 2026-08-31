// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Projects;
using Microsoft.AspNetCore.Authorization;
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
/// Who may change the install itself (#636).
/// </summary>
/// <remarks>
/// <para>
/// Installing a plugin downloads a package and puts assemblies where the next
/// start loads them into the server process — which shares its own
/// <c>Kuestenlogik.Bowire*</c> and <c>System.*</c> identities with them by
/// design. On a shared install that is code execution as the server, and it
/// used to be reachable by the least-privileged identity on it.
/// </para>
/// <para>
/// The two halves that have to hold together: the laptop keeps working
/// untouched, and on an install with identities the authority comes from the
/// caller's own token rather than from anything they can set.
/// </para>
/// </remarks>
public sealed class BowireAdminGateTests
{
    private const string Admin = "grace@example.com";
    private const string Member = "ada@example.com";

    [Fact]
    public async Task WithNoAuthProvider_EverybodyMayAdminister()
    {
        // The laptop, and the embedded host that never configured identities.
        // There is one person, gating them protects nobody, and a check meant
        // for shared installs must not reach this case.
        await using var host = await Host(withAuthProvider: false);

        var response = await host.Get("/gated", who: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task WithAuthProvider_AnAdministratorMayAdminister()
    {
        await using var host = await Host(withAuthProvider: true);

        var response = await host.Get("/gated", who: Admin);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task WithAuthProvider_AnOrdinaryMemberIsRefused()
    {
        await using var host = await Host(withAuthProvider: true);

        var response = await host.Get("/gated", who: Member);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TheRefusalSaysWhatWasRefusedAndWhy()
    {
        // A bare 403 leaves the workbench nothing to render. The type is what
        // a client keys off; the title is what a person reads.
        await using var host = await Host(withAuthProvider: true);

        var response = await host.Get("/gated", who: Member);
        var body = JsonSerializer.Deserialize<JsonElement>(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal("urn:bowire:admin:not-admin", body.GetProperty("type").GetString());
        Assert.Contains("install a plugin", body.GetProperty("title").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithAuthProvider_AnUnauthenticatedCallerIsRefusedAsUnauthenticated()
    {
        // 401 rather than 403: there is nobody to deny, which is a different
        // thing from denying somebody, and only one of them is fixed by
        // signing in.
        await using var host = await Host(withAuthProvider: true);

        var response = await host.Get("/gated", who: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnAdministratorKeepsTheirAuthorityWhileActingAsSomebodyElse()
    {
        // The one that would be wrong the other way round. While impersonating
        // (#98) the tenancy scope names the target, so reading it here would
        // strip an administrator of the authority they are exercising — and
        // with it the ability to stop. A member cannot impersonate at all, so
        // reading the actor never grants anything either.
        await using var host = await Host(withAuthProvider: true);

        var response = await host.Get("/gated", who: Admin, actingAs: Member);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ---- plumbing ----

    private static async Task<GateHost> Host(bool withAuthProvider)
    {
        var root = Path.Combine(Path.GetTempPath(), "bowire-adminGate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<IBowirePathResolver>(new FixedRoot(root));
        // Tenancy and the provider travel together on purpose: a laptop has
        // neither, and building it with one but not the other would be a
        // shape no install has.
        if (withAuthProvider)
        {
            builder.Services.AddBowireTenancy(new ConfigurationBuilder().Build(), o => o.Enabled = true);
            builder.Services.Replace(ServiceDescriptor.Singleton<IBowireUserDirectory>(
                new FakeDirectory([Admin])));
            builder.Services.AddSingleton<IBowireAuthProvider>(new FakeAuthProvider());
        }

        var app = builder.Build();

        // Stands in for the provider's authentication: the header names the
        // authenticated caller.
        app.Use(async (context, next) =>
        {
            var who = context.Request.Headers["X-Test-Subject"].ToString();
            if (!string.IsNullOrEmpty(who))
            {
                context.User = new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim("sub", who)], "test"));
            }
            await next(context);
        });
        if (withAuthProvider) app.UseBowireTenancy();

        app.MapGet("/gated", (HttpContext http) =>
            BowireAdminGate.RequireAdministrator(http, "install a plugin") ?? Results.NoContent());

        await app.StartAsync(TestContext.Current.CancellationToken);
        return new GateHost(app, root);
    }

    private sealed class GateHost(WebApplication app, string root) : IAsyncDisposable
    {
        private readonly HttpClient _client = app.GetTestServer().CreateClient();

        public async Task<HttpResponseMessage> Get(string path, string? who, string? actingAs = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            if (who is not null) request.Headers.Add("X-Test-Subject", who);
            if (actingAs is not null)
            {
                request.Headers.Add("Cookie", $"{BowireImpersonation.CookieName}={actingAs}");
            }

            return await _client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await app.DisposeAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FakeAuthProvider : IBowireAuthProvider
    {
        public string Id => "test";

        public string Name => "Test provider";

        public void AddAuthentication(IServiceCollection services, IConfiguration configuration)
        {
            // The gate only asks whether a provider is registered; this host
            // authenticates through the middleware above.
        }

        public void BuildDefaultPolicy(AuthorizationPolicyBuilder policy)
        {
            // Left open on purpose: these tests are about the admin gate, not
            // about the authorization policy #625 applies around it.
        }
    }

    private sealed class FixedRoot(string root) : IBowirePathResolver
    {
        public string Root(BowireStorageScope scope) => root;

        public string Resolve(BowireStorageScope scope, params string[] segments)
            => segments is null || segments.Length == 0 ? root : Path.Combine(root, Path.Combine(segments));
    }

    private sealed class FakeDirectory(HashSet<string> admins) : IBowireUserDirectory
    {
        public BowireUserProfile Describe(ClaimsPrincipal? principal, string subject)
            => new() { Subject = subject, IsAdmin = admins.Contains(subject) };

        public IReadOnlyList<BowireUserProfile> Search(string? term, int limit)
            => admins
                .Select(a => new BowireUserProfile { Subject = a, IsAdmin = true })
                .Take(limit)
                .ToList();
    }
}

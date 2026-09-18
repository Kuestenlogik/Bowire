// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Security.Claims;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Scim.Tests;

/// <summary>
/// How SCIM attaches to a host: what <c>AddBowireScim</c> registers,
/// what <c>MapBowireScim</c> will and will not serve, and who
/// <c>UseBowireScim</c> turns away.
/// </summary>
/// <remarks>
/// This file was the module's thinnest at 21% covered, and it is the part
/// an operator's configuration actually reaches. The branches here decide
/// whether a provisioning endpoint is served open, and whether a
/// deprovisioned identity can still use the install — neither is a thing
/// to leave to inspection.
/// </remarks>
public sealed class BowireScimExtensionsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-scim-ext-" + Guid.NewGuid().ToString("N"));
    private readonly IBowirePathResolver _previous = BowirePaths.Current;

    public BowireScimExtensionsTests()
    {
        Directory.CreateDirectory(_root);
        BowirePaths.Current = new BowirePathResolver(
            name => name == BowirePathResolver.DataDirVariable ? _root : null,
            () => _root);
    }

    public void Dispose()
    {
        BowirePaths.Current = _previous;
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static IConfiguration Config(params (string Key, string Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    // ---- AddBowireScim ----

    [Fact]
    public void Configuration_Is_Bound_From_The_Bowire_Scim_Section()
    {
        var services = new ServiceCollection();

        services.AddBowireScim(Config(
            ("Bowire:Scim:Enabled", "true"),
            ("Bowire:Scim:Token", "from-config")));

        var options = services.BuildServiceProvider().GetRequiredService<BowireScimOptions>();
        Assert.True(options.Enabled);
        Assert.Equal("from-config", options.Token);
    }

    [Fact]
    public void The_Callback_Wins_Over_The_Configuration_File()
    {
        // Documented precedence: a host that sets something in code means
        // it, whatever the file on disk says.
        var services = new ServiceCollection();

        services.AddBowireScim(
            Config(("Bowire:Scim:Token", "from-config")),
            o => o.Token = "from-code");

        Assert.Equal("from-code",
            services.BuildServiceProvider().GetRequiredService<BowireScimOptions>().Token);
    }

    [Fact]
    public void The_Provisioned_Directory_Replaces_The_Claims_Only_Default()
    {
        // #98 — whichever Add* call runs first, the directory that can
        // answer the role question has to be the one left standing.
        var services = new ServiceCollection();
        services.AddSingleton<IBowireUserDirectory>(new StubDirectory());

        services.AddBowireScim(Config(("Bowire:Scim:Token", "t")));

        var directory = services.BuildServiceProvider().GetRequiredService<IBowireUserDirectory>();
        Assert.IsNotType<StubDirectory>(directory);
    }

    [Theory]
    [InlineData("true", "01:00:00", true)]
    [InlineData("true", "00:00:00", false)]
    [InlineData("false", "01:00:00", false)]
    public void The_Purge_Service_Runs_Only_When_It_Has_Something_To_Do(
        string enabled, string purgeAfter, bool expected)
    {
        // Disabled, or with no retention window, there is nothing to purge
        // — and a hosted service that wakes up to do nothing is a thing to
        // explain later.
        var services = new ServiceCollection();

        services.AddBowireScim(Config(
            ("Bowire:Scim:Enabled", enabled),
            ("Bowire:Scim:Token", "t"),
            ("Bowire:Scim:PurgeAfter", purgeAfter)));

        var hosted = services.Any(d => d.ServiceType == typeof(IHostedService));
        Assert.Equal(expected, hosted);
    }

    // ---- MapBowireScim ----

    [Fact]
    public async Task Disabled_Mounts_Nothing()
    {
        using var host = await BuildHost(enabled: false, token: null, map: true);
        using var client = host.GetTestClient();

        var response = await client.GetAsync(
            new Uri("/scim/v2/Users", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Enabled_Without_A_Token_Is_Refused_At_Startup()
    {
        // Served open, this endpoint lets anyone who can reach the host
        // create and remove identities. Failing to start is the right
        // answer, and the message has to say why.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildHost(enabled: true, token: "", map: true));

        Assert.Contains("Token", error.Message, StringComparison.Ordinal);
        Assert.Contains("identities", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_With_A_Token_Serves_The_Endpoints()
    {
        using var host = await BuildHost(enabled: true, token: "secret", map: true);
        using var client = host.GetTestClient();

        var response = await client.GetAsync(
            new Uri("/scim/v2/Users", UriKind.Relative), TestContext.Current.CancellationToken);

        // Mounted: an unauthenticated call is turned away by the endpoint
        // rather than missing entirely.
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- UseBowireScim ----

    [Fact]
    public async Task Disabled_Lets_Everything_Through()
    {
        using var host = await BuildHost(enabled: false, token: null, map: false, subject: "unknown");
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/probe", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_Anonymous_Request_Is_Not_The_Middleware_Concern()
    {
        // No subject, nothing to look up: SCIM says nothing about callers
        // it cannot identify.
        using var host = await BuildHost(enabled: true, token: "t", map: false, subject: null);
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/probe", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_Unprovisioned_Identity_Is_Allowed_By_Default()
    {
        // An IdP that has not finished its first sync would otherwise lock
        // out the operator who just switched provisioning on.
        using var host = await BuildHost(enabled: true, token: "t", map: false, subject: "nobody");
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/probe", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_Unprovisioned_Identity_Is_Refused_When_Scim_Is_The_Allow_List()
    {
        using var host = await BuildHost(
            enabled: true, token: "t", map: false, subject: "nobody",
            configure: o => { o.RequireProvisioned = true; o.EnforceActive = true; });
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/probe", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("provisioned", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Deactivated_Identity_Is_Refused()
    {
        // The point of the middleware: deprovisioning in the directory has
        // to take effect here, not at the next token refresh.
        using var host = await BuildHost(
            enabled: true, token: "t", map: false, subject: "gone",
            configure: o => o.EnforceActive = true,
            seed: store => store.CreateUser(new ScimUser
            {
                UserName = "gone@example.com",
                Active = false,
                ExternalId = "gone",
            }));
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/probe", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("deactivated", body, StringComparison.Ordinal);
    }

    // ---- harness ----

    private static async Task<IHost> BuildHost(
        bool enabled,
        string? token,
        bool map,
        string? subject = null,
        Action<BowireScimOptions>? configure = null,
        Action<BowireScimStore>? seed = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .ConfigureServices(services =>
                   {
                       services.AddRouting();
                       services.AddSingleton(new BowireTenancyOptions());
                       services.AddBowireScim(
                           Config(
                               ("Bowire:Scim:Enabled", enabled ? "true" : "false"),
                               ("Bowire:Scim:Token", token ?? "")),
                           o => configure?.Invoke(o));
                   })
                   .Configure(app =>
                   {
                       seed?.Invoke(app.ApplicationServices.GetRequiredService<BowireScimStore>());

                       // Stand in for whatever auth the host configured.
                       if (subject is not null)
                       {
                           app.Use(async (ctx, next) =>
                           {
                               ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                                   [new Claim(ClaimTypes.NameIdentifier, subject)], "test"));
                               await next(ctx);
                           });
                       }

                       app.UseBowireScim();
                       app.UseRouting();
                       app.UseEndpoints(e =>
                       {
                           if (map) e.MapBowireScim();
                           e.MapGet("/probe", () => Results.Ok(new { ok = true }));
                       });
                   });
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    /// <summary>A directory that is recognisably not the SCIM one.</summary>
    private sealed class StubDirectory : IBowireUserDirectory
    {
        public BowireUserProfile Describe(ClaimsPrincipal? principal, string subject)
            => new() { Subject = subject };

        public IReadOnlyList<BowireUserProfile> Search(string? term, int limit) => [];
    }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// <c>GET /api/me</c> — who the workbench is serving (#98, #28 Phase F).
/// </summary>
/// <remarks>
/// The answer drives the identity chip, the impersonation banner, the
/// sign-out link and whether administrative controls are drawn at all. Three
/// of those are wrong in a way nobody would notice from the screen if this
/// endpoint quietly reported the impersonated identity instead of the
/// administrator, so they are pinned here.
/// </remarks>
public sealed class BowireIdentityEndpointsTests
{
    private const string Admin = "admin@example.com";
    private const string Target = "alice@example.com";

    // ---- the single-user shape ----

    [Fact]
    public async Task A_Single_User_Install_Says_So_Rather_Than_Refusing()
    {
        // Mapped unconditionally: a workbench that has to know the deployment
        // shape before it can ask who it is serving asks the question wrong.
        using var host = await BuildHost(tenancy: false);

        var body = await GetAsync(host);

        Assert.False(body.GetProperty("multiTenant").GetBoolean());
        Assert.False(body.TryGetProperty("subject", out _));
    }

    [Fact]
    public async Task A_Laptop_Keeps_Its_Administrative_Controls()
    {
        // #636 — canAdminister travels on the single-user shape too. Without
        // it the workbench cannot tell "there is no gate here" from "you are
        // not an administrator", and an install with no identities at all
        // would lose its plugin-install button to a check meant for shared
        // installs.
        using var host = await BuildHost(tenancy: false);

        var body = await GetAsync(host);

        Assert.True(body.GetProperty("canAdminister").GetBoolean());
    }

    [Fact]
    public async Task Tenancy_On_But_Nobody_Signed_In_Is_Still_The_Short_Answer()
    {
        // There is genuinely nobody to identify. Not an error, and not an
        // empty profile — the difference matters to the caller.
        using var host = await BuildHost(tenancy: true, subject: null);

        var body = await GetAsync(host);

        Assert.False(body.GetProperty("multiTenant").GetBoolean());
    }

    // ---- the identified shape ----

    [Fact]
    public async Task A_Signed_In_Identity_Is_Described()
    {
        using var host = await BuildHost(
            tenancy: true,
            subject: Target,
            directory: new StubDirectory((Target, "Alice Anders", "alice@example.com", false)));

        var body = await GetAsync(host);

        Assert.True(body.GetProperty("multiTenant").GetBoolean());
        Assert.Equal(Target, body.GetProperty("subject").GetString());
        Assert.Equal("Alice Anders", body.GetProperty("displayName").GetString());
        Assert.Equal("AA", body.GetProperty("initials").GetString());
        // Nobody is impersonating, so the banner has nothing to draw.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("actingAs").ValueKind);
    }

    [Fact]
    public async Task A_Sign_Out_Link_Appears_Only_When_It_Would_Do_Something()
    {
        // A sign-out link that clears nothing is worse than none, because
        // people believe it.
        using var withProvider = await BuildHost(
            tenancy: true, subject: Target, signOutUrl: "https://idp.example.com/logout");
        using var without = await BuildHost(tenancy: true, subject: Target);

        Assert.Equal("https://idp.example.com/logout",
            (await GetAsync(withProvider)).GetProperty("signOutUrl").GetString());
        Assert.Equal(JsonValueKind.Null,
            (await GetAsync(without)).GetProperty("signOutUrl").ValueKind);
    }

    // ---- impersonation ----

    [Fact]
    public async Task While_Impersonating_The_Chip_Shows_The_Administrator()
    {
        // The tenancy scope names the target, which is right for storage and
        // wrong for "who am I". Reading it here would put the impersonated
        // person's name on the administrator's own chip — an administrator
        // would have no way to see that they are not themselves.
        using var host = await BuildHost(
            tenancy: true,
            subject: Target,
            impersonating: (Actor: Admin, Target: Target),
            directory: new StubDirectory(
                (Admin, "Admin Ahlers", "admin@example.com", true),
                (Target, "Alice Anders", "alice@example.com", false)));

        var body = await GetAsync(host);

        Assert.Equal(Admin, body.GetProperty("subject").GetString());
        Assert.Equal("Admin Ahlers", body.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task The_Banner_Names_Whose_Workbench_Is_Being_Looked_At()
    {
        using var host = await BuildHost(
            tenancy: true,
            subject: Target,
            impersonating: (Actor: Admin, Target: Target),
            directory: new StubDirectory(
                (Admin, "Admin Ahlers", "admin@example.com", true),
                (Target, "Alice Anders", "alice@example.com", false)));

        var actingAs = (await GetAsync(host)).GetProperty("actingAs");

        // Described without the administrator's claims — passing those would
        // label the banner with the administrator's own name, so the screen
        // would say they are looking at themselves.
        Assert.Equal(Target, actingAs.GetProperty("subject").GetString());
        Assert.Equal("Alice Anders", actingAs.GetProperty("displayName").GetString());
        Assert.Equal("AA", actingAs.GetProperty("initials").GetString());
    }

    // ---- initials ----

    [Theory]
    // A display name wins, and two words give one letter each.
    [InlineData("Thomas Stegemann", null, "sub", "TS")]
    [InlineData("Alice", null, "sub", "AL")]
    // Then the local part of the address, which separators split.
    [InlineData(null, "thomas.stegemann@kuestenlogik.com", "sub", "TS")]
    [InlineData(null, "alice+tag@example.com", "sub", "AT")]
    [InlineData(null, "ops@example.com", "sub", "OP")]
    // And only then the subject, which for most providers is a GUID. Two
    // characters of a GUID identify nobody — still better than an empty
    // circle.
    [InlineData(null, null, "8f14e45f", "8F")]
    // Nothing usable at all.
    [InlineData(null, null, "!!!", "?")]
    [InlineData("  ", "  ", "", "?")]
    public void Initials_Come_From_The_Best_Thing_Available(
        string? displayName, string? email, string subject, string expected)
    {
        var initials = BowireIdentityEndpoints.Initials(new BowireUserProfile
        {
            Subject = subject,
            DisplayName = displayName,
            Email = email,
        });

        Assert.Equal(expected, initials);
    }

    [Fact]
    public void A_Single_Letter_Name_Does_Not_Overrun()
    {
        Assert.Equal("X", BowireIdentityEndpoints.Initials(
            new BowireUserProfile { Subject = "s", DisplayName = "X" }));
    }

    // ---- harness ----

    private static async Task<JsonElement> GetAsync(IHost host)
    {
        using var client = host.GetTestClient();
        using var response = await client.GetAsync(
            new Uri("/api/me", UriKind.Relative), TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {json}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static async Task<IHost> BuildHost(
        bool tenancy,
        string? subject = null,
        (string Actor, string Target)? impersonating = null,
        IBowireUserDirectory? directory = null,
        string? signOutUrl = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .ConfigureServices(services =>
                   {
                       services.AddRouting();
                       if (tenancy)
                           services.AddSingleton(new BowireTenancyOptions { Enabled = true });
                       if (directory is not null)
                           services.AddSingleton(directory);
                       if (signOutUrl is not null)
                           services.AddSingleton<IBowireAuthProvider>(new StubAuthProvider(signOutUrl));
                   })
                   .Configure(app =>
                   {
                       // Stand in for the tenancy and impersonation
                       // middleware: both ride the execution context.
                       app.Use(async (ctx, next) =>
                       {
                           ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                               subject is null ? [] : [new Claim(ClaimTypes.NameIdentifier, subject)],
                               subject is null ? null : "test"));

                           using var tenant = subject is null
                               ? null
                               : BowireTenancy.Enter(subject);
                           using var acting = impersonating is null
                               ? null
                               : BowireImpersonation.Enter(impersonating.Value.Actor, impersonating.Value.Target);
                           await next(ctx);
                       });

                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireIdentityEndpoints(string.Empty));
                   });
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private sealed class StubDirectory(params (string Subject, string? Name, string? Email, bool Admin)[] people)
        : IBowireUserDirectory
    {
        public BowireUserProfile Describe(ClaimsPrincipal? principal, string subject)
        {
            var match = people.FirstOrDefault(p => p.Subject == subject);
            return new BowireUserProfile
            {
                Subject = subject,
                DisplayName = match.Name,
                Email = match.Email,
                IsAdmin = match.Admin,
            };
        }

        public IReadOnlyList<BowireUserProfile> Search(string? term, int limit) => [];
    }

    /// <summary>A provider that exists only to have a sign-out URL.</summary>
    private sealed class StubAuthProvider(string signOutUrl) : IBowireAuthProvider
    {
        public string Id => "stub";

        public string Name => "Stub";

        public string? SignOutUrl { get; } = signOutUrl;

        public void AddAuthentication(
            IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            // Nothing to register: the endpoint under test only reads
            // SignOutUrl off whatever provider the host happens to have.
        }
    }
}

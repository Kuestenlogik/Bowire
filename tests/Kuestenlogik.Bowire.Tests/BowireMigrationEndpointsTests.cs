// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// <c>/api/migration</c> — the one-time offer to bring a single-user
/// install's state into the calling identity's slot (#97).
/// </summary>
/// <remarks>
/// The endpoints were untested, and they sit in front of a routine that
/// copies and moves directories on the operator's disk. The questions worth
/// answering here are the ones a person asks while looking at the dialog:
/// is this my data, what happens if I say no, and did that just delete
/// something.
/// </remarks>
public sealed class BowireMigrationEndpointsTests : IDisposable
{
    private const string Subject = "alice@example.com";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-migration-" + Guid.NewGuid().ToString("N"));

    public BowireMigrationEndpointsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- what the workbench is told ----

    [Fact]
    public async Task A_Single_User_Install_Is_Told_Off_Rather_Than_Nothing()
    {
        // Mapped whether or not the install is multi-tenant, so the workbench
        // can ask one question everywhere. "Off" is the answer, not a 404.
        using var host = await BuildHost(tenancy: false);

        var body = await GetAsync(host, "/api/migration");

        Assert.Equal("Off", body.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Without_An_Identity_There_Is_Nobody_To_Migrate_For()
    {
        // Background work reaches the same route with no subject on the
        // execution context. There is no slot to plan against.
        using var host = await BuildHost(tenancy: true, subject: null);

        var body = await GetAsync(host, "/api/migration");

        Assert.Equal("Off", body.GetProperty("state").GetString());
    }

    [Fact]
    public async Task An_Offer_Names_The_Size_Of_What_Is_Being_Decided()
    {
        // A count and a size are what make "is this my data?" answerable
        // without a file browser.
        Seed("environments/dev.json", 300);
        Seed("collections/harbor.json", 700);
        using var host = await BuildHost(tenancy: true);

        var body = await GetAsync(host, "/api/migration");

        Assert.Equal("Available", body.GetProperty("state").GetString());
        Assert.Equal(2, body.GetProperty("files").GetInt32());
        Assert.Equal(1000, body.GetProperty("bytes").GetInt64());
        Assert.Equal(Path.GetFullPath(_root), body.GetProperty("source").GetString());
    }

    [Fact]
    public async Task An_Empty_Install_Has_Nothing_To_Offer()
    {
        using var host = await BuildHost(tenancy: true);

        var body = await GetAsync(host, "/api/migration");

        Assert.Equal("NothingToMigrate", body.GetProperty("state").GetString());
    }

    [Fact]
    public async Task An_Operator_Who_Turned_The_Offer_Off_Is_Not_Asked()
    {
        Seed("environments/dev.json", 10);
        using var host = await BuildHost(tenancy: true, mode: BowireUserMigrationMode.Skip);

        var body = await GetAsync(host, "/api/migration");

        Assert.Equal("Disabled", body.GetProperty("state").GetString());
    }

    // ---- accepting ----

    [Fact]
    public async Task Accepting_Copies_The_State_And_Leaves_The_Original()
    {
        // Copy, never move: the install can be switched back to single-user,
        // and a migration into the wrong slot stays recoverable.
        Seed("environments/dev.json", 42);
        using var host = await BuildHost(tenancy: true);

        var body = await PostAsync(host, "/api/migration/accept");

        Assert.Equal("AlreadyDecided", body.GetProperty("state").GetString());
        Assert.Equal("Migrated", body.GetProperty("outcome").GetString());
        Assert.Equal(1, body.GetProperty("files").GetInt32());
        Assert.Equal(42, body.GetProperty("bytes").GetInt64());
        Assert.True(File.Exists(Path.Combine(SlotPath(), "environments", "dev.json")));
        Assert.True(File.Exists(Path.Combine(_root, "environments", "dev.json")),
            "the legacy file has to survive the migration");
    }

    [Fact]
    public async Task Install_State_Is_Not_Handed_To_One_Identity()
    {
        // #96/#98 — the SCIM roster and the audit log describe everybody.
        // Copying them into a slot would give one person a list of their
        // colleagues and a copy of the evidence.
        Seed("environments/dev.json", 10);
        Seed("scim/users.json", 10);
        Seed(BowireAuditLog.DirectoryName + "/2026-09.jsonl", 10);
        Seed("plugins/kafka/manifest.json", 10);
        using var host = await BuildHost(tenancy: true);

        await PostAsync(host, "/api/migration/accept");

        Assert.True(File.Exists(Path.Combine(SlotPath(), "environments", "dev.json")));
        Assert.False(Directory.Exists(Path.Combine(SlotPath(), "scim")));
        Assert.False(Directory.Exists(Path.Combine(SlotPath(), BowireAuditLog.DirectoryName)));
        Assert.False(Directory.Exists(Path.Combine(SlotPath(), "plugins")));
    }

    [Fact]
    public async Task A_Second_Tab_Accepting_Is_Refused_Rather_Than_Run_Twice()
    {
        Seed("environments/dev.json", 10);
        using var host = await BuildHost(tenancy: true);
        await PostAsync(host, "/api/migration/accept");

        using var client = host.GetTestClient();
        using var response = await client.PostAsync(
            new Uri("/api/migration/accept", UriKind.Relative), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        // The type is what a client branches on; the detail is what a person
        // reads, and it has to say which state blocked it.
        Assert.Equal("urn:bowire:migration:not-available",
            problem.RootElement.GetProperty("type").GetString());
        Assert.Contains("AlreadyDecided",
            problem.RootElement.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accepting_On_A_Single_User_Install_Is_Off_Not_An_Error()
    {
        using var host = await BuildHost(tenancy: false);

        var body = await PostAsync(host, "/api/migration/accept");

        Assert.Equal("Off", body.GetProperty("state").GetString());
    }

    // ---- declining ----

    [Fact]
    public async Task Declining_Records_The_Refusal_And_Copies_Nothing()
    {
        // Recorded so the offer is not made again on every sign-in.
        Seed("environments/dev.json", 10);
        using var host = await BuildHost(tenancy: true);

        var body = await PostAsync(host, "/api/migration/decline");

        Assert.Equal("AlreadyDecided", body.GetProperty("state").GetString());
        Assert.Equal("Declined", body.GetProperty("outcome").GetString());
        Assert.False(Directory.Exists(Path.Combine(SlotPath(), "environments")));

        var again = await GetAsync(host, "/api/migration");
        Assert.Equal("AlreadyDecided", again.GetProperty("state").GetString());
        Assert.Equal("Declined", again.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Declining_Twice_Is_Refused()
    {
        Seed("environments/dev.json", 10);
        using var host = await BuildHost(tenancy: true);
        await PostAsync(host, "/api/migration/decline");

        using var client = host.GetTestClient();
        using var response = await client.PostAsync(
            new Uri("/api/migration/decline", UriKind.Relative), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ---- undoing ----

    [Fact]
    public async Task Undo_Answers_Where_It_Went_Rather_Than_Reassuring()
    {
        // "Did that delete my work?" has to be answerable with a path.
        Seed("environments/dev.json", 10);
        using var host = await BuildHost(tenancy: true);
        await PostAsync(host, "/api/migration/accept");

        var body = await PostAsync(host, "/api/migration/undo");

        Assert.Equal("Available", body.GetProperty("state").GetString());
        var movedTo = body.GetProperty("movedTo").GetString();
        Assert.False(string.IsNullOrWhiteSpace(movedTo));
        Assert.True(Directory.Exists(movedTo), movedTo);
        Assert.True(File.Exists(Path.Combine(movedTo!, "environments", "dev.json")),
            "the migrated copy has to still be on disk after an undo");
        Assert.False(Directory.Exists(SlotPath()));
    }

    [Fact]
    public async Task Undo_Puts_The_Offer_Back_On_The_Table()
    {
        Seed("environments/dev.json", 10);
        using var host = await BuildHost(tenancy: true);
        await PostAsync(host, "/api/migration/accept");
        await PostAsync(host, "/api/migration/undo");

        var body = await GetAsync(host, "/api/migration");

        Assert.Equal("Available", body.GetProperty("state").GetString());
    }

    [Fact]
    public async Task There_Is_Nothing_To_Undo_Before_A_Decision()
    {
        Seed("environments/dev.json", 10);
        using var host = await BuildHost(tenancy: true);

        using var client = host.GetTestClient();
        using var response = await client.PostAsync(
            new Uri("/api/migration/undo", UriKind.Relative), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Undo_On_A_Single_User_Install_Is_Off()
    {
        using var host = await BuildHost(tenancy: false);

        var body = await PostAsync(host, "/api/migration/undo");

        Assert.Equal("Off", body.GetProperty("state").GetString());
    }

    // ---- harness ----

    private string SlotPath()
        => Path.Combine(Path.GetFullPath(_root), BowireUserSlot.DirectoryName, BowireUserSlot.Slug(Subject));

    private void Seed(string relativePath, int bytes)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private async Task<IHost> BuildHost(
        bool tenancy,
        string? subject = Subject,
        BowireUserMigrationMode mode = BowireUserMigrationMode.Prompt)
    {
        var root = _root;
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .ConfigureServices(services =>
                   {
                       services.AddRouting();
                       if (tenancy)
                       {
                           services.AddSingleton(new BowireTenancyOptions
                           {
                               Enabled = true,
                               Migration = mode,
                           });
                           services.AddSingleton(
                               new BowireTenancy(root, new ScopedBowireUserStore(root, "shared")));
                       }
                   })
                   .Configure(app =>
                   {
                       // Stand in for the tenancy middleware: the endpoints
                       // read the subject off the execution context, not the
                       // principal.
                       if (subject is not null)
                       {
                           app.Use(async (ctx, next) =>
                           {
                               using (BowireTenancy.Enter(subject)) await next(ctx);
                           });
                       }

                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireMigrationEndpoints(string.Empty));
                   });
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task<JsonElement> GetAsync(IHost host, string path)
    {
        using var client = host.GetTestClient();
        using var response = await client.GetAsync(
            new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        return await ReadAsync(response);
    }

    private static async Task<JsonElement> PostAsync(IHost host, string path)
    {
        using var client = host.GetTestClient();
        using var response = await client.PostAsync(
            new Uri(path, UriKind.Relative), null, TestContext.Current.CancellationToken);
        return await ReadAsync(response);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {json}");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

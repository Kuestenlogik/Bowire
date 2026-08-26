// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// <c>DELETE /api/workspace/{id}</c> — the per-workspace disk purge.
/// </summary>
/// <remarks>
/// <para>
/// This endpoint deletes a directory tree recursively from an id that arrives
/// in a URL. That is the shape of every path-traversal story ever written, so
/// the guards are the test: the id is sanitised, the resolved path must stay
/// strictly under the user root, and the user root itself can never be the
/// target.
/// </para>
/// <para>
/// It is also standalone-only, and the reason is not squeamishness: an
/// embedded host is typically a production server whose filesystem the
/// workbench user does not own.
/// </para>
/// <para>
/// Writing these found that the endpoint had never worked: the containment
/// check anchored on <c>GetUserPath("")</c>, and the store rejects an empty
/// filename, so every call ended in an unhandled ArgumentException. The
/// anchor is the workspaces folder now.
/// </para>
/// </remarks>
[Collection("BowireUserContext")]
public sealed class BowireWorkspacePurgeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-purge-" + Guid.NewGuid().ToString("N"));
    private readonly IBowireUserStore _previous = BowireUserContext.Current;

    public BowireWorkspacePurgeTests()
    {
        Directory.CreateDirectory(_root);
        BowireUserContext.Current = new DefaultBowireUserStore(_root);
    }

    public void Dispose()
    {
        BowireUserContext.Current = _previous;
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static async Task<IHost> BuildHost(BowireMode mode = BowireMode.Standalone)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireWorkspaceEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s =>
                   {
                       s.AddRouting();
                       s.AddSingleton<IOptions<BowireOptions>>(
                           Options.Create(new BowireOptions { Mode = mode }));
                   });
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Purge(IHost host, string id)
    {
        using var resp = await host.GetTestClient().DeleteAsync(
            new Uri($"/api/workspace/{id}", UriKind.Relative), TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (resp.StatusCode, doc.RootElement.Clone());
    }

    private string WorkspaceFolder(string id)
    {
        var dir = Path.Combine(_root, "workspaces", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "chunk-1.json"), "{}");
        return dir;
    }

    // ---- the happy path ----

    [Fact]
    public async Task Purging_A_Workspace_Deletes_Its_Folder()
    {
        // The reason the endpoint exists: without it a hard-delete from Trash
        // leaves gigabytes of recording chunks behind per purged workspace.
        using var host = await BuildHost();
        var dir = WorkspaceFolder("ws-1");

        var (status, body) = await Purge(host, "ws-1");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("purged").GetBoolean());
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task A_Workspace_That_Never_Wrote_To_Disk_Is_A_Success_With_Purged_False()
    {
        // A browser-storage-only workspace. 200 with purged=false rather than
        // a 404, so the caller can tell "cleaned up" from "nothing to clean
        // up" without branching on a status code.
        using var host = await BuildHost();

        var (status, body) = await Purge(host, "never-on-disk");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("purged").GetBoolean());
        Assert.Equal("absent", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Purging_One_Workspace_Leaves_The_Others_Alone()
    {
        using var host = await BuildHost();
        var keep = WorkspaceFolder("ws-keep");
        WorkspaceFolder("ws-drop");

        await Purge(host, "ws-drop");

        Assert.True(Directory.Exists(keep));
    }

    // ---- the guards ----

    [Theory]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("....")]
    [InlineData("/")]
    [InlineData("%2e%2e")]
    public async Task An_Id_That_Sanitises_To_Nothing_Useful_Never_Deletes_Anything(string id)
    {
        // The failure being ruled out is deleting the user root itself in
        // lieu of a workspace folder — every one of these has to come back a
        // refusal or a no-op, and the root has to survive.
        using var host = await BuildHost();
        var sentinel = Path.Combine(_root, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep me", TestContext.Current.CancellationToken);

        using var resp = await host.GetTestClient().DeleteAsync(
            new Uri($"/api/workspace/{id}", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.True(File.Exists(sentinel), "the user root must survive");
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task A_Traversing_Id_Cannot_Reach_A_Sibling_Of_The_User_Root()
    {
        // The path is rebuilt from a sanitised segment, and then checked again
        // for containment. Both belt and braces are deliberate.
        using var host = await BuildHost();
        var outside = Path.Combine(Path.GetTempPath(), $"bowire-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            // Raw rather than the JSON helper: depending on how the route
            // matches an escaped segment this may be a refusal document or a
            // plain 404, and the assertion is about the directory either way.
            using var resp = await host.GetTestClient().DeleteAsync(
                new Uri($"/api/workspace/{Uri.EscapeDataString("../" + Path.GetFileName(outside))}",
                    UriKind.Relative),
                TestContext.Current.CancellationToken);

            Assert.True(Directory.Exists(outside), "a directory outside the user root must survive");
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Purging_From_An_Embedded_Host_Is_Refused_With_The_Reason()
    {
        // The host there is typically a production server the workbench user
        // does not own. The refusal says so rather than just saying no.
        using var host = await BuildHost(BowireMode.Embedded);
        var dir = WorkspaceFolder("ws-1");

        var (status, body) = await Purge(host, "ws-1");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("urn:bowire:workspace:purge-not-available", body.GetProperty("type").GetString());
        Assert.True(Directory.Exists(dir), "an embedded host must not delete anything");
    }

    // ---- the folder-open capability probe ----

    [Fact]
    public async Task Standalone_Reports_That_Opening_A_Folder_Is_Available()
    {
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().GetAsync(
            new Uri("/api/workspace/can-open-folder", UriKind.Relative),
            TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(doc.RootElement.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task An_Embedded_Host_Reports_It_Is_Not_Available_And_Why()
    {
        // The workbench gates the click-handler on this, so the answer has to
        // arrive before anyone can press a button that would spawn a desktop
        // process on someone's server.
        using var host = await BuildHost(BowireMode.Embedded);

        using var resp = await host.GetTestClient().GetAsync(
            new Uri("/api/workspace/can-open-folder", UriKind.Relative),
            TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(doc.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("embedded", doc.RootElement.GetProperty("reason").GetString());
    }
}

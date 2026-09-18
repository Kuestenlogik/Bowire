// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text;
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
/// <c>GET</c> / <c>PUT /api/flows</c> — the server side flows did not have
/// until #641.
/// </summary>
/// <remarks>
/// Its absence showed up as four separate-looking bugs: two MCP resources
/// reading a file nothing wrote, <c>bowire test</c> unable to see anything
/// built in the workbench, flows missing from a git-native workspace, and
/// flows outside the per-identity slot. The endpoints answer all four, and
/// what is pinned here is the part that decides which file is touched —
/// plus the refusals, because this route writes to disk on the operator's
/// machine.
/// </remarks>
public sealed class BowireFlowEndpointsTests : IDisposable
{
    private const string OneFlow =
        """{"flows":[{"id":"flow_1","name":"Berth check"}]}""";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-flows-" + Guid.NewGuid().ToString("N"));

    /// <summary>This class's storage, for as long as it runs.</summary>
    private readonly IDisposable _userScope;

    public BowireFlowEndpointsTests()
    {
        Directory.CreateDirectory(_root);
        _userScope = BowireUserContext.Enter(new DefaultBowireUserStore(_root));
    }

    public void Dispose()
    {
        _userScope.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- reading ----

    [Fact]
    public async Task An_Install_That_Has_Never_Saved_Answers_An_Empty_Envelope()
    {
        // Not 404 and not an empty body: the workbench parses this, and
        // "no flows yet" is a document, not a missing resource.
        using var host = await BuildHost();

        var (status, body) = await GetAsync(host);

        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Empty(doc.RootElement.GetProperty("flows").EnumerateArray());
    }

    [Fact]
    public async Task What_Was_Put_Comes_Back()
    {
        using var host = await BuildHost();

        await PutAsync(host, OneFlow);
        var (_, body) = await GetAsync(host);

        using var doc = JsonDocument.Parse(body);
        var flow = Assert.Single(doc.RootElement.GetProperty("flows").EnumerateArray());
        Assert.Equal("flow_1", flow.GetProperty("id").GetString());
    }

    [Fact]
    public async Task The_Answer_Is_Json_So_The_Workbench_Can_Read_It()
    {
        using var host = await BuildHost();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(
            new Uri("/api/flows", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task One_Workspace_Does_Not_See_Another_Ones_Flows()
    {
        // The cross-workspace bleed the collection store already learned not
        // to do: a workspace that has never saved must not inherit whatever
        // the last one wrote.
        using var host = await BuildHost();

        await PutAsync(host, OneFlow, "?workspaceId=harbor");
        var (_, other) = await GetAsync(host, "?workspaceId=berths");

        using var doc = JsonDocument.Parse(other);
        Assert.Empty(doc.RootElement.GetProperty("flows").EnumerateArray());
    }

    [Fact]
    public async Task A_Workspace_Does_Not_See_The_Workspace_Less_File_Either()
    {
        // The CLI and hosts predating workspaces write the legacy location.
        // Handing that to the first workspace that looks is the same bleed
        // from the other direction.
        using var host = await BuildHost();

        await PutAsync(host, OneFlow);
        var (_, scoped) = await GetAsync(host, "?workspaceId=harbor");

        using var doc = JsonDocument.Parse(scoped);
        Assert.Empty(doc.RootElement.GetProperty("flows").EnumerateArray());
    }

    [Fact]
    public async Task A_Git_Native_Workspace_Writes_Into_Its_Checkout()
    {
        // The point of storageRoot: the file lands in the repository and
        // travels with a clone, rather than sitting in one person's slot.
        var checkout = Path.Combine(_root, "checkouts", "harbor");
        Directory.CreateDirectory(checkout);
        using var host = await BuildHost();

        await PutAsync(host, OneFlow, $"?workspaceId=harbor&storageRoot={Uri.EscapeDataString(checkout)}");

        Assert.True(File.Exists(Path.Combine(checkout, "flows.json")));
    }

    // ---- writing, and what is refused ----

    [Fact]
    public async Task A_Successful_Save_Says_So()
    {
        using var host = await BuildHost();

        var (status, body) = await PutAsync(host, OneFlow);

        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("saved").GetBoolean());
    }

    [Fact]
    public async Task Malformed_Json_Is_Refused_And_Nothing_Is_Written()
    {
        // Validated before writing: a malformed PUT must not leave a file
        // behind that the next load has to recover from.
        using var host = await BuildHost();
        await PutAsync(host, OneFlow);

        var (status, body) = await PutAsync(host, "{ this is not json");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("Invalid JSON", body, StringComparison.Ordinal);

        // The good document is still there.
        var (_, after) = await GetAsync(host);
        using var doc = JsonDocument.Parse(after);
        Assert.Single(doc.RootElement.GetProperty("flows").EnumerateArray());
    }

    [Theory]
    // A workspace id is one path segment; anything that could climb out of
    // the slot is refused rather than sanitised.
    [InlineData("?workspaceId=../escape")]
    [InlineData("?workspaceId=a/b")]
    [InlineData("?workspaceId=%2e%2e%2f%2e%2e")]
    public async Task A_Workspace_Id_That_Is_A_Path_Is_Refused(string query)
    {
        using var host = await BuildHost();

        var (getStatus, getBody) = await GetAsync(host, query);
        var (putStatus, _) = await PutAsync(host, OneFlow, query);

        Assert.Equal(HttpStatusCode.BadRequest, getStatus);
        Assert.Equal(HttpStatusCode.BadRequest, putStatus);
        // The message has to name what is wrong, not just refuse.
        Assert.Contains("workspaceId", getBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Relative_Storage_Root_Is_Refused()
    {
        // It would resolve against the server's working directory — which
        // the caller cannot see and did not mean to write into.
        using var host = await BuildHost();

        var (status, body) = await GetAsync(host, "?workspaceId=harbor&storageRoot=relative/path");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("absolute", body, StringComparison.Ordinal);
    }

    // ---- harness ----

    private static async Task<IHost> BuildHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .ConfigureServices(s => s.AddRouting())
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e =>
                           e.MapBowireFlowEndpoints(new BowireOptions(), string.Empty));
                   });
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        // TestServer drops the caller's execution context by default, so the
        // store scope this class opened would not reach the handler -- and
        // the handler would fall back to the host's, which is the real
        // ~/.bowire. Quietly, by writing there.
        host.GetTestServer().PreserveExecutionContext = true;
        return host;
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(
        IHost host, string query = "")
    {
        using var client = host.GetTestClient();
        using var response = await client.GetAsync(
            new Uri("/api/flows" + query, UriKind.Relative), TestContext.Current.CancellationToken);
        return (response.StatusCode,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<(HttpStatusCode Status, string Body)> PutAsync(
        IHost host, string json, string query = "")
    {
        using var client = host.GetTestClient();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PutAsync(
            new Uri("/api/flows" + query, UriKind.Relative), content, TestContext.Current.CancellationToken);
        return (response.StatusCode,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}

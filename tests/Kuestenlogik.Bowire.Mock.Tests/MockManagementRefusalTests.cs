// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Mock.Management;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// How <c>POST /api/mocks</c> refuses a start it cannot make sense of.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint accepts three different start shapes — a schema mock, an
/// inline recording, and a recording looked up by id — and picks by which
/// field arrived. That makes "which field did you mean" the most likely thing
/// to get wrong, and the answer has to say which one, because the Mocks rail
/// shows it verbatim to someone filling in a form.
/// </para>
/// <para>
/// None of these start a real mock; they are the answers given before a port
/// is bound. The starting path is covered elsewhere.
/// </para>
/// </remarks>
public sealed class MockManagementRefusalTests
{
    private static async Task<IHost> BuildHost(bool withOpenApiSource = true)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireMockManagement(basePath: string.Empty));
                   })
                   .ConfigureServices(s =>
                   {
                       s.AddRouting();
                       s.AddBowireMockManagement(
                           withOpenApiSource
                               ? new IBowireMockSchemaSource[] { new OpenApiMockSchemaSource() }
                               : Array.Empty<IBowireMockSchemaSource>(),
                           Array.Empty<IBowireMockLiveSchemaHandler>(),
                           Array.Empty<IBowireMockHostingExtension>());
                   });
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task<HttpResponseMessage> PostMock(IHost host, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await host.GetTestClient().PostAsync(
            new Uri("/api/mocks", UriKind.Relative), content, TestContext.Current.CancellationToken);
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.GetProperty("error").GetString() ?? "";
    }

    [Fact]
    public async Task A_Body_That_Is_Not_Json_Names_The_Parse_Failure()
    {
        using var host = await BuildHost();

        using var resp = await PostMock(host, "{ not json");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        // The parser's message rides along: "invalid JSON" alone leaves the
        // caller with nothing to correct.
        Assert.StartsWith("Invalid JSON:", await ErrorOf(resp), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Empty_Body_Is_Refused_Rather_Than_Started_With_Defaults()
    {
        using var host = await BuildHost();

        using var resp = await PostMock(host, "null");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("required", await ErrorOf(resp), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_Unknown_Schema_Kind_Lists_The_Ones_That_Exist()
    {
        // A typo here is the likeliest mistake in the whole payload, and the
        // fix is one word — so the message carries the valid set.
        using var host = await BuildHost();

        using var resp = await PostMock(host, """{"schemaKind":"swagger","schemaInline":"{}"}""");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var error = await ErrorOf(resp);
        Assert.Contains("openapi", error, StringComparison.Ordinal);
        Assert.Contains("protobuf", error, StringComparison.Ordinal);
        Assert.Contains("graphql", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Valid_Kind_With_No_Source_Registered_Says_So_Instead_Of_Failing_Late()
    {
        // A recording-only embedded host recognises "openapi" as a kind and
        // still cannot serve it. Answering 400 here beats letting the
        // manager's InvalidOperationException surface as a 500 — nothing is
        // broken, the host simply never wired that source.
        using var host = await BuildHost(withOpenApiSource: false);

        using var resp = await PostMock(host, """{"schemaKind":"openapi","schemaInline":"{}"}""");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var error = await ErrorOf(resp);
        Assert.Contains("openapi", error, StringComparison.Ordinal);
        Assert.Contains("host", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_Schema_Kind_Without_A_Schema_Is_Refused()
    {
        using var host = await BuildHost();

        using var resp = await PostMock(host, """{"schemaKind":"openapi"}""");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task A_Payload_Naming_No_Start_Shape_At_All_Is_Refused()
    {
        // Neither schemaKind, nor recording, nor recordingId — there is
        // nothing to start.
        using var host = await BuildHost();

        using var resp = await PostMock(host, """{"label":"just a name"}""");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---- lookups for things that are not there ----

    [Fact]
    public async Task Reading_A_Mock_That_Does_Not_Exist_Is_A_404()
    {
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().GetAsync(
            new Uri("/api/mocks/no-such-mock", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Listing_With_Nothing_Running_Is_An_Empty_List_Not_A_404()
    {
        // The Mocks rail asks for this on load; an error there would take the
        // pane down on a host that simply has no mocks yet.
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().GetAsync(
            new Uri("/api/mocks", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // An envelope, not a bare array: `{ "mocks": [...] }` leaves room to
        // add fields later without breaking a client that indexes the top
        // level. Worth pinning, because a rail reading `body[0]` would work
        // against a bare array and silently read nothing here.
        var mocks = doc.RootElement.GetProperty("mocks");
        Assert.Equal(JsonValueKind.Array, mocks.ValueKind);
        Assert.Equal(0, mocks.GetArrayLength());
    }

    [Theory]
    [InlineData("/api/mocks/no-such-mock/requests")]
    [InlineData("/api/mocks/no-such-mock/requests/unmatched")]
    [InlineData("/api/mocks/no-such-mock/stubs")]
    public async Task Sub_Resources_Of_An_Unknown_Mock_Are_404(string path)
    {
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().GetAsync(
            new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Stopping_A_Mock_That_Does_Not_Exist_Is_A_404()
    {
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().DeleteAsync(
            new Uri("/api/mocks/no-such-mock", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- runtime stub CRUD (#404) ----
    //
    // Every one of these edits a mock that is supposed to be running. Against
    // an id that is not, the answer has to be a 404 rather than a 500 or a
    // silent success: the Mocks rail keeps a stub editor open across a mock
    // restart, so "the mock you were editing is gone" is a normal outcome and
    // the only one that tells the operator to restart it.

    [Fact]
    public async Task Reading_One_Stub_Of_An_Unknown_Mock_Is_A_404()
    {
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().GetAsync(
            new Uri("/api/mocks/no-such-mock/stubs/stub-1", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("not running", await ErrorOf(resp), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_A_Stub_To_An_Unknown_Mock_Is_A_404()
    {
        using var host = await BuildHost();
        using var content = new StringContent(
            """{"httpMethod":"GET","httpPath":"/orders"}""", Encoding.UTF8, "application/json");

        using var resp = await host.GetTestClient().PostAsync(
            new Uri("/api/mocks/no-such-mock/stubs", UriKind.Relative), content,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Replacing_A_Stub_On_An_Unknown_Mock_Is_A_404()
    {
        using var host = await BuildHost();
        using var content = new StringContent(
            """{"httpMethod":"GET","httpPath":"/orders"}""", Encoding.UTF8, "application/json");

        using var resp = await host.GetTestClient().PutAsync(
            new Uri("/api/mocks/no-such-mock/stubs/stub-1", UriKind.Relative), content,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Deleting_A_Stub_From_An_Unknown_Mock_Is_A_404()
    {
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().DeleteAsync(
            new Uri("/api/mocks/no-such-mock/stubs/stub-1", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Resetting_The_Stubs_Of_An_Unknown_Mock_Is_A_404()
    {
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().PostAsync(
            new Uri("/api/mocks/no-such-mock/stubs/reset", UriKind.Relative),
            content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Verifying_Against_An_Unknown_Mock_Is_A_404()
    {
        // The verification endpoint is what a test asserts through, so the
        // difference between "no such mock" and "not satisfied" decides
        // whether a failing test points at the mock or at the code.
        using var host = await BuildHost();
        using var content = new StringContent(
            """{"path":"/orders","atLeast":1}""", Encoding.UTF8, "application/json");

        using var resp = await host.GetTestClient().PostAsync(
            new Uri("/api/mocks/no-such-mock/verify", UriKind.Relative), content,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task The_Request_Log_Of_An_Unknown_Mock_Is_A_404_Even_With_A_Cursor()
    {
        // The rail polls this with ?since=<cursor> on a timer. After the mock
        // stops, the poll has to fail in a way the rail can stop on.
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().GetAsync(
            new Uri("/api/mocks/no-such-mock/requests?limit=10&since=5", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Theory]
    [InlineData("/api/mocks/no-such-mock/scenarios")]
    [InlineData("/api/mocks/no-such-mock/faults")]
    public async Task Scenario_And_Fault_State_Of_An_Unknown_Mock_Is_A_404(string path)
    {
        // Both are read by the rail when it opens a mock's panel.
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().GetAsync(
            new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Resetting_Scenarios_On_An_Unknown_Mock_Is_A_404()
    {
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().PostAsync(
            new Uri("/api/mocks/no-such-mock/scenarios/reset", UriKind.Relative),
            content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}

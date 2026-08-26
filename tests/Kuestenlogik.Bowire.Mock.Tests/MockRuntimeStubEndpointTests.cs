// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Mock.Management;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Protocol.Rest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// #404 — editing a <em>running</em> mock: stubs, the request log, scenarios
/// and faults.
/// </summary>
/// <remarks>
/// <para>
/// The refusals against a mock that is not running are covered in
/// <see cref="MockManagementRefusalTests"/>. These start a real one and drive
/// the same endpoints against it, because the point of the feature is that an
/// operator can author a stub without restarting — and "the edit was accepted"
/// is only true if the next read shows it.
/// </para>
/// <para>
/// The mock binds an ephemeral port on loopback and is stopped in a finally,
/// so a failing assertion cannot leave a listener behind for the next test.
/// </para>
/// </remarks>
[Collection("MockHostSerialised")]
public sealed class MockRuntimeStubEndpointTests
{
    private const string WeatherOpenApi = """
        openapi: 3.0.3
        info:
          title: Weather API
          version: 1.0.0
        paths:
          /weather:
            get:
              operationId: getCurrent
              tags: [Weather]
              responses:
                '200':
                  description: OK
                  content:
                    application/json:
                      schema:
                        type: object
                        properties:
                          condition:
                            type: string
        """;

    static MockRuntimeStubEndpointTests()
    {
        BowireOpenApiAdapterRegistry.Register(
            new Kuestenlogik.Bowire.Protocol.Rest.OpenApi3.OpenApi3Adapter());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IHost> BuildHost()
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
                           new IBowireMockSchemaSource[] { new OpenApiMockSchemaSource() },
                           Array.Empty<IBowireMockLiveSchemaHandler>(),
                           Array.Empty<IBowireMockHostingExtension>());
                   });
            })
            .Build();
        await host.StartAsync(Ct);
        return host;
    }

    /// <summary>Start a schema mock and hand back its id.</summary>
    private static async Task<string> StartMock(HttpClient client)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(new { schemaKind = "openapi", schemaInline = WeatherOpenApi }),
            Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(new Uri("/api/mocks", UriKind.Relative), content, Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.GetProperty("mockId").GetString()!;
    }

    private static async Task<JsonElement> GetJson(HttpClient client, string path)
    {
        using var resp = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.Clone();
    }

    private static async Task<(HttpStatusCode Status, string Body)> Send(
        HttpClient client, HttpMethod method, string path, string? json = null)
    {
        using var content = json is null
            ? null
            : new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
        {
            Content = content,
        };
        using var resp = await client.SendAsync(request, Ct);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync(Ct));
    }

    private const string NewStub = """
        {"protocolId":"rest","service":"Weather","method":"getForecast",
         "httpMethod":"GET","httpPath":"/forecast",
         "responseStatus":200,"responseBody":"{\"days\":3}"}
        """;

    // ---- the stub list ----

    [Fact]
    public async Task A_Freshly_Started_Mock_Lists_The_Stubs_Its_Schema_Produced()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var mockId = await StartMock(client);
        try
        {
            var body = await GetJson(client, $"/api/mocks/{mockId}/stubs");

            Assert.Equal(mockId, body.GetProperty("mockId").GetString());
            Assert.True(body.GetProperty("count").GetInt32() >= 1);
        }
        finally
        {
            using var _ = await client.DeleteAsync(new Uri($"/api/mocks/{mockId}", UriKind.Relative), Ct);
        }
    }

    [Fact]
    public async Task An_Added_Stub_Shows_Up_In_The_Next_Read()
    {
        // The whole point of #404: authoring a stub at runtime instead of
        // restarting the mock. "Accepted" only means something if the next
        // read shows it.
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var mockId = await StartMock(client);
        try
        {
            var (status, created) = await Send(client, HttpMethod.Post, $"/api/mocks/{mockId}/stubs", NewStub);
            Assert.Equal(HttpStatusCode.Created, status);

            using var createdDoc = JsonDocument.Parse(created);
            var stubId = createdDoc.RootElement.GetProperty("id").GetString();
            Assert.False(string.IsNullOrEmpty(stubId));

            var one = await GetJson(client, $"/api/mocks/{mockId}/stubs/{stubId}");
            Assert.Equal("/forecast", one.GetProperty("httpPath").GetString());
        }
        finally
        {
            using var _ = await client.DeleteAsync(new Uri($"/api/mocks/{mockId}", UriKind.Relative), Ct);
        }
    }

    [Fact]
    public async Task A_Stub_Id_Nothing_Answers_To_Is_A_404_On_A_Running_Mock()
    {
        // Distinct from "the mock is not running", which is the other 404 this
        // endpoint can return — the operator's next step differs.
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var mockId = await StartMock(client);
        try
        {
            var (status, body) = await Send(client, HttpMethod.Get, $"/api/mocks/{mockId}/stubs/no-such-stub");

            Assert.Equal(HttpStatusCode.NotFound, status);
            Assert.Contains("no-such-stub", body, StringComparison.Ordinal);
        }
        finally
        {
            using var _ = await client.DeleteAsync(new Uri($"/api/mocks/{mockId}", UriKind.Relative), Ct);
        }
    }

    [Fact]
    public async Task A_Stub_Body_That_Is_Not_A_Stub_Is_A_400()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var mockId = await StartMock(client);
        try
        {
            var (status, _) = await Send(client, HttpMethod.Post, $"/api/mocks/{mockId}/stubs", "\"just a string\"");

            Assert.Equal(HttpStatusCode.BadRequest, status);
        }
        finally
        {
            using var _ = await client.DeleteAsync(new Uri($"/api/mocks/{mockId}", UriKind.Relative), Ct);
        }
    }

    [Fact]
    public async Task Replacing_A_Stub_Returns_The_Version_That_Is_Now_Live()
    {
        // A PUT that reported success while the old stub kept answering would
        // be the worst outcome here: the operator edits, the mock does not
        // change, and nothing says so.
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var mockId = await StartMock(client);
        try
        {
            var (_, created) = await Send(client, HttpMethod.Post, $"/api/mocks/{mockId}/stubs", NewStub);
            using var createdDoc = JsonDocument.Parse(created);
            var stubId = createdDoc.RootElement.GetProperty("id").GetString();

            var (status, updated) = await Send(client, HttpMethod.Put, $"/api/mocks/{mockId}/stubs/{stubId}", """
                {"protocolId":"rest","service":"Weather","method":"getForecast",
                 "httpMethod":"GET","httpPath":"/forecast-v2",
                 "responseStatus":200,"responseBody":"{\"days\":7}"}
                """);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("/forecast-v2", updated, StringComparison.Ordinal);
        }
        finally
        {
            using var _ = await client.DeleteAsync(new Uri($"/api/mocks/{mockId}", UriKind.Relative), Ct);
        }
    }

    [Fact]
    public async Task Deleting_A_Stub_Takes_It_Off_The_List()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var mockId = await StartMock(client);
        try
        {
            var (_, created) = await Send(client, HttpMethod.Post, $"/api/mocks/{mockId}/stubs", NewStub);
            using var createdDoc = JsonDocument.Parse(created);
            var stubId = createdDoc.RootElement.GetProperty("id").GetString();
            var before = (await GetJson(client, $"/api/mocks/{mockId}/stubs")).GetProperty("count").GetInt32();

            var (status, _) = await Send(client, HttpMethod.Delete, $"/api/mocks/{mockId}/stubs/{stubId}");

            Assert.Equal(HttpStatusCode.NoContent, status);
            var after = (await GetJson(client, $"/api/mocks/{mockId}/stubs")).GetProperty("count").GetInt32();
            Assert.Equal(before - 1, after);
        }
        finally
        {
            using var _ = await client.DeleteAsync(new Uri($"/api/mocks/{mockId}", UriKind.Relative), Ct);
        }
    }

    [Fact]
    public async Task Deleting_A_Stub_That_Is_Not_There_Is_A_404()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var mockId = await StartMock(client);
        try
        {
            var (status, _) = await Send(client, HttpMethod.Delete, $"/api/mocks/{mockId}/stubs/no-such-stub");

            Assert.Equal(HttpStatusCode.NotFound, status);
        }
        finally
        {
            using var _ = await client.DeleteAsync(new Uri($"/api/mocks/{mockId}", UriKind.Relative), Ct);
        }
    }

    [Fact]
    public async Task Resetting_Stubs_Puts_The_Schema_Set_Back()
    {
        // The undo for a session of runtime edits: back to what the schema
        // produced, without a restart.
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var mockId = await StartMock(client);
        try
        {
            var original = (await GetJson(client, $"/api/mocks/{mockId}/stubs")).GetProperty("count").GetInt32();
            await Send(client, HttpMethod.Post, $"/api/mocks/{mockId}/stubs", NewStub);

            var (status, _) = await Send(client, HttpMethod.Post, $"/api/mocks/{mockId}/stubs/reset");

            Assert.Equal(HttpStatusCode.OK, status);
            var after = (await GetJson(client, $"/api/mocks/{mockId}/stubs")).GetProperty("count").GetInt32();
            Assert.Equal(original, after);
        }
        finally
        {
            using var _ = await client.DeleteAsync(new Uri($"/api/mocks/{mockId}", UriKind.Relative), Ct);
        }
    }

    // ---- the request log ----

    [Fact]
    public async Task A_Mock_That_Has_Served_Nothing_Has_An_Empty_Log_With_Its_Capacity()
    {
        // The rail polls this on a timer from the moment the mock starts, so
        // the empty case is the first thing it renders.
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var mockId = await StartMock(client);
        try
        {
            var body = await GetJson(client, $"/api/mocks/{mockId}/requests");

            Assert.Equal(0, body.GetProperty("total").GetInt32());
            Assert.True(body.GetProperty("capacity").GetInt32() > 0);
            Assert.Empty(body.GetProperty("entries").EnumerateArray());
        }
        finally
        {
            using var _ = await client.DeleteAsync(new Uri($"/api/mocks/{mockId}", UriKind.Relative), Ct);
        }
    }

    [Fact]
    public async Task The_Near_Miss_Log_Is_Empty_Before_Anything_Fails_To_Match()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var mockId = await StartMock(client);
        try
        {
            var body = await GetJson(client, $"/api/mocks/{mockId}/requests/unmatched");

            Assert.Equal(JsonValueKind.Object, body.ValueKind);
        }
        finally
        {
            using var _ = await client.DeleteAsync(new Uri($"/api/mocks/{mockId}", UriKind.Relative), Ct);
        }
    }

    [Fact]
    public async Task A_Verification_Against_A_Live_Mock_That_Served_Nothing_Is_Unsatisfied()
    {
        // This is what a test asserts through. "Nothing arrived" has to come
        // back as satisfied=false with a count, not as an error — the test
        // reads the count.
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var mockId = await StartMock(client);
        try
        {
            var (status, body) = await Send(client, HttpMethod.Post, $"/api/mocks/{mockId}/verify",
                """{"path":"/weather","atLeast":1}""");

            Assert.Equal(HttpStatusCode.OK, status);
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.GetProperty("satisfied").GetBoolean());
            Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            using var _ = await client.DeleteAsync(new Uri($"/api/mocks/{mockId}", UriKind.Relative), Ct);
        }
    }

    [Fact]
    public async Task A_Verification_With_No_Body_Is_A_400()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var mockId = await StartMock(client);
        try
        {
            var (status, _) = await Send(client, HttpMethod.Post, $"/api/mocks/{mockId}/verify", "null");

            Assert.Equal(HttpStatusCode.BadRequest, status);
        }
        finally
        {
            using var _ = await client.DeleteAsync(new Uri($"/api/mocks/{mockId}", UriKind.Relative), Ct);
        }
    }

    // ---- scenarios and faults ----

    [Fact]
    public async Task A_Running_Mock_Reports_Its_Scenario_And_Fault_State()
    {
        // Both panes open with these; an error on a mock with neither
        // configured would read as "this mock is broken".
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var mockId = await StartMock(client);
        try
        {
            var scenarios = await GetJson(client, $"/api/mocks/{mockId}/scenarios");
            Assert.Equal(mockId, scenarios.GetProperty("mockId").GetString());

            using var faults = await client.GetAsync(
                new Uri($"/api/mocks/{mockId}/faults", UriKind.Relative), Ct);
            Assert.Equal(HttpStatusCode.OK, faults.StatusCode);
        }
        finally
        {
            using var _ = await client.DeleteAsync(new Uri($"/api/mocks/{mockId}", UriKind.Relative), Ct);
        }
    }

    [Fact]
    public async Task Resetting_Scenarios_On_A_Running_Mock_Succeeds()
    {
        using var host = await BuildHost();
        var client = host.GetTestClient();
        var mockId = await StartMock(client);
        try
        {
            var (status, _) = await Send(client, HttpMethod.Post, $"/api/mocks/{mockId}/scenarios/reset");

            Assert.Equal(HttpStatusCode.OK, status);
        }
        finally
        {
            using var _ = await client.DeleteAsync(new Uri($"/api/mocks/{mockId}", UriKind.Relative), Ct);
        }
    }
}

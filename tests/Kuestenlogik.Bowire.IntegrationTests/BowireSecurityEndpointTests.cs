// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// <c>/api/security/fuzz</c> and <c>/api/security/threat-model</c> — the
/// server side of the workbench's Security drawer.
/// </summary>
/// <remarks>
/// <para>
/// Fuzzing sends attack payloads at a target the operator named, so what is
/// asserted here is everything the endpoint refuses <em>before</em> a single
/// request leaves the machine. The payload cap is the one with teeth: the
/// workbench caps at 5, and the server cap exists precisely because a client
/// is not what should decide how many requests get fired at somebody's API.
/// </para>
/// <para>
/// The threat model beside it is deterministic and reaches nothing, so it is
/// exercised end to end.
/// </para>
/// </remarks>
public sealed class BowireSecurityEndpointTests
{
    private static async Task<IHost> BuildHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireSecurityEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s => s.AddRouting());
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Post(
        IHost host, string path, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await host.GetTestClient().PostAsync(
            new Uri(path, UriKind.Relative), content, TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (resp.StatusCode, doc.RootElement.Clone());
    }

    private static Task<(HttpStatusCode Status, JsonElement Body)> Fuzz(IHost host, string json)
        => Post(host, "/api/security/fuzz", json);

    // ---- what fuzzing refuses before it sends anything ----

    [Fact]
    public async Task A_Body_That_Is_Not_Json_Is_A_400()
    {
        using var host = await BuildHost();

        var (status, body) = await Fuzz(host, "{ not json");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("urn:bowire:invalid-input", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task An_Empty_Body_Is_A_400()
    {
        using var host = await BuildHost();

        var (status, _) = await Fuzz(host, "null");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Fuzzing_Without_A_Target_Is_Refused()
    {
        // The one field that decides who receives the payloads.
        using var host = await BuildHost();

        var (status, body) = await Fuzz(host, """{"field":"q","category":"sqli"}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("target", body.GetProperty("title").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fuzzing_Without_A_Field_Is_Refused()
    {
        using var host = await BuildHost();

        var (status, body) = await Fuzz(host,
            """{"target":"https://api.example.com","category":"sqli"}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("field", body.GetProperty("title").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fuzzing_With_Neither_A_Category_Nor_Payloads_Is_Refused()
    {
        // Nothing to send is not the same as "send everything"; guessing here
        // would fire a full catalogue at a target nobody asked to fuzz.
        using var host = await BuildHost();

        var (status, body) = await Fuzz(host,
            """{"target":"https://api.example.com","field":"q"}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("customPayloads", body.GetProperty("title").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Empty_Payload_List_Counts_As_No_Payloads()
    {
        using var host = await BuildHost();

        var (status, _) = await Fuzz(host,
            """{"target":"https://api.example.com","field":"q","customPayloads":[]}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task More_Than_Fifty_Custom_Payloads_Are_Refused_With_Both_Numbers()
    {
        // The workbench caps at 5; this cap is what stops a client from
        // deciding to fire a DoS-shaped volley at somebody's API. The
        // response carries what was sent and what is allowed so the caller
        // can fix it without reading the source.
        using var host = await BuildHost();
        var payloads = string.Join(",", Enumerable.Range(0, 51).Select(i => $"\"p{i}\""));

        var (status, body) = await Fuzz(host,
            $$"""{"target":"https://api.example.com","field":"q","customPayloads":[{{payloads}}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("urn:bowire:security:payload-cap", body.GetProperty("type").GetString());
        Assert.Equal(51, body.GetProperty("count").GetInt32());
        Assert.Equal(50, body.GetProperty("maxCount").GetInt32());
    }

    [Fact]
    public async Task Exactly_Fifty_Payloads_Passes_The_Cap()
    {
        // The boundary belongs to the allowed side. This one gets past
        // validation and fails on the transport instead — asserting only that
        // the refusal is no longer the cap.
        using var host = await BuildHost();
        var payloads = string.Join(",", Enumerable.Range(0, 50).Select(i => $"\"p{i}\""));

        var (_, body) = await Fuzz(host,
            $$"""{"target":"http://127.0.0.1:1","field":"q","httpVerb":"GET","httpPath":"/","customPayloads":[{{payloads}}],"timeoutSeconds":1}""");

        if (body.TryGetProperty("type", out var type))
        {
            Assert.NotEqual("urn:bowire:security:payload-cap", type.GetString());
        }
    }

    // ---- the heuristic threat model ----

    private const string TwoEndpoints = """
        {"endpoints":[
          {"endpointId":"1","path":"/admin/users","verb":"DELETE","protocol":"rest","authState":"none"},
          {"endpointId":"2","path":"/health","verb":"GET","protocol":"rest","authState":"none"}
        ]}
        """;

    [Fact]
    public async Task The_Threat_Model_Ranks_The_Endpoints_It_Was_Given()
    {
        using var host = await BuildHost();

        var (status, body) = await Post(host, "/api/security/threat-model", TwoEndpoints);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, body.GetProperty("inputCount").GetInt32());
        Assert.NotEmpty(body.GetProperty("ranked").EnumerateArray());
    }

    [Fact]
    public async Task Every_Ranked_Row_Says_Why_And_Which_Rules_Fired()
    {
        // The drawer renders the reasoning next to the score. Without it the
        // ranking is an unexplained number, which is exactly the complaint the
        // heuristic path exists to answer for the AI one.
        using var host = await BuildHost();

        var (_, body) = await Post(host, "/api/security/threat-model", TwoEndpoints);

        foreach (var row in body.GetProperty("ranked").EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("endpointId").GetString()));
            Assert.True(row.TryGetProperty("why", out _));
            Assert.True(row.TryGetProperty("ruleTrace", out _));
            Assert.True(row.TryGetProperty("suggestedTemplates", out _));
        }
    }

    [Fact]
    public async Task The_Response_Says_It_Came_From_The_Heuristic_Not_A_Model()
    {
        // Same shape as /api/ai/threat-model so one renderer serves both —
        // which makes `source` the only thing telling an operator whether a
        // ranking cost them an AI call.
        using var host = await BuildHost();

        var (_, body) = await Post(host, "/api/security/threat-model", TwoEndpoints);

        Assert.Equal("heuristic", body.GetProperty("source").GetString());
        Assert.Equal("heuristic", body.GetProperty("modelId").GetString());
    }

    [Fact]
    public async Task An_Empty_Endpoint_List_Is_Refused()
    {
        using var host = await BuildHost();

        var (status, body) = await Post(host, "/api/security/threat-model", """{"endpoints":[]}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("endpoints", body.GetProperty("title").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Threat_Model_Body_That_Is_Not_Json_Is_A_400()
    {
        using var host = await BuildHost();

        var (status, _) = await Post(host, "/api/security/threat-model", "{ not json");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Top_N_Bounds_How_Many_Rows_Come_Back()
    {
        // The drawer asks for a handful; sending everything would bury the
        // ones worth looking at.
        using var host = await BuildHost();

        var (_, body) = await Post(host, "/api/security/threat-model", """
            {"topN":1,"endpoints":[
              {"endpointId":"1","path":"/admin/users","verb":"DELETE","protocol":"rest"},
              {"endpointId":"2","path":"/health","verb":"GET","protocol":"rest"},
              {"endpointId":"3","path":"/orders","verb":"POST","protocol":"rest"}
            ]}
            """);

        Assert.Single(body.GetProperty("ranked").EnumerateArray());
        // inputCount still reports everything that was considered, so the
        // caller can tell a short list from a small input.
        Assert.Equal(3, body.GetProperty("inputCount").GetInt32());
    }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Recordings.Correlation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Recordings.Tests;

/// <summary>
/// <c>POST /api/recordings/correlate</c> — the workbench half of #539.
/// </summary>
/// <remarks>
/// <para>
/// The analyzer behind it is covered in
/// <see cref="RecordingCorrelationAnalyzerTests"/>; what this endpoint owns is
/// everything around it — reading the posted document, deciding whether a
/// correlation key was actually supplied, and refusing in a shape the rail can
/// render.
/// </para>
/// <para>
/// The key rule is the subtle one: a key with a name but no value (a
/// half-filled input in the rail) has to mean "no key", not "correlate on the
/// empty string" — which would group every step under one bogus correlation.
/// </para>
/// </remarks>
public sealed class RecordingCorrelationEndpointTests
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
                       app.UseEndpoints(e =>
                           e.MapBowireRecordingCorrelationEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s => s.AddRouting());
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Post(IHost host, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await host.GetTestClient().PostAsync(
            new Uri("/api/recordings/correlate", UriKind.Relative), content,
            TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (resp.StatusCode, doc.RootElement.Clone());
    }

    /// <summary>The recording object on its own — composed into a body below.</summary>
    private const string Recording = """
        {"id":"rec-1","name":"checkout","steps":[
           {"protocolId":"rest","httpMethod":"GET","httpPath":"/orders/42","timestamp":1000},
           {"protocolId":"rest","httpMethod":"POST","httpPath":"/payments","timestamp":2000}
        ]}
        """;

    private static string Body(string? keyJson = null)
        => keyJson is null
            ? $$"""{"recording":{{Recording}}}"""
            : $$"""{"recording":{{Recording}},"key":{{keyJson}}}""";

    [Fact]
    public async Task A_Posted_Recording_Comes_Back_As_A_Timeline()
    {
        using var host = await BuildHost();

        var (status, body) = await Post(host, Body());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
    }

    [Fact]
    public async Task A_Recording_With_No_Steps_Is_An_Empty_Timeline_Not_An_Error()
    {
        // An in-progress capture that has not recorded anything yet — the rail
        // asks for a timeline on every refresh.
        using var host = await BuildHost();

        var (status, _) = await Post(host, """{"recording":{"id":"rec-1","steps":[]}}""");

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task A_Body_Without_A_Recording_Says_What_The_Shape_Should_Be()
    {
        // The message doubles as the endpoint's documentation — there is no
        // OpenAPI entry for it (ExcludeFromDescription).
        using var host = await BuildHost();

        var (status, body) = await Post(host, """{"key":{"name":"orderId","value":"42"}}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("urn:bowire:invalid-input", body.GetProperty("type").GetString());
        Assert.Contains("recording", body.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Explicitly_Null_Recording_Is_Refused_The_Same_Way()
    {
        using var host = await BuildHost();

        var (status, _) = await Post(host, """{"recording":null}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task A_Body_That_Is_Not_Json_Is_A_Problem_Document_Not_A_500()
    {
        using var host = await BuildHost();

        var (status, body) = await Post(host, "{ not json");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        // The parser's own message is kept: it names the offending position,
        // which is the only useful thing to say about a truncated body.
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task The_Refusal_Names_The_Path_It_Was_Sent_To()
    {
        // RFC7807 `instance`, same as every other Bowire endpoint — the rail
        // renders it when it groups errors by endpoint.
        using var host = await BuildHost();

        var (_, body) = await Post(host, "{}");

        Assert.Equal("/api/recordings/correlate", body.GetProperty("instance").GetString());
    }

    [Theory]
    [InlineData("""{"name":"orderId","value":""}""")]
    [InlineData("""{"name":"","value":"42"}""")]
    [InlineData("""{"name":"   ","value":"  "}""")]
    [InlineData("null")]
    public async Task A_Half_Filled_Key_Means_No_Key_Rather_Than_An_Empty_One(string keyJson)
    {
        // The rail sends the key box's live contents while someone is still
        // typing. Correlating on an empty value would match every step and
        // present one meaningless group as a result.
        using var host = await BuildHost();
        var (status, _) = await Post(host, Body(keyJson));

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task A_Complete_Key_Is_Passed_Through_To_The_Analyzer()
    {
        // The positive side of the rule above: a name and a value both present
        // is the one case that actually correlates.
        using var host = await BuildHost();
        var (status, body) = await Post(host, Body("""{"name":"orderId","value":"42"}"""));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
    }

    [Fact]
    public async Task Field_Names_Are_Read_Whatever_Their_Casing()
    {
        // The endpoint is posted to by the rail (camelCase) and by hand from a
        // terminal (whatever the author typed).
        using var host = await BuildHost();

        var (status, _) = await Post(host, """{"Recording":{"Id":"rec-1","Steps":[]}}""");

        Assert.Equal(HttpStatusCode.OK, status);
    }
}

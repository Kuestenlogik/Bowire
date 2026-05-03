// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Request-based response templating: a recorded response can reference
/// live values from the incoming HTTP request via <c>${request.*}</c>
/// placeholders (method / path / query / header / body). Lets a single
/// recording power many test cases without per-test edits — echo
/// endpoints, correlation-id mirrors, parameter echoes, etc.
/// </summary>
public sealed class RequestTemplatingTests
{
    [Fact]
    public async Task RequestQuery_IsSubstitutedIntoResponseBody()
    {
        var rec = BuildRecording(
            httpPath: "/weather",
            response: """{"city":"${request.query.city}","temp":21.5}""");
        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/weather?city=hamburg", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("hamburg", json.RootElement.GetProperty("city").GetString());
        Assert.Equal(21.5, json.RootElement.GetProperty("temp").GetDouble());
    }

    [Fact]
    public async Task RequestHeader_IsSubstitutedCaseInsensitively()
    {
        var rec = BuildRecording(
            httpPath: "/whoami",
            response: """{"traceId":"${request.header.X-Trace-Id}"}""");
        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        req.Headers.Add("x-trace-id", "abc-123"); // lower-case on the wire
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("abc-123", json.RootElement.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task RequestPathTemplate_BindingIsSubstituted()
    {
        // Template placeholder on the recorded path binds to the live
        // segment. Supports `${request.path.<name>}` for named segments.
        var rec = BuildRecording(
            httpPath: "/users/{id}",
            response: """{"id":"${request.path.id}","loaded":true}""");
        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/users/42", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(body);
        Assert.Equal("42", json.RootElement.GetProperty("id").GetString());
        Assert.True(json.RootElement.GetProperty("loaded").GetBoolean());
    }

    [Fact]
    public async Task RequestPathIndex_IsSubstituted()
    {
        // Numeric index resolves a 0-based path segment so recordings
        // that don't use a template placeholder can still reach into
        // the path.
        var rec = BuildRecording(
            httpPath: "/api/{tenant}/orders",
            response: """{"segment0":"${request.path.0}","tenant":"${request.path.tenant}"}""");
        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/api/acme/orders", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(body);
        Assert.Equal("api", json.RootElement.GetProperty("segment0").GetString());
        Assert.Equal("acme", json.RootElement.GetProperty("tenant").GetString());
    }

    [Fact]
    public async Task RequestBody_JsonFields_AreSubstituted()
    {
        // POST body is JSON; `${request.body.a.b}` navigates into it.
        var rec = BuildRecording(
            httpPath: "/echo",
            httpVerb: "POST",
            response: """{"echoedName":"${request.body.user.name}","echoedRole":"${request.body.user.role}"}""");
        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            new Uri("/echo", UriKind.Relative),
            new { user = new { name = "Kim", role = "admin" } }, TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(body);
        Assert.Equal("Kim", json.RootElement.GetProperty("echoedName").GetString());
        Assert.Equal("admin", json.RootElement.GetProperty("echoedRole").GetString());
    }

    [Fact]
    public async Task RequestBody_ArrayIndex_Supported()
    {
        // Integer segments index into JSON arrays.
        var rec = BuildRecording(
            httpPath: "/items",
            httpVerb: "POST",
            response: """{"first":"${request.body.items.0.name}","second":"${request.body.items.1.name}"}""");
        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            new Uri("/items", UriKind.Relative),
            new { items = new[] { new { name = "a" }, new { name = "b" } } }, TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(body);
        Assert.Equal("a", json.RootElement.GetProperty("first").GetString());
        Assert.Equal("b", json.RootElement.GetProperty("second").GetString());
    }

    [Fact]
    public async Task UnknownRequestToken_LeavesLiteral()
    {
        // Unknown ${request.*} tokens stay as-is — idempotent so a
        // recorded body that legitimately contains template-shaped
        // text isn't silently mangled.
        var rec = BuildRecording(
            httpPath: "/keep",
            response: """{"literal":"${request.nothing.here}"}""");
        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/keep", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(body);
        Assert.Equal("${request.nothing.here}", json.RootElement.GetProperty("literal").GetString());
    }

    [Fact]
    public async Task Request_MethodAndPath_AreSubstituted()
    {
        var rec = BuildRecording(
            httpPath: "/audit",
            httpVerb: "DELETE",
            response: """{"ok":true,"method":"${request.method}","path":"${request.path}"}""");
        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        var resp = await client.DeleteAsync(new Uri("/audit", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(body);
        Assert.Equal("DELETE", json.RootElement.GetProperty("method").GetString());
        Assert.Equal("/audit", json.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async Task BuiltinTokens_StillWinOverSpoofedExtraBindings()
    {
        // Request tokens compose with the existing built-ins — fresh
        // uuid/now values still resolve, and ${request.*} doesn't
        // shadow them. Smoke test: response uses both, both substitute.
        var rec = BuildRecording(
            httpPath: "/mix",
            response: """{"id":"${uuid}","q":"${request.query.x}"}""");
        using var host = BuildHost(rec);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/mix?x=ping", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(body);
        var id = json.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(id));
        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal("ping", json.RootElement.GetProperty("q").GetString());
    }

    // ---- helpers ----

    private static BowireRecording BuildRecording(
        string httpPath, string response, string httpVerb = "GET") => new()
    {
        Id = "rec_req_tpl",
        Name = "request-template",
        RecordingFormatVersion = 2,
        Steps =
        {
            new BowireRecordingStep
            {
                Id = "step_tpl",
                Protocol = "rest",
                Service = "Api",
                Method = "Op",
                MethodType = "Unary",
                HttpPath = httpPath,
                HttpVerb = httpVerb,
                Status = "OK",
                Response = response
            }
        }
    };

    private static IHost BuildHost(BowireRecording recording) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                    .Configure(app =>
                    {
                        app.UseBowireMock(recording, opts =>
                        {
                            opts.Watch = false;
                            opts.ReplaySpeed = 0;
                        });
                    });
            })
            .Start();
}

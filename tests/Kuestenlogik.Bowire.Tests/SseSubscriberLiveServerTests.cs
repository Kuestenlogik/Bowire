// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.Sse;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Hosts a tiny in-process Kestrel SSE endpoint and exercises the full
/// <c>SseSubscriber</c> + <c>BowireSseProtocol</c> streaming path. Sits one
/// notch above pure unit tests but stays in this project because the
/// fixture is a single ~10-line Kestrel app — no external infrastructure,
/// no flaky network dependencies. Lifts <c>SseSubscriber</c> from 0% to
/// "good enough" by covering the event parser, comment lines, multi-line
/// data fields, and the retry+id+event header path.
/// </summary>
[Collection("Sse")]
public sealed class SseSubscriberLiveServerTests : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _baseUrl;

    public SseSubscriberLiveServerTests()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.ConfigureKestrel((Action<KestrelServerOptions>)(o => o.Listen(IPAddress.Loopback, 0)));
        builder.Logging.ClearProviders();

        _app = builder.Build();

        // Three event-stream endpoints covering the cases SseSubscriber's
        // parser needs to walk:
        //   /events/simple   → one event with id+event+data+retry
        //   /events/multi    → one event whose data spans multiple lines
        //   /events/comments → one event preceded by ":heartbeat" comments
        _app.MapGet("/events/simple", WriteSimple);
        _app.MapGet("/events/multi", WriteMultiline);
        _app.MapGet("/events/comments", WriteComments);

        _app.Start();

        var addressFeature = _app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        _baseUrl = addressFeature!.Addresses.First();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static async Task WriteSimple(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/event-stream";
        await ctx.Response.WriteAsync(
            "id: 42\nevent: tick\nretry: 1500\ndata: hello world\n\n",
            ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }

    private static async Task WriteMultiline(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/event-stream";
        // Two data: lines should be joined with a newline; no event/id/retry.
        await ctx.Response.WriteAsync(
            "data: line1\ndata: line2\n\n",
            ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }

    private static async Task WriteComments(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/event-stream";
        // Comment lines start with ':' — must be ignored by the parser.
        // Then a real event after a heartbeat. The retry: line with garbage
        // exercises the int-parse failure branch (kept null on failure).
        await ctx.Response.WriteAsync(
            ": heartbeat\nretry: notanumber\ndata: payload\n\n",
            ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }

    [Fact]
    public async Task SubscribeAsync_Parses_Full_Event_Fields()
    {
        var protocol = new BowireSseProtocol();

        var events = new List<string>();
        await foreach (var evt in protocol.SubscribeAsync(
            _baseUrl + "/events/simple",
            headers: null,
            TestContext.Current.CancellationToken))
        {
            events.Add(evt);
            break; // server closes after one event
        }

        var json = Assert.Single(events);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("42", doc.RootElement.GetProperty("Id").GetString());
        Assert.Equal("tick", doc.RootElement.GetProperty("Event").GetString());
        Assert.Equal("hello world", doc.RootElement.GetProperty("Data").GetString());
        Assert.Equal(1500, doc.RootElement.GetProperty("Retry").GetInt32());
    }

    [Fact]
    public async Task SubscribeAsync_Joins_MultiLine_Data_With_Newline()
    {
        var protocol = new BowireSseProtocol();

        var events = new List<string>();
        await foreach (var evt in protocol.SubscribeAsync(
            _baseUrl + "/events/multi",
            headers: null,
            TestContext.Current.CancellationToken))
        {
            events.Add(evt);
            break;
        }

        var json = Assert.Single(events);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("line1\nline2", doc.RootElement.GetProperty("Data").GetString());
    }

    [Fact]
    public async Task SubscribeAsync_Skips_Comments_And_Tolerates_Bad_Retry()
    {
        var protocol = new BowireSseProtocol();

        var events = new List<string>();
        await foreach (var evt in protocol.SubscribeAsync(
            _baseUrl + "/events/comments",
            headers: null,
            TestContext.Current.CancellationToken))
        {
            events.Add(evt);
            break;
        }

        var json = Assert.Single(events);
        using var doc = JsonDocument.Parse(json);
        // Comment line ":heartbeat" produced no event; the only emission is
        // the data: payload event.
        Assert.Equal("payload", doc.RootElement.GetProperty("Data").GetString());
        // Retry was unparseable → null kept.
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("Retry").ValueKind);
    }

    [Fact]
    public async Task SubscribeAsync_Forwards_Custom_Headers_To_Server()
    {
        // The server doesn't echo the header back, but we can confirm the
        // happy path doesn't break when extra headers are passed in.
        var protocol = new BowireSseProtocol();
        var headers = new Dictionary<string, string>
        {
            ["X-Trace-Id"] = "test-123",
            ["Authorization"] = "Bearer abc",
        };

        var events = new List<string>();
        await foreach (var evt in protocol.SubscribeAsync(
            _baseUrl + "/events/simple",
            headers: headers,
            TestContext.Current.CancellationToken))
        {
            events.Add(evt);
            break;
        }

        Assert.Single(events);
    }

    [Fact]
    public async Task InvokeStreamAsync_With_Method_FullName_Resolves_Path()
    {
        // BowireSseProtocol.InvokeStreamAsync builds the URL by stripping the
        // "SSE" prefix from the method full-name and appending it to serverUrl.
        var protocol = new BowireSseProtocol();

        var events = new List<string>();
        await foreach (var evt in protocol.InvokeStreamAsync(
            _baseUrl,
            service: "SSE Endpoints",
            method: "SSE/events/simple",
            jsonMessages: [],
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken))
        {
            events.Add(evt);
            break;
        }

        Assert.Single(events);
    }

    [Fact]
    public async Task InvokeStreamAsync_Body_Override_Replaces_Url()
    {
        // When jsonMessages[0] contains {"url": "/different/path"} the
        // ResolveUrl helper is supposed to swap the path but reuse the
        // server URL. Use that to redirect to /events/multi.
        var protocol = new BowireSseProtocol();

        var events = new List<string>();
        await foreach (var evt in protocol.InvokeStreamAsync(
            _baseUrl,
            service: "SSE Endpoints",
            method: "SSE/events/simple",
            jsonMessages: ["{\"url\":\"/events/multi\"}"],
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken))
        {
            events.Add(evt);
            break;
        }

        var json = Assert.Single(events);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("line1\nline2", doc.RootElement.GetProperty("Data").GetString());
    }

    [Fact]
    public async Task InvokeStreamAsync_Body_Override_Absolute_Url_Used_As_Is()
    {
        var protocol = new BowireSseProtocol();
        var absoluteUrl = _baseUrl + "/events/simple";

        var events = new List<string>();
        await foreach (var evt in protocol.InvokeStreamAsync(
            "http://wrong-host:1/will-be-ignored",
            service: "SSE Endpoints",
            method: "SSE/events/x",
            jsonMessages: ["{\"url\":\"" + absoluteUrl + "\"}"],
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken))
        {
            events.Add(evt);
            break;
        }

        Assert.Single(events);
    }

    [Fact]
    public async Task InvokeStreamAsync_Malformed_Json_Body_Falls_Back_To_Default()
    {
        // ResolveUrl swallows malformed JSON and falls through to the
        // default path. Verify the fallback succeeds against /events/simple.
        var protocol = new BowireSseProtocol();

        var events = new List<string>();
        await foreach (var evt in protocol.InvokeStreamAsync(
            _baseUrl,
            service: "SSE Endpoints",
            method: "SSE/events/simple",
            jsonMessages: ["{not valid json"],
            showInternalServices: false,
            metadata: null,
            TestContext.Current.CancellationToken))
        {
            events.Add(evt);
            break;
        }

        Assert.Single(events);
    }
}

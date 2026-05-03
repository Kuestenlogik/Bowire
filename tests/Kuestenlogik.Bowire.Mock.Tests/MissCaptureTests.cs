// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Phase 3c: unmatched REST requests are persisted to a capture file as
/// placeholder recording steps. Covers the first-miss (file creation),
/// subsequent-miss (append), body + metadata capture, and the gRPC-skip
/// path.
/// </summary>
public sealed class MissCaptureTests : IDisposable
{
    private readonly string _tempDir;

    public MissCaptureTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-mock-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task FirstMiss_CreatesRecordingWithOneStep()
    {
        var capturePath = Path.Combine(_tempDir, "misses.json");
        using var host = BuildHost(EmptyRecording(), capturePath);
        var client = host.GetTestClient();

        var resp = await client.GetAsync(new Uri("/unknown/123", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal((System.Net.HttpStatusCode)418, resp.StatusCode); // teapot fallthrough

        Assert.True(File.Exists(capturePath), "capture file should have been created on first miss");

        var captured = ReadCapture(capturePath);
        Assert.Single(captured.Steps);
        Assert.Equal("rest", captured.Steps[0].Protocol);
        Assert.Equal("GET", captured.Steps[0].HttpVerb);
        Assert.Equal("/unknown/123", captured.Steps[0].HttpPath);
        Assert.Equal("Unknown", captured.Steps[0].Status);
    }

    [Fact]
    public async Task MultipleMisses_AppendSteps()
    {
        var capturePath = Path.Combine(_tempDir, "misses.json");
        using var host = BuildHost(EmptyRecording(), capturePath);
        var client = host.GetTestClient();

        _ = await client.GetAsync(new Uri("/a", UriKind.Relative), TestContext.Current.CancellationToken);
        _ = await client.GetAsync(new Uri("/b", UriKind.Relative), TestContext.Current.CancellationToken);
        using var jsonBody = new StringContent("""{"hello":"world"}""", Encoding.UTF8, "application/json");
        _ = await client.PostAsync(new Uri("/c", UriKind.Relative), jsonBody, TestContext.Current.CancellationToken);

        var captured = ReadCapture(capturePath);
        Assert.Equal(3, captured.Steps.Count);
        Assert.Equal("/a", captured.Steps[0].HttpPath);
        Assert.Equal("/b", captured.Steps[1].HttpPath);
        Assert.Equal("/c", captured.Steps[2].HttpPath);
        Assert.Contains("hello", captured.Steps[2].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Miss_PersistsHeadersAsMetadata()
    {
        var capturePath = Path.Combine(_tempDir, "misses.json");
        using var host = BuildHost(EmptyRecording(), capturePath);
        var client = host.GetTestClient();

        client.DefaultRequestHeaders.Add("X-Request-ID", "abc-123");

        _ = await client.GetAsync(new Uri("/x", UriKind.Relative), TestContext.Current.CancellationToken);

        var captured = ReadCapture(capturePath);
        Assert.Single(captured.Steps);
        Assert.NotNull(captured.Steps[0].Metadata);
        Assert.Equal("abc-123", captured.Steps[0].Metadata!["X-Request-ID"]);
    }

    [Fact]
    public async Task GrpcMiss_CapturesServiceMethodAndRequestBinary()
    {
        // Build a gRPC-shaped request context directly: POST, content-type
        // application/grpc, body = 5-byte envelope + payload. The writer
        // should split the :path into service/method, base64 the payload
        // into requestBinary (header stripped), and leave ResponseBinary
        // null for the user to fill in.
        var capturePath = Path.Combine(_tempDir, "grpc-misses.json");
        var payload = new byte[] { 0x0A, 0x04, 0x74, 0x65, 0x73, 0x74 }; // a varint tag + "test"
        var framed = new byte[5 + payload.Length];
        framed[0] = 0; // no compression
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(1, 4), (uint)payload.Length);
        payload.CopyTo(framed, 5);

        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/echo.Echoer/Echo";
        ctx.Request.ContentType = "application/grpc";
        ctx.Request.Headers["grpc-timeout"] = "30S";
        ctx.Request.Body = new MemoryStream(framed);

        await Capture.MissCaptureWriter.CaptureAsync(
            capturePath, ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            TestContext.Current.CancellationToken);

        var captured = ReadCapture(capturePath);
        Assert.Single(captured.Steps);

        var step = captured.Steps[0];
        Assert.Equal("grpc", step.Protocol);
        Assert.Equal("echo.Echoer", step.Service);
        Assert.Equal("Echo", step.Method);
        Assert.Equal("Unary", step.MethodType);
        Assert.Equal("Unknown", step.Status);

        // requestBinary carries just the payload, not the 5-byte envelope.
        Assert.NotNull(step.RequestBinary);
        var decoded = Convert.FromBase64String(step.RequestBinary!);
        Assert.Equal(payload, decoded);

        // responseBinary is the slot the user fills in — left null on capture.
        Assert.Null(step.ResponseBinary);

        // grpc-timeout landed in metadata so the user can see what the
        // client requested.
        Assert.NotNull(step.Metadata);
        Assert.Equal("30S", step.Metadata!["grpc-timeout"]);
    }

    [Fact]
    public async Task GrpcMiss_MalformedBody_StillCapturesStepWithoutRequestBinary()
    {
        // Shorter than 5 bytes — no valid envelope. Writer should still
        // produce a step so the user knows the path was hit; the
        // RequestBinary ends up null.
        var capturePath = Path.Combine(_tempDir, "malformed.json");
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/svc/op";
        ctx.Request.ContentType = "application/grpc";
        ctx.Request.Body = new MemoryStream(new byte[] { 0x00, 0x01 });

        await Capture.MissCaptureWriter.CaptureAsync(
            capturePath, ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            TestContext.Current.CancellationToken);

        var captured = ReadCapture(capturePath);
        Assert.Single(captured.Steps);
        var step = captured.Steps[0];
        Assert.Equal("grpc", step.Protocol);
        Assert.Equal("svc", step.Service);
        Assert.Equal("op", step.Method);
        Assert.Null(step.RequestBinary);
    }

    [Fact]
    public async Task MatchedRequest_DoesNotCapture()
    {
        // A request that matches a recorded step shouldn't land in the
        // miss file — the capture is strictly for *misses*.
        var capturePath = Path.Combine(_tempDir, "misses.json");
        var recording = new BowireRecording
        {
            Id = "rec_match",
            Name = "match",
            RecordingFormatVersion = 2,
            Steps =
            {
                new BowireRecordingStep
                {
                    Id = "step_ping",
                    Protocol = "rest",
                    Service = "Ping",
                    Method = "Ping",
                    MethodType = "Unary",
                    HttpPath = "/ping",
                    HttpVerb = "GET",
                    Status = "OK",
                    Response = "pong"
                }
            }
        };

        using var host = BuildHost(recording, capturePath);
        var client = host.GetTestClient();

        _ = await client.GetAsync(new Uri("/ping", UriKind.Relative), TestContext.Current.CancellationToken);    // matched → skip
        _ = await client.GetAsync(new Uri("/unknown", UriKind.Relative), TestContext.Current.CancellationToken); // miss → captured

        var captured = ReadCapture(capturePath);
        Assert.Single(captured.Steps);
        Assert.Equal("/unknown", captured.Steps[0].HttpPath);
    }

    // ---- helpers ----

    private static BowireRecording EmptyRecording() => new()
    {
        Id = "rec_empty",
        Name = "empty",
        RecordingFormatVersion = 2,
        Steps =
        {
            new BowireRecordingStep
            {
                Id = "sentinel",
                Protocol = "rest",
                Service = "Sentinel",
                Method = "Sentinel",
                MethodType = "Unary",
                HttpPath = "/__sentinel",
                HttpVerb = "GET",
                Status = "OK",
                Response = "sentinel"
            }
        }
    };

    private static readonly JsonSerializerOptions s_readOptions =
        new() { PropertyNameCaseInsensitive = true };

    private static BowireRecording ReadCapture(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BowireRecording>(json, s_readOptions)!;
    }

    private static IHost BuildHost(BowireRecording recording, string capturePath)
    {
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                    .Configure(app =>
                    {
                        app.UseBowireMock(recording, opts =>
                        {
                            opts.Watch = false;
                            opts.PassThroughOnMiss = true;
                            opts.ReplaySpeed = 0;
                            opts.CaptureMissPath = capturePath;
                        });
                        app.Run(async ctx =>
                        {
                            ctx.Response.StatusCode = 418;
                            await ctx.Response.WriteAsync("fallthrough");
                        });
                    });
            })
            .Start();
    }
}

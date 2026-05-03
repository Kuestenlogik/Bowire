// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Round-trip gRPC replay tests. Starts a real <see cref="MockServer"/> on
/// a high port, invokes a dynamically-constructed unary method with
/// <see cref="GrpcChannel"/>, and verifies the mock replays the recorded
/// wire bytes with matching gRPC framing + trailers.
///
/// Uses <see cref="StringValue"/> from <c>Google.Protobuf.WellKnownTypes</c>
/// as the request/response type so no <c>.proto</c> codegen is required.
/// </summary>
public sealed class GrpcReplayTests : IDisposable
{
    static GrpcReplayTests()
    {
        // Grpc.Net.Client refuses HTTP/2 over plaintext by default; the mock
        // tests run unencrypted on loopback, so opt in explicitly.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    private readonly string _tempDir;

    public GrpcReplayTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-mock-grpc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private static readonly Marshaller<StringValue> StringValueMarshaller = Marshallers.Create(
        serializer: v => v.ToByteArray(),
        deserializer: bytes => StringValue.Parser.ParseFrom(bytes));

    private static Method<StringValue, StringValue> CreateMethod(string service, string method)
        => new(MethodType.Unary, service, method, StringValueMarshaller, StringValueMarshaller);

    // MockServer now accepts Port = 0 (OS-assigned) and exposes the bound
    // port via MockServer.Port. Tests use 0 to dodge port-collision flakes
    // when run in parallel.

    private string WriteRecording(string responseValue, string service, string method)
    {
        var responseBytes = new StringValue { Value = responseValue }.ToByteArray();
        var responseBinaryBase64 = Convert.ToBase64String(responseBytes);

        var recording = new
        {
            id = "rec_grpc_test",
            name = "grpc test",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_" + Guid.NewGuid().ToString("N")[..8],
                    protocol = "grpc",
                    service,
                    method,
                    methodType = "Unary",
                    status = "OK",
                    response = "{\"value\":\"" + responseValue + "\"}",
                    responseBinary = responseBinaryBase64
                }
            }
        };

        var path = Path.Combine(_tempDir, "recording.json");
        File.WriteAllText(path, JsonSerializer.Serialize(recording));
        return path;
    }

    [Fact]
    public async Task UnaryCall_ReceivesRecordedResponseBytesVerbatim()
    {
        var path = WriteRecording("world", "echo.Echoer", "Echo");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                Watch = false,
                HostingExtensions = new IBowireMockHostingExtension[] { new GrpcMockHostingExtension() }
            },
            TestContext.Current.CancellationToken);

        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{server.Port}");
        var invoker = channel.CreateCallInvoker();
        var method = CreateMethod("echo.Echoer", "Echo");

        var response = await invoker.AsyncUnaryCall(
            method,
            host: null,
            new CallOptions(),
            new StringValue { Value = "hello" });

        Assert.Equal("world", response.Value);
    }

    [Fact]
    public async Task ServerStreaming_ReplaysRecordedFramesInOrder()
    {
        // Phase 2d: record a dummy server-streaming call with three frames,
        // verify Grpc.Net.Client receives all three via a streaming RPC with
        // the original values.
        var frame0 = new StringValue { Value = "alpha" }.ToByteArray();
        var frame1 = new StringValue { Value = "beta" }.ToByteArray();
        var frame2 = new StringValue { Value = "gamma" }.ToByteArray();

        var recording = new
        {
            id = "rec_stream",
            name = "grpc stream",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_stream",
                    protocol = "grpc",
                    service = "stream.Counter",
                    method = "Count",
                    methodType = "ServerStreaming",
                    status = "OK",
                    response = "\"gamma\"",
                    receivedMessages = new object[]
                    {
                        new { index = 0, timestampMs = 0, data = "\"alpha\"", responseBinary = Convert.ToBase64String(frame0) },
                        new { index = 1, timestampMs = 5, data = "\"beta\"",  responseBinary = Convert.ToBase64String(frame1) },
                        new { index = 2, timestampMs = 10, data = "\"gamma\"", responseBinary = Convert.ToBase64String(frame2) }
                    }
                }
            }
        };

        var path = Path.Combine(_tempDir, "stream.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0, HostingExtensions = new IBowireMockHostingExtension[] { new GrpcMockHostingExtension() } },
            TestContext.Current.CancellationToken);

        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{server.Port}");
        var invoker = channel.CreateCallInvoker();

        var method = new Method<StringValue, StringValue>(
            MethodType.ServerStreaming,
            "stream.Counter",
            "Count",
            StringValueMarshaller,
            StringValueMarshaller);

        using var call = invoker.AsyncServerStreamingCall(
            method, host: null, new CallOptions(),
            new StringValue { Value = "trigger" });

        var received = new List<string>();
        while (await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken))
        {
            received.Add(call.ResponseStream.Current.Value);
        }

        Assert.Equal(3, received.Count);
        Assert.Equal("alpha", received[0]);
        Assert.Equal("beta", received[1]);
        Assert.Equal("gamma", received[2]);
    }

    [Fact]
    public async Task ClientStreaming_ReceivesRecordedResponseAfterDrainingRequests()
    {
        // Phase 2i: record a client-streaming call. Client sends three
        // StringValue frames, mock drains them all, then replies with
        // the single recorded response plus grpc-status OK.
        var responseBytes = new StringValue { Value = "aggregated" }.ToByteArray();
        var recording = new
        {
            id = "rec_client_stream",
            name = "grpc client stream",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_client_stream",
                    protocol = "grpc",
                    service = "agg.Aggregator",
                    method = "Collect",
                    methodType = "ClientStreaming",
                    status = "OK",
                    response = "\"aggregated\"",
                    responseBinary = Convert.ToBase64String(responseBytes)
                }
            }
        };

        var path = Path.Combine(_tempDir, "client-stream.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0, HostingExtensions = new IBowireMockHostingExtension[] { new GrpcMockHostingExtension() } },
            TestContext.Current.CancellationToken);

        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{server.Port}");
        var invoker = channel.CreateCallInvoker();

        var method = new Method<StringValue, StringValue>(
            MethodType.ClientStreaming,
            "agg.Aggregator",
            "Collect",
            StringValueMarshaller,
            StringValueMarshaller);

        using var call = invoker.AsyncClientStreamingCall(
            method, host: null, new CallOptions());

        await call.RequestStream.WriteAsync(new StringValue { Value = "one" }, TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new StringValue { Value = "two" }, TestContext.Current.CancellationToken);
        await call.RequestStream.WriteAsync(new StringValue { Value = "three" }, TestContext.Current.CancellationToken);
        await call.RequestStream.CompleteAsync();

        var response = await call.ResponseAsync;
        Assert.Equal("aggregated", response.Value);
    }

    [Fact]
    public async Task BidiStreaming_InterleavesRecvAndSendWithInputGating()
    {
        // Phase 2i: bidi timeline with alternating recv/send frames.
        // Timeline: recv@0 → send@10 → recv@20 → send@30 → recv@40.
        // Without input-gating the three recv frames would race ahead
        // of the two client sends. With gating they arrive one-per-
        // send so the client sees [a, write, b, write, c].
        byte[] Bytes(string s) => new StringValue { Value = s }.ToByteArray();
        var recording = new
        {
            id = "rec_bidi",
            name = "grpc bidi gating",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_bidi",
                    protocol = "grpc",
                    service = "chat.Chatter",
                    method = "Talk",
                    methodType = "Duplex",
                    status = "OK",
                    response = "\"c\"",
                    receivedMessages = new object[]
                    {
                        new { index = 0, timestampMs = 0,  data = "\"a\"", responseBinary = Convert.ToBase64String(Bytes("a")) },
                        new { index = 1, timestampMs = 20, data = "\"b\"", responseBinary = Convert.ToBase64String(Bytes("b")) },
                        new { index = 2, timestampMs = 40, data = "\"c\"", responseBinary = Convert.ToBase64String(Bytes("c")) }
                    },
                    sentMessages = new object[]
                    {
                        new { index = 0, timestampMs = 10, body = "\"s1\"" },
                        new { index = 1, timestampMs = 30, body = "\"s2\"" }
                    }
                }
            }
        };

        var path = Path.Combine(_tempDir, "bidi.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0, HostingExtensions = new IBowireMockHostingExtension[] { new GrpcMockHostingExtension() } },
            TestContext.Current.CancellationToken);

        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{server.Port}");
        var invoker = channel.CreateCallInvoker();

        var method = new Method<StringValue, StringValue>(
            MethodType.DuplexStreaming,
            "chat.Chatter",
            "Talk",
            StringValueMarshaller,
            StringValueMarshaller);

        using var call = invoker.AsyncDuplexStreamingCall(
            method, host: null, new CallOptions());

        // First recv before any send.
        Assert.True(await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken));
        Assert.Equal("a", call.ResponseStream.Current.Value);

        // Before sending, the second recv must be blocked by the gate.
        // Assert by racing the MoveNext against a short delay.
        var moveSecond = call.ResponseStream.MoveNext(TestContext.Current.CancellationToken);
        var winner = await Task.WhenAny(moveSecond, Task.Delay(200, TestContext.Current.CancellationToken));
        Assert.NotSame(moveSecond, winner);

        await call.RequestStream.WriteAsync(new StringValue { Value = "s1" }, TestContext.Current.CancellationToken);
        await moveSecond.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("b", call.ResponseStream.Current.Value);

        var moveThird = call.ResponseStream.MoveNext(TestContext.Current.CancellationToken);
        winner = await Task.WhenAny(moveThird, Task.Delay(200, TestContext.Current.CancellationToken));
        Assert.NotSame(moveThird, winner);

        await call.RequestStream.WriteAsync(new StringValue { Value = "s2" }, TestContext.Current.CancellationToken);
        await moveThird.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("c", call.ResponseStream.Current.Value);

        await call.RequestStream.CompleteAsync();

        // No more frames, stream terminates with OK.
        Assert.False(await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GrpcMissCapture_PersistsStepAfterUnknownMethod()
    {
        // End-to-end gRPC miss-capture: server has no matching step,
        // client invokes via Grpc.Net.Client, mock persists a step with
        // service/method split out and requestBinary base64-encoded.
        var path = WriteRecording("world", "echo.Echoer", "Echo");
        var capturePath = Path.Combine(_tempDir, "misses.json");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                Watch = false,
                CaptureMissPath = capturePath,
                HostingExtensions = new IBowireMockHostingExtension[] { new GrpcMockHostingExtension() }
            },
            TestContext.Current.CancellationToken);

        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{server.Port}");
        var invoker = channel.CreateCallInvoker();
        var method = CreateMethod("echo.Echoer", "Missing");

        await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await invoker.AsyncUnaryCall(
                method, host: null, new CallOptions(),
                new StringValue { Value = "ping" });
        });

        Assert.True(File.Exists(capturePath), "capture file should exist after a gRPC miss");
        var json = await File.ReadAllTextAsync(capturePath, TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        var step = doc.RootElement.GetProperty("steps")[0];

        Assert.Equal("grpc", step.GetProperty("protocol").GetString());
        Assert.Equal("echo.Echoer", step.GetProperty("service").GetString());
        Assert.Equal("Missing", step.GetProperty("method").GetString());

        // requestBinary contains the protobuf payload (stripped of the
        // 5-byte envelope); decoding it back via StringValue.Parser
        // yields the original "ping".
        var reqB64 = step.GetProperty("requestBinary").GetString();
        Assert.False(string.IsNullOrEmpty(reqB64));
        var parsed = StringValue.Parser.ParseFrom(Convert.FromBase64String(reqB64!));
        Assert.Equal("ping", parsed.Value);
    }

    [Fact]
    public async Task UnaryCall_AgainstUnknownMethod_GetsGrpcUnimplementedStyleFailure()
    {
        var path = WriteRecording("world", "echo.Echoer", "Echo");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                Watch = false,
                HostingExtensions = new IBowireMockHostingExtension[] { new GrpcMockHostingExtension() }
            },
            TestContext.Current.CancellationToken);

        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{server.Port}");
        var invoker = channel.CreateCallInvoker();
        // A method that isn't in the recording — mock's matcher returns no
        // step, the standalone host responds with HTTP 404. Grpc.Net.Client
        // surfaces that as an RpcException.
        var method = CreateMethod("echo.Echoer", "Missing");

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await invoker.AsyncUnaryCall(
                method,
                host: null,
                new CallOptions(),
                new StringValue { Value = "hello" });
        });

        Assert.NotEqual(StatusCode.OK, ex.StatusCode);
    }
}

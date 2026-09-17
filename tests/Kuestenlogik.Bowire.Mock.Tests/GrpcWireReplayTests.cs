// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Kuestenlogik.Bowire.Mock.Matchers;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// A recording from a plugin built on gRPC — TacticalAPI, whose steps say
/// <c>protocol: "tacticalapi"</c> and name their services by simple name —
/// replays over gRPC like a <c>grpc</c> recording does. The host records
/// the wire bytes for every plugin that implements
/// <see cref="IBowireStreamingWithWireBytes"/>; the mock used to read the
/// label instead of the bytes and answered such a recording with 501.
/// </summary>
public sealed class GrpcWireReplayTests : IDisposable
{
    static GrpcWireReplayTests()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    private readonly string _tempDir;

    public GrpcWireReplayTests()
    {
        _tempDir = SafePath.Combine(Path.GetTempPath(), "bowire-mock-wire-" + Guid.NewGuid().ToString("N"));
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

    // ---- the predicate ----

    [Fact]
    public void A_step_with_wire_bytes_is_grpc_whatever_its_label()
    {
        Assert.True(GrpcWire.IsGrpcStep(new BowireRecordingStep
        {
            Protocol = "tacticalapi", Service = "Situation", Method = "GetSituationObjects", MethodType = "Unary", ResponseBinary = "CCo=",
        }));
        Assert.True(GrpcWire.IsGrpcStep(new BowireRecordingStep
        {
            Protocol = "tacticalapi", Service = "Situation", Method = "SubscribeSituationObjectEvents", MethodType = "ServerStreaming",
            ReceivedMessages = [new BowireRecordingFrame { Data = "{}", ResponseBinary = "CCo=" }],
        }));
        // The label alone still counts: a pre-wire-bytes grpc step reaches
        // the replayer and its "re-record" answer.
        Assert.True(GrpcWire.IsGrpcStep(new BowireRecordingStep { Protocol = "grpc", Service = "calc.Calculator", Method = "Add" }));
        // No bytes, no label: not gRPC. Bytes without an address are not
        // either — nothing could match them. A REST step with a body is not.
        Assert.False(GrpcWire.IsGrpcStep(new BowireRecordingStep { Protocol = "tacticalapi", Service = "Situation", Method = "GetSituationObjects" }));
        Assert.False(GrpcWire.IsGrpcStep(new BowireRecordingStep { Protocol = "tacticalapi", ResponseBinary = "CCo=" }));
        Assert.False(GrpcWire.IsGrpcStep(new BowireRecordingStep { Protocol = "rest", Service = "Weather", Method = "getCurrent", HttpPath = "/weather", HttpVerb = "GET", ResponseBinary = "CCo=" }));
        // A streamed step with a frame that has no bytes cannot replay.
        Assert.False(GrpcWire.IsGrpcStep(new BowireRecordingStep
        {
            Protocol = "tacticalapi", Service = "Situation", Method = "Subscribe", MethodType = "ServerStreaming",
            ReceivedMessages = [new BowireRecordingFrame { Data = "{}", ResponseBinary = "CCo=" }, new BowireRecordingFrame { Data = "{}" }],
        }));
    }

    [Fact]
    public void A_simple_service_name_matches_the_wire_paths_last_segment()
    {
        var step = new BowireRecordingStep { Protocol = "tacticalapi", Service = "Situation", Method = "GetSituationObjects", ResponseBinary = "CCo=" };
        Assert.True(GrpcWire.MatchesPath(step, "/rheinmetall.tactical_api.v0.Situation/GetSituationObjects"));
        Assert.True(GrpcWire.MatchesPath(step, "/Situation/GetSituationObjects"));
        Assert.False(GrpcWire.MatchesPath(step, "/rheinmetall.tactical_api.v0.OwnPose/GetSituationObjects"));
        Assert.False(GrpcWire.MatchesPath(step, "/rheinmetall.tactical_api.v0.Situation/DeleteSituationObjects"));
        Assert.False(GrpcWire.MatchesPath(step, "/rheinmetall.tactical_api.v0.NotSituation/GetSituationObjects"));

        // A fully-qualified service matches the whole path only.
        var full = new BowireRecordingStep { Protocol = "grpc", Service = "calc.Calculator", Method = "Add" };
        Assert.True(GrpcWire.MatchesPath(full, "/calc.Calculator/Add"));
        Assert.False(GrpcWire.MatchesPath(full, "/other.Calculator/Add"));
    }

    [Fact]
    public void The_matcher_pairs_a_grpc_request_with_a_tacticalapi_step()
    {
        var matcher = new ExactMatcher();
        var rec = new BowireRecording
        {
            Id = "rec", Name = "tacticalapi", RecordingFormatVersion = 2,
            Steps =
            [
                new BowireRecordingStep { Id = "s1", Protocol = "tacticalapi", Service = "OwnPose", Method = "GetPosition", MethodType = "Unary", Status = "OK", ResponseBinary = "CAE=" },
                new BowireRecordingStep { Id = "s2", Protocol = "tacticalapi", Service = "Situation", Method = "GetSituationObjects", MethodType = "Unary", Status = "OK", ResponseBinary = "CCo=" },
            ],
        };
        var request = new MockRequest { Protocol = "grpc", HttpMethod = "POST", Path = "/rheinmetall.tactical_api.v0.Situation/GetSituationObjects", ContentType = "application/grpc" };
        Assert.True(matcher.TryMatch(request, rec, out var step));
        Assert.Equal("s2", step.Id);
    }

    // ---- over the wire ----

    [Fact]
    public async Task A_tacticalapi_recording_replays_unary_and_streamed_frames_over_grpc()
    {
        var unary = new StringValue { Value = "situation" }.ToByteArray();
        var frames = new[] { new StringValue { Value = "alpha" }.ToByteArray(), new StringValue { Value = "beta" }.ToByteArray() };
        var recording = new
        {
            id = "rec_tacticalapi",
            name = "tacticalapi replay",
            recordingFormatVersion = 2,
            steps = new object[]
            {
                new
                {
                    id = "step_unary",
                    protocol = "tacticalapi",
                    service = "Situation",
                    method = "GetSituationObjects",
                    methodType = "Unary",
                    status = "OK",
                    response = "{\"value\":\"situation\"}",
                    responseBinary = Convert.ToBase64String(unary),
                },
                new
                {
                    id = "step_stream",
                    protocol = "tacticalapi",
                    service = "Situation",
                    method = "SubscribeSituationObjectEvents",
                    methodType = "ServerStreaming",
                    status = "OK",
                    response = "\"beta\"",
                    receivedMessages = new object[]
                    {
                        new { index = 0, timestampMs = 0, data = "\"alpha\"", responseBinary = Convert.ToBase64String(frames[0]) },
                        new { index = 1, timestampMs = 5, data = "\"beta\"", responseBinary = Convert.ToBase64String(frames[1]) },
                    },
                },
            },
        };
        var path = SafePath.Combine(_tempDir, "tacticalapi.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions { RecordingPath = path, Port = 0, Watch = false, ReplaySpeed = 0, HostingExtensions = new IBowireMockHostingExtension[] { new GrpcMockHostingExtension() } },
            TestContext.Current.CancellationToken);

        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{server.Port}");
        var invoker = channel.CreateCallInvoker();

        // The client dials the fully-qualified service the plugin's
        // stubs use; the recording knows it as "Situation".
        var unaryMethod = new Method<StringValue, StringValue>(MethodType.Unary, "rheinmetall.tactical_api.v0.Situation", "GetSituationObjects", StringValueMarshaller, StringValueMarshaller);
        var response = await invoker.AsyncUnaryCall(unaryMethod, host: null, new CallOptions(), new StringValue { Value = "{}" });
        Assert.Equal("situation", response.Value);

        var streamMethod = new Method<StringValue, StringValue>(MethodType.ServerStreaming, "rheinmetall.tactical_api.v0.Situation", "SubscribeSituationObjectEvents", StringValueMarshaller, StringValueMarshaller);
        using var call = invoker.AsyncServerStreamingCall(streamMethod, host: null, new CallOptions(), new StringValue { Value = "{}" });
        var received = new List<string>();
        while (await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken))
            received.Add(call.ResponseStream.Current.Value);
        Assert.Equal(["alpha", "beta"], received);
    }
}

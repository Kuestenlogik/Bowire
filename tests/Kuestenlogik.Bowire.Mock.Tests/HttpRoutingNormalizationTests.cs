// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mock.Loading;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// #511 — load-time HTTP-routing normalisation. Lean recordings (hand-
/// authored, or captured before the recorder stamped httpVerb/httpPath on
/// GraphQL / SSE / WebSocket / SignalR steps) must still reach the matcher:
/// the loader derives the routing pair from the step's serverUrl.
/// </summary>
public sealed class HttpRoutingNormalizationTests
{
    private static string Recording(string stepsJson) => $$"""
    {
      "id": "rec_norm",
      "name": "normalization",
      "recordingFormatVersion": 2,
      "steps": [{{stepsJson}}]
    }
    """;

    [Theory]
    [InlineData("graphql", "http://localhost:5153/graphql", "POST", "/graphql")]
    [InlineData("sse", "http://localhost:5156/arrivals", "GET", "/arrivals")]
    [InlineData("websocket", "ws://localhost:5154/ais", "GET", "/ais")]
    [InlineData("signalr", "http://localhost:5155/ops", "GET", "/ops")]
    public void Derives_Routing_From_ServerUrl(string protocol, string serverUrl, string expectedVerb, string expectedPath)
    {
        var recording = RecordingLoader.Parse(Recording($$"""
        {
          "id": "s1",
          "protocol": "{{protocol}}",
          "service": "svc",
          "method": "m",
          "methodType": "ServerStreaming",
          "serverUrl": "{{serverUrl}}"
        }
        """));

        var step = Assert.Single(recording.Steps);
        Assert.Equal(expectedVerb, step.HttpVerb);
        Assert.Equal(expectedPath, step.HttpPath);
    }

    [Fact]
    public void Existing_Routing_Fields_Are_Not_Clobbered()
    {
        var recording = RecordingLoader.Parse(Recording("""
        {
          "id": "s1",
          "protocol": "graphql",
          "service": "Query",
          "method": "portCall",
          "methodType": "Unary",
          "serverUrl": "http://localhost:5153/graphql",
          "httpVerb": "PUT",
          "httpPath": "/custom"
        }
        """));

        var step = Assert.Single(recording.Steps);
        Assert.Equal("PUT", step.HttpVerb);
        Assert.Equal("/custom", step.HttpPath);
    }

    [Fact]
    public void Fills_Only_The_Missing_Half()
    {
        var recording = RecordingLoader.Parse(Recording("""
        {
          "id": "s1",
          "protocol": "sse",
          "service": "arrivals",
          "method": "arrival",
          "methodType": "ServerStreaming",
          "serverUrl": "http://localhost:5156/arrivals",
          "httpPath": "/already-set"
        }
        """));

        var step = Assert.Single(recording.Steps);
        Assert.Equal("GET", step.HttpVerb);
        Assert.Equal("/already-set", step.HttpPath);
    }

    [Theory]
    [InlineData("grpc")]
    [InlineData("mqtt")]
    [InlineData("socketio")]
    [InlineData("rest")]
    public void Other_Protocols_Stay_Untouched(string protocol)
    {
        var recording = RecordingLoader.Parse(Recording($$"""
        {
          "id": "s1",
          "protocol": "{{protocol}}",
          "service": "svc",
          "method": "m",
          "methodType": "Unary",
          "serverUrl": "http://localhost:5150/some/path"
        }
        """));

        var step = Assert.Single(recording.Steps);
        Assert.True(string.IsNullOrEmpty(step.HttpVerb));
        Assert.True(string.IsNullOrEmpty(step.HttpPath));
    }

    [Fact]
    public void Missing_Or_Malformed_ServerUrl_Is_Skipped()
    {
        var recording = RecordingLoader.Parse(Recording("""
        {
          "id": "s1",
          "protocol": "graphql",
          "service": "Query",
          "method": "portCall",
          "methodType": "Unary",
          "serverUrl": "not a url"
        }
        """));

        var step = Assert.Single(recording.Steps);
        Assert.True(string.IsNullOrEmpty(step.HttpVerb));
        Assert.True(string.IsNullOrEmpty(step.HttpPath));
    }
}

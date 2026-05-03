// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using MQTTnet;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Phase 2f: the mock spins up an embedded MQTT broker and proactively
/// injects every recorded MQTT publish on schedule, without waiting for an
/// inbound HTTP request. Verify by connecting an MQTTnet client as a
/// subscriber and collecting the emitted messages.
/// </summary>
public sealed class MqttEmissionTests : IDisposable
{
    private readonly string _tempDir;

    public MqttEmissionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-mock-mqtt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RecordedMqttPublishes_AreInjectedOnConnectedSubscriber()
    {
        var recording = new
        {
            id = "rec_mqtt",
            name = "mqtt proactive",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_a",
                    capturedAt = 1_000L,
                    protocol = "mqtt",
                    service = "sensors",
                    method = "sensors/temperature",
                    methodType = "Unary",
                    body = "21.5",
                    messages = new[] { "21.5" },
                    metadata = new Dictionary<string, string> { ["qos"] = "1", ["retain"] = "false" },
                    status = "OK",
                    durationMs = 4L,
                    response = (string?)null
                },
                new
                {
                    id = "step_b",
                    capturedAt = 1_050L,
                    protocol = "mqtt",
                    service = "sensors",
                    method = "sensors/humidity",
                    methodType = "Unary",
                    body = "72",
                    messages = new[] { "72" },
                    metadata = new Dictionary<string, string> { ["qos"] = "1" },
                    status = "OK",
                    durationMs = 3L,
                    response = (string?)null
                }
            }
        };

        var path = Path.Combine(_tempDir, "mqtt.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                TransportPorts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase) { ["mqtt"] = 0 },
                TransportHosts = new IBowireMockTransportHost[] { new MqttMockTransportHost() },
                Watch = false,
                ReplaySpeed = 0 // fire publishes instantly
            },
            TestContext.Current.CancellationToken);

        Assert.True(server.TransportPorts["mqtt"] > 0, "Expected an OS-assigned MQTT port.");

        // Connect an MQTTnet subscriber to the embedded broker and collect
        // messages with a small timeout; both publishes should arrive.
        var factory = new MqttClientFactory();
        using var subscriber = factory.CreateMqttClient();

        var received = new List<(string Topic, string Payload)>();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        subscriber.ApplicationMessageReceivedAsync += args =>
        {
            var topic = args.ApplicationMessage.Topic;
            var payloadBytes = args.ApplicationMessage.Payload;
            var payload = Encoding.UTF8.GetString(payloadBytes);
            lock (received)
            {
                received.Add((topic, payload));
                if (received.Count >= 2) done.TrySetResult(true);
            }
            return Task.CompletedTask;
        };

        var clientOpts = factory.CreateClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", server.TransportPorts["mqtt"])
            .WithClientId("bowire-mock-test-sub")
            .Build();

        await subscriber.ConnectAsync(clientOpts, TestContext.Current.CancellationToken);
        var subOpts = factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter("sensors/#")
            .Build();
        await subscriber.SubscribeAsync(subOpts, TestContext.Current.CancellationToken);

        // The emitter fires on its own timer; give it enough slack for slow
        // test hosts. ReplaySpeed = 0 skips the scheduled delay so this
        // lands within a handful of ms.
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, received.Count);
        var topics = received.Select(r => r.Topic).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(2, topics.Count);
        Assert.Equal("sensors/humidity", topics[0]);
        Assert.Equal("sensors/temperature", topics[1]);
        Assert.Contains(received, r => r.Topic == "sensors/temperature" && r.Payload == "21.5");
        Assert.Contains(received, r => r.Topic == "sensors/humidity" && r.Payload == "72");

        await subscriber.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Loop_True_RepeatsRecordingWhileMockRuns()
    {
        // Two-step recording. In loop mode the emitter fires both
        // publishes, then wraps back to step 0 and fires them again.
        // We collect 5 messages which can only arrive if the emitter
        // looped at least once.
        var recording = new
        {
            id = "rec_mqtt_loop",
            name = "mqtt loop",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_a", capturedAt = 1_000L,
                    protocol = "mqtt", service = "sensors", method = "loop/a",
                    methodType = "Unary", body = "A",
                    messages = new[] { "A" },
                    metadata = (Dictionary<string, string>?)null,
                    status = "OK", durationMs = 0L, response = (string?)null
                },
                new
                {
                    id = "step_b", capturedAt = 1_001L,
                    protocol = "mqtt", service = "sensors", method = "loop/b",
                    methodType = "Unary", body = "B",
                    messages = new[] { "B" },
                    metadata = (Dictionary<string, string>?)null,
                    status = "OK", durationMs = 0L, response = (string?)null
                }
            }
        };

        var path = Path.Combine(_tempDir, "mqtt-loop.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                TransportPorts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase) { ["mqtt"] = 0 },
                TransportHosts = new IBowireMockTransportHost[] { new MqttMockTransportHost() },
                Watch = false,
                ReplaySpeed = 0, // fire instantly, no per-step pacing
                Loop = true
            },
            TestContext.Current.CancellationToken);

        var factory = new MqttClientFactory();
        using var subscriber = factory.CreateMqttClient();
        var received = new List<string>();
        var enough = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.ApplicationMessageReceivedAsync += args =>
        {
            lock (received)
            {
                received.Add(args.ApplicationMessage.Topic);
                if (received.Count >= 5) enough.TrySetResult(true);
            }
            return Task.CompletedTask;
        };

        await subscriber.ConnectAsync(
            factory.CreateClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", server.TransportPorts["mqtt"])
                .WithClientId("bowire-mqtt-loop")
                .Build(),
            TestContext.Current.CancellationToken);
        await subscriber.SubscribeAsync(
            factory.CreateSubscribeOptionsBuilder().WithTopicFilter("loop/#").Build(),
            TestContext.Current.CancellationToken);

        await enough.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Snapshot under lock — the subscriber callback keeps pushing
        // into 'received' on a background thread while we assert.
        List<string> snapshot;
        lock (received)
        {
            snapshot = [.. received];
        }

        // 5+ messages received means the emitter looped past the initial
        // 2-step recording at least twice.
        Assert.True(snapshot.Count >= 5,
            $"Expected the looped emitter to publish at least 5 times; got {snapshot.Count}.");

        // Every received topic must be one of the two we recorded — no
        // garbage, no extra synthesized topics.
        Assert.All(snapshot, t => Assert.True(t == "loop/a" || t == "loop/b",
            $"Unexpected topic '{t}'; the recording only declares 'loop/a' and 'loop/b'."));

        // Loop semantics: after the first 'loop/b' the emitter must
        // wrap back to 'loop/a' — otherwise it ran the recording once
        // and stopped. We assert against this wrap rather than against
        // the absolute order [a, b, a, b, ...] because, on fast Linux
        // loopback, the subscriber's SUBSCRIBE handshake can complete
        // after the very first frame, so the early sequence may start
        // at 'loop/b' instead of 'loop/a'. The Linux-vs-Windows split
        // is an artefact of the broker's grace period; the property
        // we actually want to verify is "the emitter wraps", not
        // "the subscriber catches the first frame".
        var firstB = snapshot.IndexOf("loop/b");
        Assert.True(firstB >= 0,
            $"Subscriber never received 'loop/b'. Received: [{string.Join(", ", snapshot)}]");
        var aAfterB = snapshot.IndexOf("loop/a", firstB + 1);
        Assert.True(aAfterB >= 0,
            $"Loop didn't wrap: after 'loop/b' at index {firstB}, no 'loop/a' follows. " +
            $"Received: [{string.Join(", ", snapshot)}]");

        await subscriber.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TopicTemplate_DynamicTokenInTopic_SubstitutedBeforePublish()
    {
        // Recorded topic carries a ${uuid} token; the emitter should
        // substitute it into a concrete UUID string before publishing
        // so clients subscribed on sensors/# can still pick it up.
        var recording = new
        {
            id = "rec_mqtt_tpl",
            name = "mqtt topic template",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_tpl",
                    capturedAt = 1_000L,
                    protocol = "mqtt",
                    service = "sensors",
                    method = "sensors/${uuid}/temp",
                    methodType = "Unary",
                    body = "21.5",
                    messages = new[] { "21.5" },
                    metadata = new Dictionary<string, string> { ["qos"] = "1" },
                    status = "OK",
                    durationMs = 0L,
                    response = (string?)null
                }
            }
        };

        var path = Path.Combine(_tempDir, "mqtt-tpl.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                TransportPorts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase) { ["mqtt"] = 0 },
                TransportHosts = new IBowireMockTransportHost[] { new MqttMockTransportHost() },
                Watch = false,
                ReplaySpeed = 0
            },
            TestContext.Current.CancellationToken);

        var factory = new MqttClientFactory();
        using var subscriber = factory.CreateMqttClient();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.ApplicationMessageReceivedAsync += args =>
        {
            tcs.TrySetResult(args.ApplicationMessage.Topic);
            return Task.CompletedTask;
        };

        await subscriber.ConnectAsync(
            factory.CreateClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", server.TransportPorts["mqtt"])
                .WithClientId("bowire-mqtt-topic-tpl")
                .Build(),
            TestContext.Current.CancellationToken);
        await subscriber.SubscribeAsync(
            factory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter("sensors/#")
                .Build(),
            TestContext.Current.CancellationToken);

        var topic = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Token was substituted: topic now starts with sensors/ and
        // ends with /temp, with a non-literal uuid in the middle.
        Assert.StartsWith("sensors/", topic, StringComparison.Ordinal);
        Assert.EndsWith("/temp", topic, StringComparison.Ordinal);
        Assert.DoesNotContain("${uuid}", topic, StringComparison.Ordinal);
        // The middle segment looks like a real UUID (8-4-4-4-12 hex
        // groups separated by hyphens).
        var segments = topic.Split('/');
        Assert.Equal(3, segments.Length);
        Assert.Matches(
            @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            segments[1]);

        await subscriber.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
    }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace Kuestenlogik.Bowire.Protocol.Mqtt.Mock;

/// <summary>
/// Proactive emitter for Phase 2f: replays the MQTT publishes captured in a
/// <see cref="BowireRecording"/> onto an embedded MQTTnet broker on a
/// schedule. No HTTP trigger needed — the scheduler fires as soon as a
/// subscriber is attached (or after the startup-grace timeout, whichever
/// comes first). Subscribers that connect late miss already-fired
/// publishes (retained messages stick around per MQTT semantics).
/// </summary>
public sealed class MqttProactiveEmitter : IAsyncDisposable
{
    private readonly MqttServer _broker;
    private readonly BowireRecording _recording;
    private readonly double _speed;
    private readonly bool _loop;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _firstSubscribeSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _schedulerTask;

    public MqttProactiveEmitter(
        MqttServer broker,
        BowireRecording recording,
        double speed,
        ILogger logger,
        bool loop = false)
    {
        _broker = broker;
        _recording = recording;
        _speed = speed;
        _loop = loop;
        _logger = logger;
    }

    /// <summary>Kick off the schedule on a background task.</summary>
    public void Start()
    {
        // Hook the post-subscribe event so we know when a client's
        // subscription has actually been registered in the broker's
        // routing table. `InterceptingSubscriptionAsync` fires too
        // early — before the subscription lands — so an emit that
        // wins the race against the broker's own bookkeeping produces
        // an MQTT message with no matching routes and the subscriber
        // sees nothing. `ClientSubscribedTopicAsync` fires after the
        // subscription is live, eliminating that window.
        _broker.ClientSubscribedTopicAsync += OnClientSubscribed;
        _schedulerTask = Task.Run(() => RunAsync(_cts.Token));
    }

    private Task OnClientSubscribed(ClientSubscribedTopicEventArgs args)
    {
        _firstSubscribeSignal.TrySetResult();
        return Task.CompletedTask;
    }

    // Maximum startup grace before the scheduler fires without a
    // subscriber. Serves as a backstop when the recording is replayed
    // into a detached broker (nobody's listening, nobody will) so the
    // emitter doesn't hang forever. 2s is generous enough to cover CI
    // load + slow subscriber connects while still feeling instant in
    // interactive use.
    private static readonly TimeSpan s_startupGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// One scheduled broker injection. Publish steps yield one emission
    /// at their capture offset; subscription steps (ServerStreaming with
    /// <c>receivedMessages</c>) yield one emission per captured frame —
    /// the frames ARE the publishes the original broker delivered, so
    /// replaying them is what makes a mock subscriber see the recorded
    /// stream (#511).
    /// </summary>
    private sealed record Emission(
        long OffsetMs,
        string Topic,
        string Payload,
        IDictionary<string, string>? Metadata,
        string StepId);

    private List<Emission> BuildSchedule()
    {
        var mqttSteps = _recording.Steps
            .Where(s => string.Equals(s.Protocol, "mqtt", StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.CapturedAt)
            .ToList();
        if (mqttSteps.Count == 0) return [];

        var baseCapturedAt = mqttSteps[0].CapturedAt;
        var emissions = new List<Emission>();
        foreach (var step in mqttSteps)
        {
            var stepOffset = step.CapturedAt - baseCapturedAt;
            if (IsMqttPublish(step))
            {
                // Publish steps: step.method is the topic path.
                if (string.IsNullOrEmpty(step.Method))
                {
                    _logger.LogWarning("Skipping MQTT step '{StepId}' — no topic on the 'method' field.", step.Id);
                    continue;
                }
                var payload = step.Body ?? step.Messages.FirstOrDefault() ?? "{}";
                emissions.Add(new Emission(stepOffset, step.Method, payload, step.Metadata, step.Id));
            }
            else if (string.Equals(step.MethodType, "ServerStreaming", StringComparison.OrdinalIgnoreCase)
                && step.ReceivedMessages is { Count: > 0 } frames)
            {
                // Subscription steps: step.service carries the topic the
                // client subscribed to (method is the synthetic
                // "receive"/"subscribe" label).
                var topic = !string.IsNullOrEmpty(step.Service) ? step.Service : step.Method;
                if (string.IsNullOrEmpty(topic))
                {
                    _logger.LogWarning("Skipping MQTT step '{StepId}' — no topic on 'service' or 'method'.", step.Id);
                    continue;
                }
                foreach (var frame in frames)
                {
                    var payload = FramePayload(frame);
                    if (payload is null) continue;
                    emissions.Add(new Emission(
                        stepOffset + (frame.TimestampMs ?? 0), topic, payload, step.Metadata, step.Id));
                }
            }
        }
        return emissions.OrderBy(e => e.OffsetMs).ToList();
    }

    private static string? FramePayload(BowireRecordingFrame frame) => frame.Data switch
    {
        null => frame.Body,
        string s => s,
        System.Text.Json.JsonElement el => el.GetRawText(),
        _ => System.Text.Json.JsonSerializer.Serialize(frame.Data),
    };

    private async Task RunAsync(CancellationToken ct)
    {
        var emissions = BuildSchedule();
        if (emissions.Count == 0) return;

        // Wait for the first subscriber OR the backstop timeout. Either
        // way we proceed to emit — but the subscribe-triggered path
        // fires as soon as the subscriber is ready, which means tests
        // (and real clients) don't lose the opening burst on slow hosts.
        try
        {
            await _firstSubscribeSignal.Task.WaitAsync(s_startupGrace, ct);
        }
        catch (TimeoutException) { /* nobody subscribed — fire anyway */ }
        catch (OperationCanceledException) { return; }

        do
        {
            // Reset the wall-clock origin at the start of every loop
            // iteration so the second playthrough paces from its own
            // zero, not from way-after-the-first-run's offsets.
            var scheduleStartTicks = Environment.TickCount64;

            foreach (var emission in emissions)
            {
                ct.ThrowIfCancellationRequested();

                if (_speed > 0)
                {
                    var targetOffsetMs = (long)(emission.OffsetMs / _speed);
                    var elapsed = Environment.TickCount64 - scheduleStartTicks;
                    var waitMs = targetOffsetMs - elapsed;
                    if (waitMs > 0)
                    {
                        try { await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct); }
                        catch (OperationCanceledException) { return; }
                    }
                }

                await EmitAsync(emission, ct);
            }
        }
        while (_loop && !ct.IsCancellationRequested);
    }

    private async Task EmitAsync(Emission emission, CancellationToken ct)
    {
        try
        {
            var topic = emission.Topic;

            // Apply the same dynamic-value substitution to the topic
            // that the payload already gets. Enables recorded topics
            // like "sensors/${uuid}/temp" or "cmd/${now}/ack" without
            // pre-rendering them at capture time. Clients subscribing
            // with MQTT wildcards (+/#) pick the concrete topic up via
            // the broker's native routing; no mock-side match needed.
            topic = Kuestenlogik.Bowire.Mock.Replay.ResponseBodySubstitutor.Substitute(topic);

            var payloadBytes = Encoding.UTF8.GetBytes(
                Kuestenlogik.Bowire.Mock.Replay.ResponseBodySubstitutor.Substitute(emission.Payload));

            var qos = MqttQualityOfServiceLevel.AtLeastOnce;
            var retain = false;
            if (emission.Metadata is not null)
            {
                if (emission.Metadata.TryGetValue("qos", out var qosStr) &&
                    Enum.TryParse<MqttQualityOfServiceLevel>(qosStr, ignoreCase: true, out var q))
                {
                    qos = q;
                }
                else if (emission.Metadata.TryGetValue("qos", out qosStr) &&
                    int.TryParse(qosStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qi) &&
                    qi is >= 0 and <= 2)
                {
                    qos = (MqttQualityOfServiceLevel)qi;
                }

                if (emission.Metadata.TryGetValue("retain", out var retainStr))
                    retain = string.Equals(retainStr, "true", StringComparison.OrdinalIgnoreCase);
            }

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payloadBytes)
                .WithQualityOfServiceLevel(qos)
                .WithRetainFlag(retain)
                .Build();

            var injected = new InjectedMqttApplicationMessage(message);
            await _broker.InjectApplicationMessage(injected, ct);

            _logger.LogInformation(
                "mqtt-emit(step={StepId}, topic={Topic}, qos={Qos}, retain={Retain}, bytes={Bytes})",
                LogSanitizer.Strip(emission.StepId), LogSanitizer.Strip(topic), (int)qos, retain, payloadBytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inject MQTT message for step '{StepId}'; scheduler continues.", LogSanitizer.Strip(emission.StepId));
        }
    }

    private static bool IsMqttPublish(BowireRecordingStep step) =>
        string.Equals(step.Protocol, "mqtt", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(step.MethodType, "Unary", StringComparison.OrdinalIgnoreCase);

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Detach the broker-event handler before tearing down so a
        // subscription happening during shutdown doesn't poke a
        // cancelled TCS.
        _broker.ClientSubscribedTopicAsync -= OnClientSubscribed;
        _firstSubscribeSignal.TrySetCanceled();

        await _cts.CancelAsync();
        if (_schedulerTask is not null)
        {
            try { await _schedulerTask; }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { _logger.LogWarning(ex, "MQTT scheduler exited with an error."); }
        }
        _cts.Dispose();
    }
}

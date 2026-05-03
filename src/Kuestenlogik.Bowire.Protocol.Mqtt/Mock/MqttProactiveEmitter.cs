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

    private async Task RunAsync(CancellationToken ct)
    {
        var steps = _recording.Steps
            .Where(IsMqttPublish)
            .OrderBy(s => s.CapturedAt)
            .ToList();

        if (steps.Count == 0) return;

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

        var baseCapturedAt = steps[0].CapturedAt;

        do
        {
            // Reset the wall-clock origin at the start of every loop
            // iteration so the second playthrough paces from its own
            // zero, not from way-after-the-first-run's offsets.
            var scheduleStartTicks = Environment.TickCount64;

            foreach (var step in steps)
            {
                ct.ThrowIfCancellationRequested();

                if (_speed > 0)
                {
                    var targetOffsetMs = (long)((step.CapturedAt - baseCapturedAt) / _speed);
                    var elapsed = Environment.TickCount64 - scheduleStartTicks;
                    var waitMs = targetOffsetMs - elapsed;
                    if (waitMs > 0)
                    {
                        try { await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct); }
                        catch (OperationCanceledException) { return; }
                    }
                }

                await EmitAsync(step, ct);
            }
        }
        while (_loop && !ct.IsCancellationRequested);
    }

    private async Task EmitAsync(BowireRecordingStep step, CancellationToken ct)
    {
        try
        {
            var topic = step.Method; // MQTT plugin uses step.method as the topic path
            if (string.IsNullOrEmpty(topic))
            {
                _logger.LogWarning("Skipping MQTT step '{StepId}' — no topic on the 'method' field.", step.Id);
                return;
            }

            // Apply the same dynamic-value substitution to the topic
            // that the payload already gets. Enables recorded topics
            // like "sensors/${uuid}/temp" or "cmd/${now}/ack" without
            // pre-rendering them at capture time. Clients subscribing
            // with MQTT wildcards (+/#) pick the concrete topic up via
            // the broker's native routing; no mock-side match needed.
            topic = Kuestenlogik.Bowire.Mock.Replay.ResponseBodySubstitutor.Substitute(topic);

            var payload = step.Body ?? step.Messages.FirstOrDefault() ?? "{}";
            var payloadBytes = Encoding.UTF8.GetBytes(
                Kuestenlogik.Bowire.Mock.Replay.ResponseBodySubstitutor.Substitute(payload));

            var qos = MqttQualityOfServiceLevel.AtLeastOnce;
            var retain = false;
            if (step.Metadata is not null)
            {
                if (step.Metadata.TryGetValue("qos", out var qosStr) &&
                    Enum.TryParse<MqttQualityOfServiceLevel>(qosStr, ignoreCase: true, out var q))
                {
                    qos = q;
                }
                else if (step.Metadata.TryGetValue("qos", out qosStr) &&
                    int.TryParse(qosStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var qi) &&
                    qi is >= 0 and <= 2)
                {
                    qos = (MqttQualityOfServiceLevel)qi;
                }

                if (step.Metadata.TryGetValue("retain", out var retainStr))
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
                step.Id, topic, (int)qos, retain, payloadBytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inject MQTT message for step '{StepId}'; scheduler continues.", step.Id);
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

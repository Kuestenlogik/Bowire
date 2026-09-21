// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

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

    // #708 — the shortest a loop cycle may take. Pacing inside a run and
    // pacing between runs were two different things, and only the first
    // one existed: `--replay-speed 0 --loop` published as fast as the CPU
    // allowed, for ever. Both switches are documented on their own and
    // both are sensible; their product was described nowhere.
    //
    // A cycle now lasts as long as the recording it replays, so "play the
    // frames as fast as you like" still leaves the recording worth N
    // seconds of traffic. An unbounded rate is something to ask for, not
    // something to fall into by combining two other switches.
    //
    // The floor is what makes that true for short recordings. A two-step
    // capture spanning 1 ms would otherwise loop a thousand times a second
    // — at any speed, including the default 1.0, where the same flood has
    // always been possible and simply went unnoticed. One second is the
    // smallest unit in which "N seconds of traffic" means anything, and a
    // looped sub-second recording is a heartbeat, for which one per second
    // is the ordinary rate.
    internal static readonly TimeSpan MinimumCycle = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long one loop cycle should take, in milliseconds: the recording's
    /// own span under the current pacing, never below
    /// <see cref="MinimumCycle"/>.
    /// </summary>
    /// <param name="spanMs">
    /// Offset of the last emission, i.e. the recording's duration.
    /// </param>
    /// <param name="speed">
    /// <c>MockEmitterOptions.ReplaySpeed</c>. At <c>0</c> the frames inside
    /// the run are not paced at all; the cycle still stands for the span the
    /// recording covers.
    /// </param>
    internal static long CycleLengthMs(long spanMs, double speed)
    {
        if (spanMs < 0) spanMs = 0;
        // Above 1.0 the run is compressed and the cycle compresses with it,
        // so a faster replay stays faster end to end. At or below 0 there is
        // no factor to divide by and the span stands as captured.
        var paced = speed > 0
            ? (long)Math.Round(spanMs / speed, MidpointRounding.AwayFromZero)
            : spanMs;
        return Math.Max(paced, (long)MinimumCycle.TotalMilliseconds);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var emissions = BuildSchedule();
        if (emissions.Count == 0) return;

        // The recording's own duration: offsets are relative to the first
        // step, so the last one is the span. Read once — the schedule does
        // not change between cycles.
        var cycleLengthMs = CycleLengthMs(emissions[^1].OffsetMs, _speed);

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

            if (!_loop) return;

            // Hold the cycle open for what the recording is worth. With the
            // frames paced (speed > 0) the run has usually taken that long
            // already and this waits for nothing; with speed 0 it is the
            // only thing standing between `--loop` and an unbounded rate.
            var remaining = cycleLengthMs - (Environment.TickCount64 - scheduleStartTicks);
            if (remaining > 0)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(remaining), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
        while (!ct.IsCancellationRequested);
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
                // Enum.TryParse also accepts the numeric forms "0"/"1"/"2" (the
                // underlying value), so a single parse covers both the named and
                // integer QoS spellings; IsDefined rejects out-of-range numbers.
                if (emission.Metadata.TryGetValue("qos", out var qosStr)
                    && Enum.TryParse<MqttQualityOfServiceLevel>(qosStr, ignoreCase: true, out var q)
                    && Enum.IsDefined(q))
                {
                    qos = q;
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

    private int _disposeSignaled;

    public async ValueTask DisposeAsync()
    {
        // Atomic guard: concurrent DisposeAsync calls must not both pass and
        // double-cancel / double-dispose _cts.
        if (Interlocked.Exchange(ref _disposeSignaled, 1) == 1) return;

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

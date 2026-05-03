// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
// (MqttTopicMatcher now in same namespace)
using Kuestenlogik.Bowire.Mocking;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace Kuestenlogik.Bowire.Protocol.Mqtt.Mock;

/// <summary>
/// MQTT reactive matcher / responder. Watches incoming client
/// publishes via the broker's <c>InterceptingPublishAsync</c> event
/// and emits paired responses for every recorded step whose topic
/// pattern matches.
/// </summary>
/// <remarks>
/// <para>
/// Recording shape for a reactive MQTT step:
/// </para>
/// <code>
/// {
///   "protocol": "mqtt",
///   "methodType": "Duplex",
///   "method": "cmd/+/reboot",              // topic pattern with wildcards
///   "metadata": {
///       "responseTopic": "cmd/${topic.0}/ack",  // optional; MQTT v5 ResponseTopic on the incoming publish wins when present
///       "qos": "1",
///       "retain": "false"
///   },
///   "body": "{\"ack\":true,\"device\":\"${topic.0}\"}"
/// }
/// </code>
/// <para>
/// On match:
/// </para>
/// <list type="number">
///   <item>Extract wildcard bindings via <see cref="MqttTopicMatcher"/>
///   (<c>+</c> captures by position, trailing <c>#</c> as
///   <c>${topic.rest}</c>).</item>
///   <item>Resolve the response topic — MQTT v5 <c>ResponseTopic</c>
///   on the incoming publish wins, else <c>metadata.responseTopic</c>
///   from the step, else no response (fire-and-forget).</item>
///   <item>Run <see cref="Kuestenlogik.Bowire.Mock.Replay.ResponseBodySubstitutor.Substitute(string, IReadOnlyDictionary{string, string}?)"/>
///   on both the response topic and the payload with the bindings as
///   extras so <c>${topic.N}</c> / <c>${topic.rest}</c> get concrete
///   values.</item>
///   <item>Inject the response via
///   <see cref="MqttServer.InjectApplicationMessage"/>, copying
///   <c>CorrelationData</c> from the request (MQTT v5) so clients can
///   pair request/response.</item>
/// </list>
/// </remarks>
public sealed class MqttReactiveResponder : IDisposable
{
    private readonly MqttServer _broker;
    private readonly List<ReactiveStep> _steps;
    private readonly ILogger _logger;
    private bool _disposed;

    public MqttReactiveResponder(MqttServer broker, BowireRecording recording, ILogger logger)
    {
        _broker = broker;
        _logger = logger;
        _steps = ExtractReactiveSteps(recording);
    }

    /// <summary>Hook the broker-intercept event. Call once per emitter lifetime.</summary>
    public void Start()
    {
        if (_steps.Count == 0) return;
        _broker.InterceptingPublishAsync += OnInterceptingPublishAsync;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _broker.InterceptingPublishAsync -= OnInterceptingPublishAsync;
    }

    private async Task OnInterceptingPublishAsync(InterceptingPublishEventArgs args)
    {
        // Don't interfere with publishes originating from the broker
        // itself (that's our proactive emitter injecting). Client-side
        // IDs on injected messages are empty, so filter on that.
        if (string.IsNullOrEmpty(args.ClientId)) return;

        var incoming = args.ApplicationMessage;
        var topic = incoming.Topic ?? string.Empty;

        foreach (var step in _steps)
        {
            if (!MqttTopicMatcher.TryMatch(step.Pattern, topic, out var bindings)) continue;

            // Build extraBindings as topic.0, topic.1, topic.rest so
            // ${topic.0} etc. resolve in the response body/topic.
            var extras = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in bindings) extras["topic." + k] = v;

            var responseTopic = ResolveResponseTopic(incoming, step, extras);
            if (string.IsNullOrEmpty(responseTopic))
            {
                _logger.LogDebug(
                    "mqtt-react(step={StepId}, topic={Topic}) — no response topic; fire-and-forget",
                    step.Id, topic);
                continue;
            }

            var payload = Kuestenlogik.Bowire.Mock.Replay.ResponseBodySubstitutor.Substitute(step.Payload, extras);
            var payloadBytes = Encoding.UTF8.GetBytes(payload);

            var responseBuilder = new MqttApplicationMessageBuilder()
                .WithTopic(responseTopic)
                .WithPayload(payloadBytes)
                .WithQualityOfServiceLevel(step.Qos)
                .WithRetainFlag(step.Retain);

            // MQTT v5 CorrelationData passthrough — lets clients pair
            // their pending request with the arriving response.
            if (incoming.CorrelationData is { Length: > 0 } corrData)
                responseBuilder.WithCorrelationData(corrData);

            var response = responseBuilder.Build();
            try
            {
                await _broker.InjectApplicationMessage(new InjectedMqttApplicationMessage(response));
                _logger.LogInformation(
                    "mqtt-react(step={StepId}, request={Topic}) -> {ResponseTopic} ({Bytes} bytes)",
                    step.Id, topic, responseTopic, payloadBytes.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "mqtt-react: failed to emit response for step '{StepId}'", step.Id);
            }
        }
    }

    private static string? ResolveResponseTopic(
        MqttApplicationMessage incoming,
        ReactiveStep step,
        IReadOnlyDictionary<string, string> extras)
    {
        // MQTT v5 ResponseTopic on the incoming publish has the
        // highest priority — it's the client telling us exactly where
        // the reply goes. Falls back to the step's metadata for
        // v3.1.1 recordings or when the client doesn't set it.
        if (!string.IsNullOrEmpty(incoming.ResponseTopic)) return incoming.ResponseTopic;
        if (string.IsNullOrEmpty(step.ResponseTopicTemplate)) return null;
        return Kuestenlogik.Bowire.Mock.Replay.ResponseBodySubstitutor.Substitute(step.ResponseTopicTemplate, extras);
    }

    private static List<ReactiveStep> ExtractReactiveSteps(BowireRecording recording)
    {
        var result = new List<ReactiveStep>();
        foreach (var step in recording.Steps)
        {
            if (!string.Equals(step.Protocol, "mqtt", StringComparison.OrdinalIgnoreCase)) continue;
            // Reactive steps use Duplex or ClientStreaming; Unary
            // steps are the proactive emitter's domain.
            if (!string.Equals(step.MethodType, "Duplex", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(step.MethodType, "ClientStreaming", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrEmpty(step.Method)) continue;

            var qos = MqttQualityOfServiceLevel.AtLeastOnce;
            var retain = false;
            string? responseTopicTemplate = null;
            if (step.Metadata is not null)
            {
                if (step.Metadata.TryGetValue("qos", out var qosStr) &&
                    int.TryParse(qosStr, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var qi) &&
                    qi is >= 0 and <= 2)
                {
                    qos = (MqttQualityOfServiceLevel)qi;
                }
                if (step.Metadata.TryGetValue("retain", out var retainStr))
                    retain = string.Equals(retainStr, "true", StringComparison.OrdinalIgnoreCase);
                if (step.Metadata.TryGetValue("responseTopic", out var rt))
                    responseTopicTemplate = rt;
            }

            result.Add(new ReactiveStep(
                Id: step.Id,
                Pattern: step.Method!,
                ResponseTopicTemplate: responseTopicTemplate,
                Payload: step.Body ?? step.Messages.FirstOrDefault() ?? "{}",
                Qos: qos,
                Retain: retain));
        }
        return result;
    }

    private sealed record ReactiveStep(
        string Id,
        string Pattern,
        string? ResponseTopicTemplate,
        string Payload,
        MqttQualityOfServiceLevel Qos,
        bool Retain);
}

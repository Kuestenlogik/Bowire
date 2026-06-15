// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Kuestenlogik.Bowire.AsyncApi;

/// <summary>
/// Inverse of <see cref="AsyncApiDocumentLoader"/>: emits an AsyncAPI
/// 3.0 document (YAML by default, JSON optional) from a Bowire
/// discovery result. Pair this with a workbench-side "Export AsyncAPI"
/// action or the <c>bowire export asyncapi</c> CLI to capture the
/// topology of a running MQTT / NATS / Kafka / WebSocket / AMQP
/// target as a schema artifact teams can check into their docs repo
/// and feed back into Bowire as a discovery source.
/// </summary>
/// <remarks>
/// <para>
/// Pure transformation — no IO, no wire-plugin lookup. Takes a server
/// URL plus a list of <see cref="BowireServiceInfo"/> records the
/// caller already discovered through whichever plugin owns that URL.
/// The exporter then groups, maps, and serialises:
/// </para>
/// <list type="bullet">
///   <item><c>servers[default]</c> — host + protocol parsed from the
///     URL (mqtt / nats / kafka / ws / wss / amqp / amqp1 / http /
///     https).</item>
///   <item><c>channels[&lt;safeId&gt;]</c> — one per Bowire method
///     deduplicated by address; address = the method name (which is
///     the topic / subject / endpoint path on every messaging
///     plugin).</item>
///   <item><c>operations[&lt;safeId&gt;]</c> — one per method:
///     <c>publish</c> / <c>produce</c>-style → <c>send</c>;
///     <c>subscribe</c> / <c>consume</c>-style → <c>receive</c>.
///     Method-shape <c>ServerStreaming</c> is the secondary tie-
///     breaker for protocols whose verb doesn't show up in the
///     method's <c>FullName</c>.</item>
///   <item><c>components.messages[&lt;safeId&gt;]</c> — one per
///     unique <see cref="BowireMessageInfo"/> referenced by any
///     method's input or output; <c>payload</c> is a synthesised
///     JSON Schema built from the message's
///     <see cref="BowireFieldInfo"/> list.</item>
/// </list>
/// <para>
/// The bindings emitted on a channel match the binding-resolver keys
/// Phase A/B/C ships (<c>mqtt</c> / <c>mqtt5</c> / <c>kafka</c> /
/// <c>ws</c> / <c>amqp</c> / <c>amqp1</c> / <c>nats</c> / <c>http</c>)
/// so a round-trip — export → re-load → discover — lands the same
/// channels back, just with the bindings as data on the document
/// instead of inferred from the plugin's URL scheme.
/// </para>
/// </remarks>
public static class AsyncApiDocumentBuilder
{
    /// <summary>
    /// Build an AsyncAPI 3.0 document from a discovery result.
    /// </summary>
    /// <param name="serverUrl">The URL the discovery was performed against.</param>
    /// <param name="services">Services returned by the wire plugin's <c>DiscoverAsync</c>.</param>
    /// <param name="recording">
    /// Optional recording — when supplied, every operation gets an
    /// <c>x-bowire-coverage</c> extension reporting how many steps
    /// the recording carries for that channel address. Sibling of the
    /// REST exporter's coverage path (matched there by
    /// <c>(HttpVerb, HttpPath)</c>; here by channel address because
    /// messaging steps don't carry HTTP identifiers).
    /// </param>
    /// <param name="options">
    /// Optional output knobs (format, info-title, info-version). When
    /// <c>null</c>, defaults to YAML output, title = host name, version
    /// = "1.0.0".
    /// </param>
    public static string Build(
        string serverUrl,
        IReadOnlyList<BowireServiceInfo> services,
        BowireRecording? recording = null,
        AsyncApiExportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        ArgumentNullException.ThrowIfNull(services);
        options ??= new AsyncApiExportOptions();

        var coverage = BuildCoverageIndex(recording);
        var doc = BuildDocumentModel(serverUrl, services, options, coverage);
        return options.Format == AsyncApiExportFormat.Json
            ? SerializeJson(doc)
            : SerializeYaml(doc);
    }

    /// <summary>
    /// Aggregate recording steps into a <c>channelAddress → stepCount</c>
    /// map. Messaging recordings don't carry an HTTP verb / path pair,
    /// so the only stable address-side identifier is <c>step.Method</c>
    /// — which is the topic / subject / endpoint for every messaging
    /// plugin (MQTT/NATS/Kafka/AMQP/WebSocket all set it to the
    /// channel address). Steps whose method is empty or that came from
    /// a non-messaging recording are skipped silently.
    /// </summary>
    internal static Dictionary<string, int> BuildCoverageIndex(BowireRecording? recording)
    {
        var dict = new Dictionary<string, int>(StringComparer.Ordinal);
        if (recording?.Steps is null) return dict;
        foreach (var addr in recording.Steps.Select(s => s.Method).Where(a => !string.IsNullOrEmpty(a)))
        {
            dict[addr] = dict.TryGetValue(addr, out var n) ? n + 1 : 1;
        }
        return dict;
    }

    // ---- model assembly --------------------------------------------

    private static Dictionary<string, object?> BuildDocumentModel(
        string serverUrl,
        IReadOnlyList<BowireServiceInfo> services,
        AsyncApiExportOptions options,
        Dictionary<string, int> coverage)
    {
        var (host, protocol, scheme) = ParseServer(serverUrl);
        var title = options.Title ?? (string.IsNullOrEmpty(host) ? "Exported from Bowire" : host);
        var version = options.Version ?? "1.0.0";

        var channels = new Dictionary<string, object?>(StringComparer.Ordinal);
        var operations = new Dictionary<string, object?>(StringComparer.Ordinal);
        var messages = new Dictionary<string, object?>(StringComparer.Ordinal);

        var seenChannelIds = new HashSet<string>(StringComparer.Ordinal);
        var seenOperationIds = new HashSet<string>(StringComparer.Ordinal);
        var seenMessageIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var svc in services)
        {
            foreach (var method in svc.Methods)
            {
                var address = method.Name;
                if (string.IsNullOrWhiteSpace(address)) continue;

                var channelId = MakeUniqueId(seenChannelIds, ToSafeId(address));
                var action = ClassifyAction(method);

                // Message — both input and output may carry payload
                // shapes; for send operations the input matters, for
                // receive the output. Always pick the side that's
                // actually the wire payload.
                var msgInfo = action == "send" ? method.InputType : method.OutputType;
                var messageRefId = msgInfo is null ? null : RegisterMessage(messages, seenMessageIds, msgInfo);

                var channelEntry = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["address"] = address,
                };
                if (messageRefId is not null)
                {
                    channelEntry["messages"] = new Dictionary<string, object?>
                    {
                        [messageRefId] = new Dictionary<string, object?>
                        {
                            ["$ref"] = "#/components/messages/" + messageRefId
                        }
                    };
                }
                var bindings = BuildChannelBindings(protocol, scheme, method);
                if (bindings is not null) channelEntry["bindings"] = bindings;
                if (!string.IsNullOrEmpty(method.Description))
                    channelEntry["description"] = method.Description;
                channels[channelId] = channelEntry;

                var opId = MakeUniqueId(seenOperationIds, $"{channelId}_{action}");
                var operationEntry = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["action"] = action,
                    ["channel"] = new Dictionary<string, object?> { ["$ref"] = "#/channels/" + channelId },
                };
                if (!string.IsNullOrEmpty(method.Summary))
                    operationEntry["summary"] = method.Summary;
                // x-bowire-coverage: only emitted when a recording was
                // supplied (coverage dict is empty otherwise). Channels
                // with no captured steps but a non-empty recording get
                // an explicit `recorded: false, stepCount: 0` so peer
                // consumers see the gap rather than ambiguity.
                if (coverage.Count > 0)
                {
                    var stepCount = coverage.TryGetValue(address, out var n) ? n : 0;
                    operationEntry["x-bowire-coverage"] = new Dictionary<string, object?>
                    {
                        ["recorded"] = stepCount > 0,
                        ["stepCount"] = stepCount,
                    };
                }
                operations[opId] = operationEntry;
            }
        }

        var doc = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["asyncapi"] = "3.0.0",
            ["info"] = new Dictionary<string, object?>
            {
                ["title"] = title,
                ["version"] = version,
            },
            ["servers"] = new Dictionary<string, object?>
            {
                ["default"] = new Dictionary<string, object?>
                {
                    ["host"] = string.IsNullOrEmpty(host) ? serverUrl : host,
                    ["protocol"] = protocol,
                }
            },
            ["channels"] = channels,
            ["operations"] = operations,
        };
        if (messages.Count > 0)
        {
            doc["components"] = new Dictionary<string, object?>
            {
                ["messages"] = messages,
            };
        }
        return doc;
    }

    // ---- classification --------------------------------------------

    /// <summary>
    /// Pick <c>send</c> or <c>receive</c> for a method. Most messaging
    /// plugins encode the verb in <see cref="BowireMethodInfo.FullName"/>
    /// (<c>mqtt/foo/publish</c>, <c>nats/foo/subscribe</c>, …); when
    /// that's missing, the method shape's
    /// <see cref="BowireMethodInfo.ServerStreaming"/> flag is the
    /// fallback (a server-streaming method is a receive).
    /// </summary>
    internal static string ClassifyAction(BowireMethodInfo method)
    {
        var full = method.FullName ?? string.Empty;
        if (EndsAny(full, "/publish", "/produce", "/send")) return "send";
        if (EndsAny(full, "/subscribe", "/consume", "/receive", "/listen")) return "receive";
        // Method-shape fallback: server-streaming = a receive,
        // anything else = a send.
        return method.ServerStreaming ? "receive" : "send";

        static bool EndsAny(string s, params string[] suffixes)
            => suffixes.Any(suffix => s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    // ---- bindings --------------------------------------------------

    /// <summary>
    /// Build the channel-level <c>bindings</c> block for the given
    /// protocol. Empty / null when the protocol has no binding key
    /// (e.g. plain TCP). The key chosen matches what
    /// <see cref="BowireAsyncApiProtocol"/> resolves on the loader
    /// side.
    /// </summary>
    private static Dictionary<string, object?>? BuildChannelBindings(
        string protocol, string scheme, BowireMethodInfo method)
    {
        switch (protocol)
        {
            case "mqtt":
            case "mqtts":
                return new Dictionary<string, object?>
                {
                    ["mqtt"] = new Dictionary<string, object?>
                    {
                        ["topic"] = method.Name,
                        ["bindingVersion"] = "0.2.0",
                    }
                };
            case "nats":
                return new Dictionary<string, object?>
                {
                    ["nats"] = new Dictionary<string, object?>
                    {
                        ["bindingVersion"] = "0.1.0",
                    }
                };
            case "kafka":
                return new Dictionary<string, object?>
                {
                    ["kafka"] = new Dictionary<string, object?>
                    {
                        ["topic"] = method.Name,
                        ["bindingVersion"] = "0.5.0",
                    }
                };
            case "ws":
            case "wss":
                return new Dictionary<string, object?>
                {
                    ["ws"] = new Dictionary<string, object?>
                    {
                        ["bindingVersion"] = "0.1.0",
                    }
                };
            case "amqp":
            case "amqps":
                return new Dictionary<string, object?>
                {
                    ["amqp"] = new Dictionary<string, object?>
                    {
                        ["bindingVersion"] = "0.3.0",
                    }
                };
            case "amqp1":
            case "amqps1":
                return new Dictionary<string, object?>
                {
                    ["amqp1"] = new Dictionary<string, object?>
                    {
                        ["bindingVersion"] = "0.1.0",
                    }
                };
            case "http":
            case "https":
                return new Dictionary<string, object?>
                {
                    ["http"] = new Dictionary<string, object?>
                    {
                        ["method"] = string.IsNullOrEmpty(method.HttpMethod) ? "POST" : method.HttpMethod,
                        ["bindingVersion"] = "0.3.0",
                    }
                };
            default:
                return null;
        }
    }

    // ---- messages --------------------------------------------------

    private static string RegisterMessage(
        Dictionary<string, object?> messages,
        HashSet<string> seen,
        BowireMessageInfo info)
    {
        var id = MakeUniqueId(seen, ToSafeId(string.IsNullOrEmpty(info.FullName) ? info.Name : info.FullName));
        if (messages.ContainsKey(id)) return id;
        var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = info.Name,
            ["payload"] = BuildPayloadSchema(info),
        };
        messages[id] = entry;
        return id;
    }

    private static Dictionary<string, object?> BuildPayloadSchema(BowireMessageInfo info)
    {
        var props = new Dictionary<string, object?>(StringComparer.Ordinal);
        var required = new List<string>();
        if (info.Fields is { Count: > 0 })
        {
            foreach (var field in info.Fields.Where(f => !string.IsNullOrEmpty(f.Name)))
            {
                props[field.Name] = MapFieldType(field.Type);
                // Proto's "LABEL_OPTIONAL" treats fields as
                // omissible, but every non-optional label is
                // required on the wire; mirror that into JSON
                // Schema's `required` block.
                if (!string.Equals(field.Label, "LABEL_OPTIONAL", StringComparison.Ordinal))
                    required.Add(field.Name);
            }
        }
        var schema = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "object",
        };
        if (props.Count > 0) schema["properties"] = props;
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    /// <summary>
    /// Map Bowire / proto type names to JSON Schema type tags.
    /// Anything unrecognised falls through as <c>string</c>, which
    /// matches the workbench's default form-input shape.
    /// </summary>
    internal static Dictionary<string, object?> MapFieldType(string? protoType)
    {
        // AsyncAPI/JSON-Schema type tags are conventionally lowercase
        // ("integer", "boolean", "string"). CA1308 prefers uppercase
        // normalisation, but the spec we're emitting is case-sensitive
        // and demands lowercase, so we suppress.
#pragma warning disable CA1308
        var t = protoType?.ToLowerInvariant() ?? "string";
#pragma warning restore CA1308
        return t switch
        {
            "bool" or "boolean" => new() { ["type"] = "boolean" },
            "int32" or "int64" or "uint32" or "uint64" or "sint32" or "sint64"
                or "fixed32" or "fixed64" or "sfixed32" or "sfixed64" or "integer"
                => new() { ["type"] = "integer" },
            "double" or "float" or "number" => new() { ["type"] = "number" },
            "bytes" => new() { ["type"] = "string", ["contentEncoding"] = "base64" },
            _ => new() { ["type"] = "string" },
        };
    }

    // ---- URL / id helpers ------------------------------------------

    /// <summary>
    /// Split a Bowire server URL into <c>(host, asyncapiProtocol,
    /// scheme)</c>. The protocol returned is the lowercase AsyncAPI
    /// binding key (<c>mqtt</c> / <c>nats</c> / <c>kafka</c> /
    /// <c>ws</c> / <c>amqp</c> / <c>amqp1</c> / <c>http</c>) — for
    /// schemes that don't have a 1:1 AsyncAPI binding the scheme is
    /// returned verbatim.
    /// </summary>
    internal static (string Host, string Protocol, string Scheme) ParseServer(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
        {
            // Fall back to "scheme://host" parsing so naked
            // host:port still produces something sensible.
            return (serverUrl, "tcp", "tcp");
        }
        // URI scheme normalisation: AsyncAPI binding keys are
        // lowercase by spec convention ("mqtt", "nats", "kafka"),
        // so we lowercase here to match. CA1308 prefers uppercase
        // for security-sensitive normalisation, but binding-key
        // matching has no such concern.
#pragma warning disable CA1308
        var scheme = uri.Scheme.ToLowerInvariant();
#pragma warning restore CA1308
        var protocol = scheme switch
        {
            "mqtt" or "tcp" => "mqtt",
            "mqtts" or "ssl" => "mqtts",
            "ws" => "ws",
            "wss" => "wss",
            "http" => "http",
            "https" => "https",
            "nats" => "nats",
            "kafka" => "kafka",
            "amqp" => "amqp",
            "amqps" => "amqps",
            "amqp1" => "amqp1",
            "amqps1" => "amqps1",
            "pulsar" => "pulsar",
            _ => scheme,
        };
        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return (host, protocol, scheme);
    }

    /// <summary>
    /// Sanitise a string for use as an AsyncAPI map key: replace any
    /// non-letter/digit with '_' and collapse runs. AsyncAPI 3 doesn't
    /// formally restrict keys, but downstream tools (Swagger UI,
    /// AsyncAPI Studio, codegen) all assume keys are
    /// identifier-shaped, so we conform.
    /// </summary>
    internal static string ToSafeId(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "_";
        var sb = new System.Text.StringBuilder(raw.Length);
        var lastWasSep = false;
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasSep = false;
            }
            else if (!lastWasSep && sb.Length > 0)
            {
                sb.Append('_');
                lastWasSep = true;
            }
        }
        // Trim trailing separator and start-with-digit case.
        var s = sb.ToString().TrimEnd('_');
        if (s.Length == 0) return "_";
        if (char.IsDigit(s[0])) s = "_" + s;
        return s;
    }

    private static string MakeUniqueId(HashSet<string> taken, string candidate)
    {
        if (taken.Add(candidate)) return candidate;
        var i = 2;
        string attempt;
        do { attempt = candidate + "_" + i++; } while (!taken.Add(attempt));
        return attempt;
    }

    // ---- serialisation ---------------------------------------------

    private static string SerializeYaml(Dictionary<string, object?> doc)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        return serializer.Serialize(doc);
    }

    private static readonly System.Text.Json.JsonSerializerOptions s_jsonOpts =
        new() { WriteIndented = true };

    private static string SerializeJson(Dictionary<string, object?> doc)
    {
        // Same in-memory tree, JSON output for the (rare) consumer
        // that wants the JSON serialisation of AsyncAPI 3.
        return System.Text.Json.JsonSerializer.Serialize(doc, s_jsonOpts);
    }
}

/// <summary>Output knobs for <see cref="AsyncApiDocumentBuilder.Build"/>.</summary>
public sealed record AsyncApiExportOptions
{
    /// <summary>Output format. Defaults to YAML.</summary>
    public AsyncApiExportFormat Format { get; init; } = AsyncApiExportFormat.Yaml;

    /// <summary>Override the document title. Defaults to the host name.</summary>
    public string? Title { get; init; }

    /// <summary>Override the document version. Defaults to "1.0.0".</summary>
    public string? Version { get; init; }
}

/// <summary>Output formats <see cref="AsyncApiDocumentBuilder"/> can emit.</summary>
public enum AsyncApiExportFormat
{
    /// <summary>YAML — the AsyncAPI ecosystem's canonical format.</summary>
    Yaml,
    /// <summary>JSON — for consumers that read AsyncAPI as JSON only.</summary>
    Json,
}

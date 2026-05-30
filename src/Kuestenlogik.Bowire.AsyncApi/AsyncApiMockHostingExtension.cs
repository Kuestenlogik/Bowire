// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.AsyncApi;

/// <summary>
/// AsyncAPI plugin's mock-host extension — the messaging-side sibling
/// of <c>RestMockHostingExtension</c>. Serves the recording's verbatim
/// <see cref="BowireRecording.SourceSchema"/> back under
/// <c>GET /asyncapi.{yaml,yml,json}</c> when the format tag identifies
/// an AsyncAPI document. Lets a peer Bowire workbench point at the
/// mock and discover the <em>full</em> declared topology — channels,
/// operations, bindings — rather than just the topic slice the
/// recording happens to replay.
/// </summary>
/// <remarks>
/// <para>
/// The HTTP endpoints are served on the mock-server's HTTP port
/// regardless of what wire the original target spoke (MQTT, NATS,
/// Kafka, &amp;c). The mock already binds an HTTP listener — replay
/// of non-HTTP captures runs through a side-channel inside the same
/// process — so adding two GET routes is essentially free and lets
/// AsyncAPI discovery hit a stable URL even when the wire is over
/// TCP/UDP.
/// </para>
/// <para>
/// Picked up via assembly-scan
/// (<c>PluginManager.EnumeratePluginServices&lt;IBowireMockHostingExtension&gt;</c>)
/// at mock-server startup — same discovery path as
/// <c>RestMockHostingExtension</c> and the gRPC reflection extension.
/// Recordings without an AsyncAPI source schema (gRPC, OpenAPI, or
/// pre-v1.7 captures) silently no-op.
/// </para>
/// </remarks>
public sealed class AsyncApiMockHostingExtension : IBowireMockHostingExtension
{
    /// <inheritdoc/>
    public string Id => "asyncapi";

    /// <inheritdoc/>
    public void MapEndpoints(IEndpointRouteBuilder endpoints, BowireRecording recording)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(recording);

        var schema = recording.SourceSchema;
        if (schema is null) return;
        if (!IsAsyncApi(schema.Format)) return;
        if (string.IsNullOrEmpty(schema.Content)) return;

        var sourceIsJson = LooksLikeJson(schema.Content);

        endpoints.MapGet("/asyncapi.yaml", () => sourceIsJson
            ? Results.Content(JsonToYaml(schema.Content), "application/yaml")
            : Results.Content(schema.Content, "application/yaml"));

        endpoints.MapGet("/asyncapi.yml", () => sourceIsJson
            ? Results.Content(JsonToYaml(schema.Content), "application/yaml")
            : Results.Content(schema.Content, "application/yaml"));

        endpoints.MapGet("/asyncapi.json", () => sourceIsJson
            ? Results.Content(schema.Content, "application/json")
            : Results.Content(YamlToJson(schema.Content), "application/json"));
    }

    /// <summary>True when the format tag identifies an AsyncAPI document.</summary>
    internal static bool IsAsyncApi(string format)
        => format is not null && format.StartsWith("asyncapi-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Same heuristic the REST extension uses — first non-whitespace
    /// char tells JSON apart from YAML. Cheap, allocation-free.
    /// </summary>
    internal static bool LooksLikeJson(string s)
    {
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) continue;
            return ch is '{' or '[';
        }
        return false;
    }

    /// <summary>
    /// Convert a YAML AsyncAPI document to JSON via YamlDotNet's
    /// representation model. No semantic interpretation, just a
    /// format conversion.
    /// </summary>
    internal static string YamlToJson(string yaml)
    {
        try
        {
            using var reader = new StringReader(yaml);
            var yamlStream = new YamlDotNet.RepresentationModel.YamlStream();
            yamlStream.Load(reader);
            if (yamlStream.Documents.Count == 0) return "{}";
            var node = yamlStream.Documents[0].RootNode;
            var obj = YamlNodeToObject(node);
            return System.Text.Json.JsonSerializer.Serialize(obj, s_jsonOpts);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return yaml;
        }
    }

    /// <summary>Convert a JSON AsyncAPI document to YAML.</summary>
    internal static string JsonToYaml(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var obj = JsonElementToObject(doc.RootElement);
            var serializer = new YamlDotNet.Serialization.SerializerBuilder()
                .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.NullNamingConvention.Instance)
                .Build();
            return serializer.Serialize(obj);
        }
        catch (System.Text.Json.JsonException)
        {
            return json;
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions s_jsonOpts =
        new() { WriteIndented = true };

    private static object? YamlNodeToObject(YamlDotNet.RepresentationModel.YamlNode node)
    {
        switch (node)
        {
            case YamlDotNet.RepresentationModel.YamlScalarNode scalar:
                return scalar.Value;
            case YamlDotNet.RepresentationModel.YamlMappingNode map:
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (k, v) in map.Children)
                {
                    var key = (k as YamlDotNet.RepresentationModel.YamlScalarNode)?.Value ?? k.ToString()!;
                    dict[key] = YamlNodeToObject(v);
                }
                return dict;
            case YamlDotNet.RepresentationModel.YamlSequenceNode seq:
                var list = new List<object?>(seq.Children.Count);
                foreach (var child in seq.Children) list.Add(YamlNodeToObject(child));
                return list;
            default:
                return null;
        }
    }

    private static object? JsonElementToObject(System.Text.Json.JsonElement el)
    {
        switch (el.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var prop in el.EnumerateObject())
                    dict[prop.Name] = JsonElementToObject(prop.Value);
                return dict;
            case System.Text.Json.JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var child in el.EnumerateArray())
                    list.Add(JsonElementToObject(child));
                return list;
            case System.Text.Json.JsonValueKind.String:
                return el.GetString();
            case System.Text.Json.JsonValueKind.Number:
                if (el.TryGetInt64(out var l)) return l;
                return el.GetDouble();
            case System.Text.Json.JsonValueKind.True: return true;
            case System.Text.Json.JsonValueKind.False: return false;
            case System.Text.Json.JsonValueKind.Null:
            default:
                return null;
        }
    }
}

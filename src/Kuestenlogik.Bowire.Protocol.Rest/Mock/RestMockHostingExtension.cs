// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Protocol.Rest.Mock;

/// <summary>
/// REST plugin's mock-host extension. Serves the recording's verbatim
/// <see cref="BowireRecording.SourceSchema"/> back under the conventional
/// OpenAPI endpoints so peer Bowire discovery against the mock returns
/// the <em>full</em> contract the original target advertised, not just
/// the slice this recording happens to replay.
/// </summary>
/// <remarks>
/// <para>
/// Endpoints mapped (only when the recording carries an OpenAPI
/// <see cref="RecordingSourceSchema"/>):
/// </para>
/// <list type="bullet">
///   <item><c>GET /openapi.json</c> — content negotiated to JSON.
///     YAML sources are converted to JSON via the OpenAPI reader so the
///     downstream consumer doesn't have to depend on a YAML parser.</item>
///   <item><c>GET /openapi.yaml</c> + <c>GET /openapi.yml</c> — verbatim
///     text when the source was YAML; serialised to YAML when the source
///     was JSON.</item>
///   <item><c>GET /swagger.json</c> — alias for <c>/openapi.json</c>;
///     a lot of legacy clients hardcode this path.</item>
/// </list>
/// <para>
/// Picked up via assembly-scan (<c>IBowirePluginLoader.EnumerateServices&lt;IBowireMockHostingExtension&gt;</c>)
/// at mock-server startup — same discovery pattern gRPC uses for its
/// reflection-service mapping. Recordings without an OpenAPI source
/// schema (gRPC-only, AsyncAPI-only, or pre-v1.7 captures that pre-date
/// the sidecar) silently no-op.
/// </para>
/// </remarks>
public sealed class RestMockHostingExtension : IBowireMockHostingExtension
{
    /// <inheritdoc/>
    public string Id => "rest";

    /// <inheritdoc/>
    public void MapEndpoints(IEndpointRouteBuilder endpoints, BowireRecording recording)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(recording);

        var schema = recording.SourceSchema;
        if (schema is null) return;
        if (!IsOpenApi(schema.Format)) return;
        if (string.IsNullOrEmpty(schema.Content)) return;

        // Detect source content shape: JSON sources start with '{' or
        // '['; everything else is treated as YAML. Cheap, allocation-
        // free, and matches what every OpenAPI reader does anyway.
        var sourceIsJson = LooksLikeJson(schema.Content);

        endpoints.MapGet("/openapi.json", () => sourceIsJson
            ? Results.Content(schema.Content, "application/json")
            : Results.Content(YamlToJson(schema.Content), "application/json"));

        endpoints.MapGet("/openapi.yaml", () => sourceIsJson
            ? Results.Content(JsonToYaml(schema.Content), "application/yaml")
            : Results.Content(schema.Content, "application/yaml"));

        endpoints.MapGet("/openapi.yml", () => sourceIsJson
            ? Results.Content(JsonToYaml(schema.Content), "application/yaml")
            : Results.Content(schema.Content, "application/yaml"));

        // Legacy Swagger alias — many older clients (and tools like the
        // Swagger UI default config) probe `/swagger.json` first.
        endpoints.MapGet("/swagger.json", () => sourceIsJson
            ? Results.Content(schema.Content, "application/json")
            : Results.Content(YamlToJson(schema.Content), "application/json"));
    }

    /// <summary>True when the format tag identifies an OpenAPI document.</summary>
    internal static bool IsOpenApi(string format)
        => format is not null && format.StartsWith("openapi-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Cheap "is this JSON?" probe — skip leading whitespace then
    /// check the first non-whitespace character. Same heuristic the
    /// OpenAPI reader uses internally to pick a parser.
    /// </summary>
    internal static bool LooksLikeJson(string s)
    {
        var firstNonWs = s.SkipWhile(char.IsWhiteSpace).FirstOrDefault();
        return firstNonWs is '{' or '[';
    }

    /// <summary>
    /// Convert a YAML OpenAPI document to JSON by round-tripping through
    /// YamlDotNet's representation model. Keeps the document tree intact;
    /// no schema-semantic interpretation, just a format conversion.
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
            // If the source claims to be YAML but doesn't parse, hand
            // the raw text through under the JSON content-type — the
            // downstream consumer will surface its own parse error
            // with the source context preserved.
            return yaml;
        }
    }

    /// <summary>
    /// Convert a JSON OpenAPI document to YAML by deserialising then
    /// re-emitting through YamlDotNet's serializer. Same fallback as
    /// <see cref="YamlToJson"/> when the input doesn't parse.
    /// </summary>
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
                return ScalarToObject(scalar);
            case YamlDotNet.RepresentationModel.YamlMappingNode map:
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (k, v) in map.Children)
                {
                    var key = (k as YamlDotNet.RepresentationModel.YamlScalarNode)?.Value ?? k.ToString()!;
                    dict[key] = YamlNodeToObject(v);
                }
                return dict;
            case YamlDotNet.RepresentationModel.YamlSequenceNode seq:
                return seq.Children.Select(YamlNodeToObject).ToList();
            default:
                return null;
        }
    }

    /// <summary>
    /// The JSON value of a YAML scalar under the YAML 1.2 core schema
    /// (#901). Only a plain, untagged scalar is resolved; quoting or an
    /// explicit tag (<c>'true'</c>, <c>!!str 7</c>) keeps it a string.
    /// Numbers keep their literal where it is valid JSON, so <c>1.5</c> or a
    /// 20-digit integer survives unchanged; <c>.inf</c> and <c>.nan</c> have
    /// no JSON form and stay strings.
    /// </summary>
    private static object? ScalarToObject(YamlDotNet.RepresentationModel.YamlScalarNode scalar)
    {
        var text = scalar.Value ?? string.Empty;
        if (scalar.Style != YamlDotNet.Core.ScalarStyle.Plain || !scalar.Tag.IsEmpty) return text;

        switch (text)
        {
            case "" or "~" or "null" or "Null" or "NULL": return null;
            case "true" or "True" or "TRUE": return true;
            case "false" or "False" or "FALSE": return false;
        }

        if (CoreSchemaInt.IsMatch(text) || CoreSchemaFloat.IsMatch(text))
        {
            if (JsonNumber.IsMatch(text)) return JsonNumberLiteral(text);
            if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
                return d;
            return text;
        }
        if (CoreSchemaHex.IsMatch(text)
            && ulong.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out var hex))
            return hex;
        if (CoreSchemaOctal.IsMatch(text) && text.Length <= 23)
            return Convert.ToInt64(text[2..], 8);
        return text;
    }

    /// <summary>The number literal itself, emitted verbatim by the JSON serializer.</summary>
    private static System.Text.Json.JsonElement JsonNumberLiteral(string text)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // YAML 1.2 core schema (https://yaml.org/spec/1.2.2/#1032-tag-resolution).
    private static readonly System.Text.RegularExpressions.Regex CoreSchemaInt =
        new(@"^[-+]?[0-9]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex CoreSchemaFloat =
        new(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex CoreSchemaHex =
        new(@"^0x[0-9a-fA-F]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex CoreSchemaOctal =
        new(@"^0o[0-7]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex JsonNumber =
        new(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][-+]?[0-9]+)?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

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
                return el.EnumerateArray().Select(JsonElementToObject).ToList();
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

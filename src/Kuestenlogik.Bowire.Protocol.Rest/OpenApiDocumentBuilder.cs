// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Kuestenlogik.Bowire.Protocol.Rest;

/// <summary>
/// Inverse of the <see cref="IBowireOpenApiAdapter"/>'s discovery path:
/// emit an OpenAPI 3.0 document from a Bowire discovery result. Sibling to
/// <c>AsyncApiDocumentBuilder</c> — same pure-builder shape, no IO, no
/// wire-plugin lookup. Lets the workbench, the CLI, or a future MCP tool
/// turn a discovered REST surface back into a portable schema artefact teams
/// can check into their docs repo or feed back into Bowire as a discovery
/// source.
/// </summary>
/// <remarks>
/// <para>
/// Mapping <see cref="BowireServiceInfo"/> → OpenAPI:
/// </para>
/// <list type="bullet">
///   <item><c>servers[0]</c> ← the <c>serverUrl</c> argument verbatim.</item>
///   <item><c>paths[&lt;HttpPath&gt;][&lt;httpVerb&gt;]</c> ← one entry per
///     method; <c>operationId</c> = method name, <c>summary</c> /
///     <c>description</c> / <c>deprecated</c> ride through.</item>
///   <item><c>parameters[]</c> ← path placeholders detected as
///     <c>{name}</c> segments in <see cref="BowireMethodInfo.HttpPath"/>,
///     plus any <see cref="BowireFieldInfo"/> whose
///     <see cref="BowireFieldInfo.Source"/> ∈ <c>{path, query, header}</c>.</item>
///   <item><c>requestBody</c> ← body fields synthesised into a
///     JSON-Schema property map. POST / PUT / PATCH get a body; GET /
///     DELETE / HEAD don't (heuristic — matches the OpenAPI convention).</item>
///   <item><c>responses["200"].content.application/json.schema</c> ←
///     output-type fields, same JSON-Schema synthesis.</item>
/// </list>
/// <para>
/// When a <see cref="BowireRecording"/> is supplied the exporter
/// stamps each operation with an <c>x-bowire-coverage</c> extension
/// reporting whether the recording carries at least one captured step
/// for this <c>(httpVerb, httpPath)</c> pair, plus the captured step
/// count. That's the coverage layer your mock-as-stand-in needs:
/// consumers see the <em>full</em> contract the original advertised
/// (because the mock serves back the recording's
/// <see cref="BowireRecording.SourceSchema"/> verbatim) and exactly
/// which slice of it this recording can replay deterministically vs.
/// which slice would have to fall back to schema-generated samples.
/// </para>
/// </remarks>
public static class OpenApiDocumentBuilder
{
    /// <summary>Build the OpenAPI document.</summary>
    /// <param name="serverUrl">URL the discovery ran against; lands on <c>servers[0].url</c>.</param>
    /// <param name="services">Services returned by <c>BowireRestProtocol.DiscoverAsync</c>.</param>
    /// <param name="recording">
    /// Optional recording — when supplied, every operation gets an
    /// <c>x-bowire-coverage</c> entry from the recording's step list.
    /// </param>
    /// <param name="options">Output format / title / version knobs.</param>
    public static string Build(
        string serverUrl,
        IReadOnlyList<BowireServiceInfo> services,
        BowireRecording? recording = null,
        OpenApiExportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        ArgumentNullException.ThrowIfNull(services);
        options ??= new OpenApiExportOptions();

        var coverage = BuildCoverageIndex(recording);
        var doc = BuildDocumentModel(serverUrl, services, options, coverage);
        return options.Format == OpenApiExportFormat.Json
            ? SerializeJson(doc)
            : SerializeYaml(doc);
    }

    // ---- model assembly -------------------------------------------

    private static Dictionary<string, object?> BuildDocumentModel(
        string serverUrl,
        IReadOnlyList<BowireServiceInfo> services,
        OpenApiExportOptions options,
        Dictionary<string, CoverageEntry> coverage)
    {
        var title = options.Title ?? PickDocTitle(services, serverUrl);
        var version = options.Version
            ?? services.FirstOrDefault(s => !string.IsNullOrEmpty(s.Version))?.Version
            ?? "1.0.0";

        var paths = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var svc in services)
        {
            foreach (var method in svc.Methods)
            {
                var path = method.HttpPath;
                var verb = (method.HttpMethod ?? "GET").ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(path)) continue;

                // Get-or-create the path-item object, then add the verb
                // entry — OpenAPI groups multiple verbs under one path.
                if (!paths.TryGetValue(path, out var pathItemObj)
                    || pathItemObj is not Dictionary<string, object?> pathItem)
                {
                    pathItem = new Dictionary<string, object?>(StringComparer.Ordinal);
                    paths[path] = pathItem;
                }
                // OpenAPI requires the verb key in lowercase. CA1308
                // prefers uppercase normalisation; the spec wins.
#pragma warning disable CA1308
                pathItem[verb.ToLowerInvariant()] = BuildOperation(method, verb, path, coverage);
#pragma warning restore CA1308
            }
        }

        var doc = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["openapi"] = "3.0.0",
            ["info"] = new Dictionary<string, object?>
            {
                ["title"] = title,
                ["version"] = version,
            },
            ["servers"] = new List<object?>
            {
                new Dictionary<string, object?> { ["url"] = serverUrl }
            },
            ["paths"] = paths,
        };
        return doc;
    }

    private static Dictionary<string, object?> BuildOperation(
        BowireMethodInfo method, string verb, string path,
        Dictionary<string, CoverageEntry> coverage)
    {
        var op = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operationId"] = method.Name,
        };
        if (!string.IsNullOrEmpty(method.Summary)) op["summary"] = method.Summary;
        if (!string.IsNullOrEmpty(method.Description)) op["description"] = method.Description;
        if (method.Deprecated) op["deprecated"] = true;

        var parameters = BuildParameters(method, path);
        if (parameters.Count > 0) op["parameters"] = parameters;

        if (VerbCarriesBody(verb))
        {
            var body = BuildRequestBody(method);
            if (body is not null) op["requestBody"] = body;
        }

        op["responses"] = BuildResponses(method);

        // x-bowire-coverage — populated only when a recording was
        // supplied; addresses by (verb, path) since the recording
        // captures HTTP-level identifiers, not Bowire's method name
        // (which the user can rename per-recording).
        var key = CoverageKey(verb, path);
        if (coverage.TryGetValue(key, out var entry))
        {
            op["x-bowire-coverage"] = new Dictionary<string, object?>
            {
                ["recorded"] = entry.Recorded,
                ["stepCount"] = entry.StepCount,
            };
        }
        else if (coverage.Count > 0)
        {
            // We have a recording but no step for this operation —
            // flag it explicitly so consumers see the gap without
            // having to diff schema vs. recording themselves.
            op["x-bowire-coverage"] = new Dictionary<string, object?>
            {
                ["recorded"] = false,
                ["stepCount"] = 0,
            };
        }

        return op;
    }

    private static List<object?> BuildParameters(BowireMethodInfo method, string path)
    {
        var parameters = new List<object?>();
        // Track path-template placeholders so we emit one param per
        // {name} even if the input-type doesn't carry a matching field.
        var pathPlaceholders = ExtractPathPlaceholders(path);
        var emittedPathNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Inspect input-type fields for non-body sources.
        if (method.InputType?.Fields is { Count: > 0 } fields)
        {
            foreach (var field in fields)
            {
                // OpenAPI parameter "in" is lowercase per spec; CA1308
                // suppression analogous to the verb normalisation above.
#pragma warning disable CA1308
                var src = (field.Source ?? string.Empty).ToLowerInvariant();
#pragma warning restore CA1308
                if (src is not ("path" or "query" or "header")) continue;
                parameters.Add(BuildParameter(field, src));
                if (src == "path") emittedPathNames.Add(field.Name);
            }
        }

        // Backfill placeholders we haven't emitted yet (the field
        // metadata was missing or didn't tag them with Source=path).
        foreach (var placeholder in pathPlaceholders)
        {
            if (emittedPathNames.Add(placeholder))
            {
                parameters.Add(new Dictionary<string, object?>
                {
                    ["name"] = placeholder,
                    ["in"] = "path",
                    ["required"] = true,
                    ["schema"] = new Dictionary<string, object?> { ["type"] = "string" },
                });
            }
        }

        return parameters;
    }

    private static Dictionary<string, object?> BuildParameter(BowireFieldInfo field, string @in)
    {
        var p = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = field.Name,
            ["in"] = @in,
            ["schema"] = MapFieldType(field.Type),
        };
        // Path params are always required per OpenAPI spec; others
        // follow the field's required flag.
        if (@in == "path" || field.Required) p["required"] = true;
        if (!string.IsNullOrEmpty(field.Description)) p["description"] = field.Description;
        if (!string.IsNullOrEmpty(field.Example)) p["example"] = field.Example;
        return p;
    }

    private static Dictionary<string, object?>? BuildRequestBody(BowireMethodInfo method)
    {
        var bodyFields = method.InputType?.Fields?
            .Where(f =>
            {
                // Same casing rule as the parameter side — OpenAPI
                // sources are lowercase strings; CA1308 suppression
                // applies for the same reason.
#pragma warning disable CA1308
                var src = (f.Source ?? string.Empty).ToLowerInvariant();
#pragma warning restore CA1308
                // Untagged fields default to body when the verb carries
                // a body — matches how the OpenAPI ingest side treats
                // unannotated request fields.
                return src is "body" or "";
            })
            .ToList();
        if (bodyFields is null || bodyFields.Count == 0) return null;

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["content"] = new Dictionary<string, object?>
            {
                ["application/json"] = new Dictionary<string, object?>
                {
                    ["schema"] = BuildObjectSchema(bodyFields),
                }
            }
        };
    }

    private static Dictionary<string, object?> BuildResponses(BowireMethodInfo method)
    {
        var fields = method.OutputType?.Fields;
        var schema = fields is { Count: > 0 }
            ? BuildObjectSchema(fields)
            : new Dictionary<string, object?> { ["type"] = "object" };
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["200"] = new Dictionary<string, object?>
            {
                ["description"] = "OK",
                ["content"] = new Dictionary<string, object?>
                {
                    ["application/json"] = new Dictionary<string, object?>
                    {
                        ["schema"] = schema,
                    }
                }
            }
        };
    }

    // ---- schema synthesis -----------------------------------------

    private static Dictionary<string, object?> BuildObjectSchema(IEnumerable<BowireFieldInfo> fields)
    {
        var props = new Dictionary<string, object?>(StringComparer.Ordinal);
        var required = new List<string>();
        foreach (var field in fields)
        {
            if (string.IsNullOrEmpty(field.Name)) continue;
            props[field.Name] = MapFieldType(field.Type);
            if (field.Required) required.Add(field.Name);
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
    /// Map Bowire / proto type names to JSON-Schema type tags. Mirrors
    /// the AsyncAPI builder's mapper so the two exporters report types
    /// consistently for the same input.
    /// </summary>
    internal static Dictionary<string, object?> MapFieldType(string? type)
    {
        // OpenAPI/JSON-Schema demands lowercase type tags; suppress
        // CA1308 (which prefers uppercase normalisation) here — this
        // is a spec-mandated casing, not a security-sensitive lookup.
#pragma warning disable CA1308
        var t = type?.ToLowerInvariant() ?? "string";
#pragma warning restore CA1308
        return t switch
        {
            "bool" or "boolean" => new() { ["type"] = "boolean" },
            "int32" or "int64" or "uint32" or "uint64" or "sint32" or "sint64"
                or "fixed32" or "fixed64" or "sfixed32" or "sfixed64" or "integer"
                => new() { ["type"] = "integer" },
            "double" or "float" or "number" => new() { ["type"] = "number" },
            "bytes" => new() { ["type"] = "string", ["format"] = "byte" },
            _ => new() { ["type"] = "string" },
        };
    }

    // ---- helpers --------------------------------------------------

    /// <summary>
    /// Extract path-template placeholders from an OpenAPI path string —
    /// the segments in <c>{...}</c>, e.g. <c>/users/{id}/posts/{postId}</c>
    /// → <c>["id", "postId"]</c>. Used to backfill <c>parameters[]</c>
    /// entries that the input-type metadata didn't cover.
    /// </summary>
    internal static List<string> ExtractPathPlaceholders(string path)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(path)) return result;
        var i = 0;
        while (i < path.Length)
        {
            var open = path.IndexOf('{', i);
            if (open < 0) break;
            var close = path.IndexOf('}', open + 1);
            if (close < 0) break;
            var name = path.Substring(open + 1, close - open - 1);
            // Drop OpenAPI 3.1-style style/explode suffixes if present
            // (e.g. {foo*}), they're rare in practice but cheap to guard.
            var trimmed = name.TrimEnd('*');
            if (!string.IsNullOrEmpty(trimmed)) result.Add(trimmed);
            i = close + 1;
        }
        return result;
    }

    /// <summary>
    /// Heuristic: which HTTP verbs typically carry a request body?
    /// Matches OpenAPI's own convention — GET/DELETE/HEAD/OPTIONS
    /// don't, the others do.
    /// </summary>
    internal static bool VerbCarriesBody(string verb) => verb switch
    {
        "POST" or "PUT" or "PATCH" => true,
        _ => false,
    };

    internal static string CoverageKey(string verb, string path)
        => verb.ToUpperInvariant() + " " + path;

    private static Dictionary<string, CoverageEntry> BuildCoverageIndex(BowireRecording? recording)
    {
        var dict = new Dictionary<string, CoverageEntry>(StringComparer.OrdinalIgnoreCase);
        if (recording?.Steps is null) return dict;
        foreach (var step in recording.Steps)
        {
            if (step.HttpPath is null || step.HttpVerb is null) continue;
            var key = CoverageKey(step.HttpVerb, step.HttpPath);
            if (dict.TryGetValue(key, out var entry))
                dict[key] = entry with { StepCount = entry.StepCount + 1 };
            else
                dict[key] = new CoverageEntry(Recorded: true, StepCount: 1);
        }
        return dict;
    }

    private static string PickDocTitle(IReadOnlyList<BowireServiceInfo> services, string serverUrl)
    {
        // Prefer the first non-empty service-level description, then
        // the host name from the URL, then a default.
        var firstWithDesc = services.FirstOrDefault(s => !string.IsNullOrEmpty(s.Description));
        if (firstWithDesc is not null) return firstWithDesc.Description!;
        if (Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)) return uri.Host;
        return "Exported from Bowire";
    }

    // ---- serialisation --------------------------------------------

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
        => System.Text.Json.JsonSerializer.Serialize(doc, s_jsonOpts);

    /// <summary>
    /// Coverage entry for one <c>(verb, path)</c> pair — the recording
    /// either carries replayable steps (<see cref="Recorded"/> true,
    /// <see cref="StepCount"/> &gt; 0) or it doesn't.
    /// </summary>
    internal sealed record CoverageEntry(bool Recorded, int StepCount);
}

/// <summary>Output knobs for <see cref="OpenApiDocumentBuilder.Build"/>.</summary>
public sealed record OpenApiExportOptions
{
    /// <summary>Output format. Defaults to YAML.</summary>
    public OpenApiExportFormat Format { get; init; } = OpenApiExportFormat.Yaml;

    /// <summary>Override the document title.</summary>
    public string? Title { get; init; }

    /// <summary>Override the document version.</summary>
    public string? Version { get; init; }
}

/// <summary>Output formats <see cref="OpenApiDocumentBuilder"/> can emit.</summary>
public enum OpenApiExportFormat
{
    /// <summary>YAML — the more common OpenAPI distribution format in the wild.</summary>
    Yaml,
    /// <summary>JSON — what most tooling generates natively (Swagger UI, codegen).</summary>
    Json,
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.Models;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Kuestenlogik.Bowire.Protocol.Rest;

/// <summary>
/// Discovers REST endpoints by fetching an OpenAPI 3 document from the user's
/// URL and converting it into Bowire's service/method/field model so the
/// existing form-based UI can render REST calls without any REST-specific
/// knowledge.
///
/// Built against Microsoft.OpenApi v2 (preview), which supports both OpenAPI
/// 3.0 and 3.1. The 3.1 type system uses a flags enum (<see cref="JsonSchemaType"/>)
/// instead of strings, and example/default values are <see cref="JsonNode"/>
/// instances rather than the v1 <c>IOpenApiAny</c> wrapper.
/// </summary>
internal static class OpenApiDiscovery
{
    /// <summary>
    /// Fetch the OpenAPI document from the EXACT URL the user provided.
    /// No path probing — the URL is taken literally. The plugin's
    /// <c>DiscoverAsync</c> handles the wrong-protocol case by silently
    /// returning no services so other plugins (gRPC, SignalR, ...) can
    /// succeed against the same URL.
    /// </summary>
    public static async Task<DiscoveredApi?> FetchAndParseAsync(
        string docUrl, HttpClient http, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(docUrl)) return null;
        if (!Uri.TryCreate(docUrl, UriKind.Absolute, out var docUri)) return null;

        try
        {
            using var resp = await http.GetAsync(docUri, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            // Quick content-type sanity check — don't try to parse HTML as OpenAPI
            var contentType = resp.Content?.Headers?.ContentType?.MediaType ?? string.Empty;
            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase)) return null;

            // OpenApiDocument.LoadAsync handles content-type sniffing for us.
            await using var stream = await resp.Content!.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var readResult = await OpenApiDocument.LoadAsync(stream, format: null, settings: null, ct).ConfigureAwait(false);

            if (readResult?.Document is null) return null;
            // Require at least one path to count it as "an OpenAPI document" — that's
            // the cheapest way to tell apart "valid OpenAPI" from "random JSON".
            if (readResult.Document.Paths is null || readResult.Document.Paths.Count == 0)
                return null;

            return new DiscoveredApi(docUrl, readResult.Document, readResult.Diagnostic);
        }
        catch
        {
            // Network errors, parser errors, wrong content-type — all treated the same:
            // return null and let other protocols try the URL.
            return null;
        }
    }

    /// <summary>
    /// Parses raw OpenAPI/Swagger document text (JSON or YAML) without any
    /// HTTP fetch. Used for documents uploaded via <see cref="OpenApiUploadStore"/>.
    /// </summary>
    public static async Task<DiscoveredApi?> ParseRawAsync(string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            await using var stream = new MemoryStream(bytes);
            var readResult = await OpenApiDocument.LoadAsync(stream, format: null, settings: null, ct).ConfigureAwait(false);
            if (readResult?.Document is null) return null;
            if (readResult.Document.Paths is null || readResult.Document.Paths.Count == 0) return null;
            return new DiscoveredApi("uploaded", readResult.Document, readResult.Diagnostic);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Convert a parsed OpenAPI document into Bowire services. Operations are
    /// grouped by their first tag (Postman-style). Operations without tags go
    /// into a "Default" service so they're not lost.
    /// </summary>
    public static List<BowireServiceInfo> BuildServices(OpenApiDocument doc)
    {
        var services = new Dictionary<string, List<BowireMethodInfo>>(StringComparer.Ordinal);

        foreach (var (path, pathItem) in doc.Paths)
        {
            if (pathItem.Operations is null) continue;
            foreach (var (operationType, operation) in pathItem.Operations)
            {
                var method = BuildMethod(path, operationType, operation);
                if (method is null) continue;

                string tag = "Default";
                if (operation.Tags is { Count: > 0 } tags)
                {
                    // ISet in v2 — iterate to grab the first
                    foreach (var t in tags)
                    {
                        if (t?.Name is { Length: > 0 } n) { tag = n; break; }
                    }
                }

                if (!services.TryGetValue(tag, out var list))
                {
                    list = [];
                    services[tag] = list;
                }
                list.Add(method);
            }
        }

        var apiTitle = doc.Info?.Title ?? "REST";
        var apiDescription = doc.Info?.Description;
        var apiVersion = doc.Info?.Version;

        var result = new List<BowireServiceInfo>();
        foreach (var (tag, methods) in services)
        {
            // Sort methods by their HTTP path so the UI shows them grouped logically
            methods.Sort(static (a, b) => string.CompareOrdinal(a.HttpPath, b.HttpPath));

            var svc = new BowireServiceInfo(
                Name: tag,
                Package: apiTitle,
                Methods: methods)
            {
                Source = "rest",
                Description = apiDescription,
                Version = apiVersion
            };
            result.Add(svc);
        }

        return result;
    }

    /// <summary>
    /// Returns the first server URL declared in the OpenAPI document, if any.
    /// Used to override the discovery URL with the actual API base when the
    /// OpenAPI doc is hosted somewhere different from where the API runs.
    /// </summary>
    public static string? GetFirstServerUrl(OpenApiDocument doc)
    {
        if (doc.Servers is null || doc.Servers.Count == 0) return null;
        var raw = doc.Servers[0].Url;
        if (string.IsNullOrEmpty(raw)) return null;

        // Server URLs may contain {variable} placeholders with defaults — substitute them
        var serverVars = doc.Servers[0].Variables;
        if (serverVars is not null && serverVars.Count > 0)
        {
            foreach (var (name, variable) in serverVars)
            {
                if (!string.IsNullOrEmpty(variable.Default))
                    raw = raw.Replace("{" + name + "}", variable.Default, StringComparison.Ordinal);
            }
        }
        return raw;
    }

    private static BowireMethodInfo? BuildMethod(string path, HttpMethod verb, OpenApiOperation operation)
    {
        var verbStr = verb.ToString().ToUpperInvariant();
        var methodName = !string.IsNullOrEmpty(operation.OperationId)
            ? operation.OperationId
            : SanitizeName(verbStr + "_" + path);

        // Synthesize an "input message" containing every parameter the user can fill in:
        // path params, query params, header params, and the body schema's properties.
        var fields = new List<BowireFieldInfo>();
        var fieldNumber = 1;

        if (operation.Parameters is not null)
        {
            foreach (var p in operation.Parameters)
            {
                if (string.IsNullOrEmpty(p.Name)) continue;
                var location = p.In switch
                {
                    ParameterLocation.Path => "path",
                    ParameterLocation.Query => "query",
                    ParameterLocation.Header => "header",
                    _ => null
                };
                if (location is null) continue; // skip cookie etc.

                fields.Add(SchemaToField(
                    name: p.Name,
                    schema: p.Schema,
                    source: location,
                    number: fieldNumber++,
                    required: p.Required,
                    description: p.Description,
                    parameterExample: p.Example));
            }
        }

        // Request body: prefer JSON, but fall back to multipart/form-data when
        // the operation only declares it (file-upload endpoints). Each top-
        // level property becomes a body field; binary fields land with
        // Source="formdata" + IsBinary so the UI renders a file picker.
        var bodyRequired = operation.RequestBody?.Required ?? false;
        var multipartSchema = ExtractMultipartSchema(operation.RequestBody);
        var bodySchema = ExtractJsonBodySchema(operation.RequestBody);
        // JSON wins when both are declared (typical for OpenAPI specs that
        // define request bodies in multiple representations) — Bowire's
        // happy path stays JSON, multipart kicks in for upload-only endpoints.
        if (bodySchema?.Properties is { Count: > 0 } props)
        {
            var requiredSet = bodySchema.Required ?? new HashSet<string>();
            foreach (var (propName, propSchema) in props)
            {
                fields.Add(SchemaToField(
                    name: propName,
                    schema: propSchema,
                    source: "body",
                    number: fieldNumber++,
                    required: bodyRequired && requiredSet.Contains(propName),
                    description: propSchema?.Description,
                    parameterExample: null));
            }
        }
        else if (multipartSchema?.Properties is { Count: > 0 } mpProps)
        {
            var requiredSet = multipartSchema.Required ?? new HashSet<string>();
            foreach (var (propName, propSchema) in mpProps)
            {
                var field = SchemaToField(
                    name: propName,
                    schema: propSchema,
                    source: "formdata",
                    number: fieldNumber++,
                    required: bodyRequired && requiredSet.Contains(propName),
                    description: propSchema?.Description,
                    parameterExample: null);
                if (IsBinaryFormat(propSchema))
                {
                    field = field with { IsBinary = true };
                }
                fields.Add(field);
            }
        }
        else if (bodySchema is not null)
        {
            // Free-form body — surface as a single "body" field of type object so the user
            // can edit the raw JSON in the JSON tab.
            fields.Add(new BowireFieldInfo(
                Name: "body",
                Number: fieldNumber++,
                Type: MapJsonType(bodySchema),
                Label: bodyRequired ? "required" : "optional",
                IsMap: false,
                IsRepeated: false,
                MessageType: null,
                EnumValues: null)
            {
                Source = "body",
                Required = bodyRequired,
                Description = bodySchema.Description
            });
        }

        var inputType = new BowireMessageInfo(
            Name: methodName + "Request",
            FullName: methodName + "Request",
            Fields: fields);

        // Output: pick the 2xx response schema if present
        var outputType = ExtractOutputType(operation);

        return new BowireMethodInfo(
            Name: methodName,
            FullName: verbStr + " " + path,
            ClientStreaming: false,
            ServerStreaming: false,
            InputType: inputType,
            OutputType: outputType,
            MethodType: "Unary")
        {
            HttpMethod = verbStr,
            HttpPath = path,
            Summary = operation.Summary,
            Description = operation.Description,
            Deprecated = operation.Deprecated
        };
    }

    private static IOpenApiSchema? ExtractJsonBodySchema(IOpenApiRequestBody? body)
    {
        if (body?.Content is null || body.Content.Count == 0) return null;

        // Prefer application/json; fall back to anything ending in +json.
        // Note (changed in the multipart slice): we no longer fall through
        // to "any media type" because that would shadow multipart/form-data
        // schemas — the new ExtractMultipartSchema picks those up explicitly.
        foreach (var (contentType, mediaType) in body.Content)
        {
            if (string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
                return mediaType.Schema;
        }
        foreach (var (contentType, mediaType) in body.Content)
        {
            if (contentType.Contains("+json", StringComparison.OrdinalIgnoreCase))
                return mediaType.Schema;
        }
        return null;
    }

    /// <summary>
    /// Extracts the schema declared under the <c>multipart/form-data</c>
    /// media type (or the legacy <c>application/x-www-form-urlencoded</c>
    /// shape, which has the same property-tree semantics). Returns null
    /// when the operation doesn't declare a form-style body.
    /// </summary>
    private static IOpenApiSchema? ExtractMultipartSchema(IOpenApiRequestBody? body)
    {
        if (body?.Content is null || body.Content.Count == 0) return null;
        foreach (var (contentType, mediaType) in body.Content)
        {
            if (string.Equals(contentType, "multipart/form-data", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(contentType, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            {
                return mediaType.Schema;
            }
        }
        return null;
    }

    /// <summary>
    /// True when an OpenAPI schema's <c>format</c> declares a binary payload
    /// (<c>format: binary</c> or <c>format: byte</c>). The discovery layer
    /// flags these fields as <see cref="BowireFieldInfo.IsBinary"/> so the
    /// UI renders a file picker.
    /// </summary>
    private static bool IsBinaryFormat(IOpenApiSchema? schema)
    {
        var fmt = schema?.Format;
        if (string.IsNullOrEmpty(fmt)) return false;
        return string.Equals(fmt, "binary", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fmt, "byte", StringComparison.OrdinalIgnoreCase);
    }

    private static BowireMessageInfo ExtractOutputType(OpenApiOperation operation)
    {
        if (operation.Responses is not null)
        {
            foreach (var (code, response) in operation.Responses)
            {
                if (!code.StartsWith('2')) continue;
                if (response.Content is null) continue;

                foreach (var (contentType, mediaType) in response.Content)
                {
                    if (mediaType.Schema is null) continue;
                    if (string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                        contentType.Contains("+json", StringComparison.OrdinalIgnoreCase))
                    {
                        return SchemaToMessage(operation.OperationId + "Response", mediaType.Schema);
                    }
                }
            }
        }

        // No structured response — return an empty placeholder so the schema tab still works
        return new BowireMessageInfo(
            Name: "Response",
            FullName: "Response",
            Fields: []);
    }

    private static BowireFieldInfo SchemaToField(
        string name,
        IOpenApiSchema? schema,
        string source,
        int number,
        bool required = false,
        string? description = null,
        JsonNode? parameterExample = null)
    {
        if (schema is null)
        {
            return new BowireFieldInfo(
                Name: name,
                Number: number,
                Type: "string",
                Label: required ? "required" : "optional",
                IsMap: false,
                IsRepeated: false,
                MessageType: null,
                EnumValues: null)
            {
                Source = source,
                Required = required,
                Description = description,
                Example = ExampleToString(parameterExample)
            };
        }

        var isArray = SchemaIsType(schema, JsonSchemaType.Array);
        var element = isArray ? schema.Items : schema;
        var typeName = MapJsonType(element);

        BowireMessageInfo? nested = null;
        if (element is not null && SchemaIsType(element, JsonSchemaType.Object) && element.Properties is { Count: > 0 })
        {
            nested = SchemaToMessage(name + "Type", element);
            typeName = "message";
        }

        List<BowireEnumValue>? enumValues = null;
        if (element?.Enum is { Count: > 0 } enums)
        {
            enumValues = [];
            var i = 0;
            foreach (var ev in enums)
            {
                var raw = ev?.ToJsonString().Trim('"') ?? string.Empty;
                enumValues.Add(new BowireEnumValue(raw, i++));
            }
        }

        // Examples can come from three places — prefer parameter-level, then
        // schema example, then schema default. All converted to a JSON string
        // so the JS can parse them when generating the default form values.
        var example = ExampleToString(parameterExample)
            ?? ExampleToString(schema.Example)
            ?? ExampleToString(schema.Default);

        var effectiveDescription = description ?? schema.Description;

        return new BowireFieldInfo(
            Name: name,
            Number: number,
            Type: typeName,
            Label: required ? "required" : "optional",
            IsMap: false,
            IsRepeated: isArray,
            MessageType: nested,
            EnumValues: enumValues)
        {
            Source = source,
            Required = required,
            Description = effectiveDescription,
            Example = example
        };
    }

    private static BowireMessageInfo SchemaToMessage(string name, IOpenApiSchema schema)
    {
        var fields = new List<BowireFieldInfo>();
        if (schema.Properties is not null)
        {
            var requiredSet = schema.Required ?? new HashSet<string>();
            var i = 1;
            foreach (var (propName, propSchema) in schema.Properties)
            {
                fields.Add(SchemaToField(
                    name: propName,
                    schema: propSchema,
                    source: "body",
                    number: i++,
                    required: requiredSet.Contains(propName),
                    description: propSchema?.Description,
                    parameterExample: null));
            }
        }
        return new BowireMessageInfo(
            Name: name,
            FullName: name,
            Fields: fields);
    }

    /// <summary>
    /// Convert an OpenAPI example/default value (a <see cref="JsonNode"/>) into
    /// a JSON string the JS layer can parse later. Returns null when the value
    /// is missing.
    /// </summary>
    private static string? ExampleToString(JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            var json = node.ToJsonString();
            return string.IsNullOrWhiteSpace(json) ? null : json;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// In OpenAPI v2 the schema's <c>Type</c> property is a flags enum so a single
    /// schema can declare multiple types (e.g. OpenAPI 3.1's <c>type: ["string", "null"]</c>).
    /// This helper checks whether a given type is set, ignoring nullability.
    /// </summary>
    private static bool SchemaIsType(IOpenApiSchema schema, JsonSchemaType target)
    {
        if (schema.Type is not { } t) return false;
        return (t & target) == target;
    }

    private static string MapJsonType(IOpenApiSchema? schema)
    {
        if (schema is null) return "string";

        // Strip Null from the flags so OpenAPI 3.1 nullables map to the same name
        // as their non-nullable counterparts.
        var t = schema.Type ?? JsonSchemaType.String;
        var nonNull = t & ~JsonSchemaType.Null;

        if ((nonNull & JsonSchemaType.Integer) != 0)
            return string.Equals(schema.Format, "int64", StringComparison.OrdinalIgnoreCase) ? "int64" : "int32";
        if ((nonNull & JsonSchemaType.Number) != 0)
            return string.Equals(schema.Format, "float", StringComparison.OrdinalIgnoreCase) ? "float" : "double";
        if ((nonNull & JsonSchemaType.String) != 0)
            return string.Equals(schema.Format, "byte", StringComparison.OrdinalIgnoreCase) ? "bytes" : "string";
        if ((nonNull & JsonSchemaType.Boolean) != 0) return "bool";
        if ((nonNull & JsonSchemaType.Object) != 0) return "message";
        if ((nonNull & JsonSchemaType.Array) != 0) return "string"; // caller usually unwraps Items first
        return "string";
    }

    private static string SanitizeName(string s)
    {
        var chars = new char[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            chars[i] = char.IsLetterOrDigit(c) ? c : '_';
        }
        return new string(chars);
    }
}

/// <summary>
/// Output of a successful discovery — keeps the parsed document around so the
/// REST plugin can re-walk it during invocation without re-fetching.
/// </summary>
internal sealed record DiscoveredApi(
    string ServerUrl,
    OpenApiDocument Document,
    OpenApiDiagnostic? Diagnostic);

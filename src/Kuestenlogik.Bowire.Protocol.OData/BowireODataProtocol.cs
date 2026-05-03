// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml;
using Kuestenlogik.Bowire.Models;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Csdl;

namespace Kuestenlogik.Bowire.Protocol.OData;

/// <summary>
/// Bowire protocol plugin for OData v4 services. Discovers entity sets
/// and operations via the $metadata endpoint, maps them to Bowire
/// services and methods.
///
/// Discovery: GET {baseUrl}/$metadata → parse EDMX → entity sets become
/// services, CRUD verbs (GET/POST/PATCH/DELETE) become methods, bound
/// functions/actions become additional methods.
///
/// Auto-discovered by <see cref="BowireProtocolRegistry"/>.
/// </summary>
public sealed class BowireODataProtocol : IBowireProtocol
{
    public string Name => "OData";
    public string Id => "odata";

    // OData has no official SVG; cylinder glyph ("queryable dataset") matches the site.
    public string IconSvg => """<svg viewBox="0 0 24 24" fill="none" stroke="#eab308" stroke-width="1.5" width="16" height="16" aria-hidden="true"><ellipse cx="12" cy="5" rx="9" ry="3"/><path d="M21 5v6c0 1.66-4.03 3-9 3s-9-1.34-9-3V5"/><path d="M21 11v6c0 1.66-4.03 3-9 3s-9-1.34-9-3v-6"/></svg>""";

    public void Initialize(IServiceProvider? serviceProvider) { }

    public async Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)) return [];
        var baseUrl = serverUrl.TrimEnd('/');
        if (!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return [];

        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var metadataUrl = baseUrl.EndsWith("$metadata", StringComparison.OrdinalIgnoreCase)
                ? baseUrl
                : baseUrl + "/$metadata";

            var resp = await http.GetAsync(new Uri(metadataUrl), ct);
            if (!resp.IsSuccessStatusCode) return [];

            var xml = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = XmlReader.Create(xml);

            if (!CsdlReader.TryParse(reader, out var model, out var errors))
                return [];

            var services = new List<BowireServiceInfo>();
            var container = model.EntityContainer;
            if (container is null) return [];

            // Each entity set → a service
            foreach (var entitySet in container.EntitySets())
            {
                var entityType = entitySet.EntityType;
                var methods = new List<BowireMethodInfo>();
                var inputFields = BuildFieldsFromType(entityType);
                var inputType = new BowireMessageInfo(entityType.Name, entityType.FullName(), inputFields);
                var outputType = new BowireMessageInfo(entityType.Name + "Response", entityType.FullName() + "Response", inputFields);

                // Standard CRUD methods
                methods.Add(new BowireMethodInfo(
                    "GET", $"odata/{entitySet.Name}/GET", false, false, BuildEmptyInput(), outputType, "Unary")
                {
                    HttpMethod = "GET", HttpPath = $"/{entitySet.Name}",
                    Summary = $"Query {entitySet.Name} entities",
                    Description = "Supports $filter, $select, $expand, $orderby, $top, $skip"
                });
                methods.Add(new BowireMethodInfo(
                    "GET_BY_KEY", $"odata/{entitySet.Name}/GET_BY_KEY", false, false, BuildKeyInput(entityType), outputType, "Unary")
                {
                    HttpMethod = "GET", HttpPath = $"/{entitySet.Name}({{key}})",
                    Summary = $"Get {entitySet.Name} by key"
                });
                methods.Add(new BowireMethodInfo(
                    "POST", $"odata/{entitySet.Name}/POST", false, false, inputType, outputType, "Unary")
                {
                    HttpMethod = "POST", HttpPath = $"/{entitySet.Name}",
                    Summary = $"Create a new {entitySet.Name} entity"
                });
                methods.Add(new BowireMethodInfo(
                    "PATCH", $"odata/{entitySet.Name}/PATCH", false, false, inputType, outputType, "Unary")
                {
                    HttpMethod = "PATCH", HttpPath = $"/{entitySet.Name}({{key}})",
                    Summary = $"Update a {entitySet.Name} entity"
                });
                methods.Add(new BowireMethodInfo(
                    "DELETE", $"odata/{entitySet.Name}/DELETE", false, false, BuildKeyInput(entityType), BuildEmptyInput(), "Unary")
                {
                    HttpMethod = "DELETE", HttpPath = $"/{entitySet.Name}({{key}})",
                    Summary = $"Delete a {entitySet.Name} entity"
                });

                // Strip /$metadata from the origin URL so invocations
                // route to the OData base (e.g. /odata/Products, not
                // /odata/$metadata/Products).
                var odataBase = baseUrl.EndsWith("/$metadata", StringComparison.OrdinalIgnoreCase)
                    ? baseUrl[..^10]
                    : baseUrl;
                var svc = new BowireServiceInfo(entitySet.Name, "odata", methods)
                {
                    Source = "odata",
                    OriginUrl = odataBase,
                    Description = $"OData entity set: {entitySet.Name} ({entityType.FullName()})"
                };
                services.Add(svc);
            }

            return services;
        }
        catch
        {
            return [];
        }
    }

    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        var baseUrl = serverUrl.TrimEnd('/');
        // Remove /$metadata suffix if present
        if (baseUrl.EndsWith("/$metadata", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^10];

        var payload = jsonMessages.FirstOrDefault() ?? "{}";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var http = new HttpClient();

        // Parse method to determine HTTP verb and path
        var parts = method.Split('/');
        var httpVerb = parts.Length >= 3 ? parts[2] : "GET";
        var entitySet = parts.Length >= 2 ? parts[1] : service;

        var requestUrl = new Uri($"{baseUrl}/{entitySet}");

        // Parse body for key-based operations
        string? key = null;
        try
        {
            var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("key", out var k))
                key = k.ToString();
            if (doc.RootElement.TryGetProperty("$filter", out var f))
                requestUrl = new Uri($"{baseUrl}/{entitySet}?$filter={Uri.EscapeDataString(f.GetString() ?? "")}");
            if (doc.RootElement.TryGetProperty("$select", out var s))
                requestUrl = new Uri(requestUrl + (requestUrl.Query.Length > 0 ? "&" : "?") + "$select=" + Uri.EscapeDataString(s.GetString() ?? ""));
        }
        catch { /* payload is just a body */ }

        if (key is not null && (httpVerb == "GET_BY_KEY" || httpVerb == "PATCH" || httpVerb == "DELETE"))
            requestUrl = new Uri($"{baseUrl}/{entitySet}({key})");
        if (httpVerb == "GET_BY_KEY") httpVerb = "GET";

        HttpResponseMessage resp;
        if (httpVerb == "POST" || httpVerb == "PATCH")
        {
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(new HttpMethod(httpVerb), requestUrl) { Content = content };
            resp = await http.SendAsync(req, ct);
        }
        else if (httpVerb == "DELETE")
        {
            resp = await http.DeleteAsync(requestUrl, ct);
        }
        else
        {
            resp = await http.GetAsync(requestUrl, ct);
        }

        var responseBody = await resp.Content.ReadAsStringAsync(ct);
        sw.Stop();

        return new InvokeResult(
            responseBody,
            sw.ElapsedMilliseconds,
            resp.IsSuccessStatusCode ? "OK" : $"HTTP {(int)resp.StatusCode}",
            new Dictionary<string, string>
            {
                ["httpStatus"] = ((int)resp.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["contentType"] = resp.Content.Headers.ContentType?.ToString() ?? ""
            });
    }

    public IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
        => AsyncEnumerable.Empty<string>();

    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
        => Task.FromResult<IBowireChannel?>(null);

    // ---- Schema helpers ----
    private static List<BowireFieldInfo> BuildFieldsFromType(IEdmStructuredType type)
    {
        var fields = new List<BowireFieldInfo>();
        var i = 1;
        foreach (var prop in type.Properties())
        {
            fields.Add(new BowireFieldInfo(
                prop.Name, i++,
                MapEdmType(prop.Type),
                "LABEL_OPTIONAL",
                false, false, null, null)
            {
                Description = $"Type: {prop.Type.FullName()}"
            });
        }
        return fields;
    }

    private static BowireMessageInfo BuildEmptyInput() => new("Empty", "odata.Empty", []);

    private static BowireMessageInfo BuildKeyInput(IEdmEntityType entityType)
    {
        var keyFields = new List<BowireFieldInfo>();
        var i = 1;
        foreach (var key in entityType.DeclaredKey)
        {
            keyFields.Add(new BowireFieldInfo(
                key.Name, i++,
                MapEdmType(key.Type),
                "LABEL_OPTIONAL",
                false, false, null, null)
            { Required = true, Description = "Entity key" });
        }
        return new BowireMessageInfo("KeyInput", "odata.KeyInput", keyFields);
    }

    private static string MapEdmType(IEdmTypeReference typeRef)
    {
        if (typeRef.IsString()) return "string";
        if (typeRef.IsInt32() || typeRef.IsInt64() || typeRef.IsInt16()) return "int64";
        if (typeRef.IsDouble() || typeRef.IsSingle() || typeRef.IsDecimal()) return "double";
        if (typeRef.IsBoolean()) return "bool";
        if (typeRef.IsDateTimeOffset() || typeRef.IsDate()) return "string";
        if (typeRef.IsGuid()) return "string";
        return "string";
    }
}

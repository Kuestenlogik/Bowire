// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.Interop.Formats;

/// <summary>
/// .http files (#674, the read direction of #673): requests become REST items
/// of one collection per file; <c>@var</c> lines and
/// <c>http-client(.private).env.json</c> become environments.
/// </summary>
internal sealed class HttpFileImporter : IInteropImporter
{
    private const string EnvFile = "http-client.env.json";
    private const string PrivateEnvFile = "http-client.private.env.json";

    public string Id => "http";

    public bool CanRead(IReadOnlyList<SourceFile> files) =>
        files.Any(f => HasHttpExtension(f.Path) || (!IsEnvFile(f.Path) && HttpFile.LooksLikeHttp(f.Content)));

    public ImportCollectionResponse Read(IReadOnlyList<SourceFile> files, string? name, ReportBuilder report)
    {
        var response = new ImportCollectionResponse { Format = Id };
        var httpFiles = files.Where(f => !IsEnvFile(f.Path)).ToList();
        foreach (var file in httpFiles)
        {
            var doc = HttpFile.Parse(file.Content);
            if (doc.Requests.Count == 0)
            {
                report.Skipped(DisplayName(file.Path), "no request line found");
                continue;
            }
            var collectionName = httpFiles.Count == 1 && name is not null ? name : Stem(file.Path) ?? name ?? "Imported .http";
            var collection = new Collection { Name = collectionName };
            foreach (var request in doc.Requests) ReadRequest(request, collection, report);
            response.Collections.Add(collection);

            if (doc.Variables.Count > 0)
            {
                var env = new V1.Environment { Name = collectionName + " (file variables)" };
                var unmapped = new List<string>();
                foreach (var (key, value) in doc.Variables) env.Vars[key] = HttpVariables.ToBowire(value, unmapped);
                response.Environments.Add(env);
                report.Approximated(env.Name, "@variables of the file became an environment: Bowire collections hold no variables of their own");
            }
        }
        if (response.Collections.Count == 0) throw new InteropFormatException("no request found in the .http file(s)");

        ReadEnvironments(files, response, report);
        return response;
    }

    private static void ReadRequest(HttpFileRequest request, Collection collection, ReportBuilder report)
    {
        var subject = request.Title ?? request.Name ?? $"{request.Method} {request.Url}";
        var unmapped = new List<string>();
        var url = HttpVariables.ToBowire(request.Url, unmapped);
        var item = new CollectionItem
        {
            Name = subject,
            Protocol = "rest",
            Service = url,
            Method = request.Method,
            MethodType = "Unary",
            ServerUrl = url,
        };

        switch (request.Method)
        {
            case "WEBSOCKET":
            case "GRPC":
                report.Skipped(subject, $"{request.Method} requests (line {request.Line}) are not imported yet; Bowire discovers WebSocket and gRPC services from the server instead");
                return;
            case "GRAPHQL":
                // JetBrains sends a GRAPHQL body as a POST with {"query": …}.
                item.Method = "POST";
                item.Messages.Add(JsonSerializer.Serialize(new { query = request.Body }));
                item.Metadata["Content-Type"] = "application/json";
                report.Approximated(subject, "GRAPHQL request became a REST POST with a {\"query\": …} body; for schema-aware calls, add the endpoint as a GraphQL source");
                break;
            default:
                item.Messages.Add(request.Body.Length > 0 ? HttpVariables.ToBowire(request.Body, unmapped) : "{}");
                break;
        }

        foreach (var (key, value) in request.Headers)
        {
            var headerValue = HttpVariables.ToBowire(value, unmapped);
            // JetBrains shorthand: `Authorization: Basic user password`.
            if (key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                && TryExpandBasic(headerValue, out var expanded, out var hasReference))
            {
                if (hasReference)
                {
                    report.Approximated(subject, "`Authorization: Basic user password` with variables cannot be encoded ahead of time; set Basic auth on the environment instead");
                }
                else
                {
                    headerValue = expanded;
                }
            }
            item.Metadata[key] = headerValue;
        }
        report.CheckHeaders(subject, item.Metadata);

        foreach (var line in request.Unsupported)
        {
            report.Skipped(subject, line.StartsWith('<') && !line.StartsWith("<>", StringComparison.Ordinal)
                ? $"body from a file ({line}): the file is not part of the import"
                : $"response handler or output redirect ({Shorten(line)}): Bowire runs post-scripts and assertions in its own format");
        }
        foreach (var directive in request.Directives) report.Skipped(subject, $"directive @{directive}");
        foreach (var name in unmapped.Distinct(StringComparer.Ordinal))
            report.Approximated(subject, $"{{{{{name}}}}} has no Bowire equivalent and stays as written");

        if (request.Method != "GRAPHQL") report.Mapped(subject);
        collection.Items.Add(item);
    }

    private static void ReadEnvironments(IReadOnlyList<SourceFile> files, ImportCollectionResponse response, ReportBuilder report)
    {
        var shared = new Dictionary<string, string>(StringComparer.Ordinal);
        var envs = new Dictionary<string, V1.Environment>(StringComparer.Ordinal);
        foreach (var file in files.Where(f => IsEnvFile(f.Path)).OrderBy(f => IsPrivate(f.Path)))
        {
            var isPrivate = IsPrivate(file.Path);
            JsonDocument json;
            try
            {
                json = JsonDocument.Parse(file.Content);
            }
            catch (JsonException ex)
            {
                throw new InteropFormatException($"{DisplayName(file.Path)} is not valid JSON: {ex.Message}", ex);
            }
            using (json)
            {
                foreach (var env in json.RootElement.EnumerateObject())
                {
                    if (env.Value.ValueKind != JsonValueKind.Object) continue;
                    IDictionary<string, string> target;
                    if (env.Name == "$shared") target = shared;
                    else if (envs.TryGetValue(env.Name, out var existing)) target = existing.Vars;
                    else target = (envs[env.Name] = new V1.Environment { Name = env.Name }).Vars;
                    foreach (var variable in env.Value.EnumerateObject())
                    {
                        if (variable.Value.ValueKind == JsonValueKind.Object)
                        {
                            report.Skipped($"{env.Name}.{variable.Name}", "structured value (e.g. an SSL or auth configuration block)");
                            continue;
                        }
                        target[variable.Name] = variable.Value.ValueKind == JsonValueKind.String ? variable.Value.GetString()! : variable.Value.GetRawText();
                        if (isPrivate) report.Credential(env.Name, $"private variable {variable.Name}");
                    }
                }
            }
        }
        foreach (var env in envs.Values)
        {
            foreach (var (key, value) in shared) env.Vars.TryAdd(key, value);
            response.Environments.Add(env);
            report.Mapped(env.Name, "environment");
        }
        if (envs.Count == 0 && shared.Count > 0)
        {
            var env = new V1.Environment { Name = "shared" };
            env.Vars.Add(shared);
            response.Environments.Add(env);
            report.Approximated("$shared", "only shared variables, imported as an environment named 'shared'");
        }
    }

    private static bool TryExpandBasic(string value, out string expanded, out bool hasReference)
    {
        expanded = value;
        hasReference = false;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !parts[0].Equals("Basic", StringComparison.OrdinalIgnoreCase)) return false;
        hasReference = value.Contains("{{", StringComparison.Ordinal);
        expanded = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(parts[1] + ":" + parts[2]));
        return true;
    }

    private static bool HasHttpExtension(string path) =>
        path.EndsWith(".http", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".rest", StringComparison.OrdinalIgnoreCase);

    private static bool IsEnvFile(string path) =>
        DisplayName(path).Equals(EnvFile, StringComparison.OrdinalIgnoreCase) || IsPrivate(path);

    private static bool IsPrivate(string path) => DisplayName(path).Equals(PrivateEnvFile, StringComparison.OrdinalIgnoreCase);

    private static string DisplayName(string path) => Path.GetFileName(path.Replace('\\', '/'));

    private static string? Stem(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(DisplayName(path));
        return string.IsNullOrWhiteSpace(stem) ? null : stem;
    }

    private static string Shorten(string line) => line.Length <= 40 ? line : line[..37] + "...";
}

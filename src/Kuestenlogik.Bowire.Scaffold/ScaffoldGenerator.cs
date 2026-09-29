// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Scaffold;

/// <summary>One generated file.</summary>
/// <param name="Path">Relative to the scaffold's folder, forward slashes.</param>
/// <param name="Kind"><c>spec</c>, <c>schema</c>, <c>stub</c>, <c>collection</c>, <c>test</c> or <c>readme</c>.</param>
/// <param name="Language">For the editor: <c>yaml</c>, <c>protobuf</c>, <c>csharp</c>, <c>xml</c>, <c>json</c>, <c>markdown</c>.</param>
/// <param name="Content">The file.</param>
public sealed record ScaffoldFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("content")] string Content);

/// <summary>
/// Turns a <see cref="ScaffoldSpec"/> into files (#177). A pure function of
/// the spec and the templates embedded in this assembly: the same spec gives
/// the same bytes on every machine, whichever model wrote it.
/// </summary>
public static class ScaffoldGenerator
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Generate every file for a spec; throws on a spec that does not normalize.</summary>
    public static IReadOnlyList<ScaffoldFile> Generate(ScaffoldSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var (normalized, errors) = spec.Normalize();
        if (normalized is null) throw new ArgumentException(string.Join("; ", errors), nameof(spec));
        spec = normalized;

        var model = BuildModel(spec);
        var files = new List<ScaffoldFile>
        {
            new("bowire-scaffold.json", "spec", "json", JsonSerializer.Serialize(spec, s_json) + "\n"),
        };
        var service = spec.Service!;
        if (spec.Protocol == "rest")
        {
            files.Add(new("openapi.yaml", "schema", "yaml", Render("rest/openapi.yaml.tmpl", model)));
            files.Add(new("Program.cs", "stub", "csharp", Render("rest/program-cs.tmpl", model)));
            files.Add(new(service + ".csproj", "stub", "xml", Render("rest/project.csproj.tmpl", model)));
        }
        else
        {
            var proto = (string)model["protoFile"]!;
            files.Add(new(proto, "schema", "protobuf", Render("grpc/service.proto.tmpl", model)));
            files.Add(new(service + "Service.cs", "stub", "csharp", Render("grpc/service-cs.tmpl", model)));
            files.Add(new("Program.cs", "stub", "csharp", Render("grpc/program-cs.tmpl", model)));
            files.Add(new(service + ".csproj", "stub", "xml", Render("grpc/project.csproj.tmpl", model)));
        }
        files.Add(new("bowire/" + (string)model["collectionFile"]!, "collection", "json", Collection(spec, model)));
        files.Add(new("bowire/" + (string)model["testFile"]!, "test", "json", SmokeTest(spec, model)));
        files.Add(new("README.md", "readme", "markdown", Render("README.md.tmpl", model)));
        return files;
    }

    private static Dictionary<string, object?> BuildModel(ScaffoldSpec spec)
    {
        var entity = spec.Entity;
        var plural = ScaffoldNames.Plural(entity);
        var service = spec.Service!;
        var port = new Uri(spec.BaseUrl!).Port;
        var grpc = spec.Protocol == "grpc";
        var fields = spec.Fields.Select((f, i) => (IReadOnlyDictionary<string, object?>)FieldModel(f, i, spec.Fields.Count)).ToList();
        var route = ScaffoldNames.Lower(plural);
        var protoFile = ScaffoldNames.Snake(service) + ".proto";
        var package = ScaffoldNames.Snake(service) + ".v1";
        var pluralSnake = ScaffoldNames.Snake(ScaffoldSpec.Camel(plural));
        var entitySnake = ScaffoldNames.Snake(ScaffoldSpec.Camel(entity));
        var stubFiles = grpc
            ? new List<IReadOnlyDictionary<string, object?>>
            {
                Row(service + "Service.cs", "The service, keeping " + ScaffoldSpec.Camel(plural) + " in memory"),
                Row("Program.cs", "Host with gRPC and server reflection"),
                Row(service + ".csproj", "The project"),
            }
            : [Row("Program.cs", "Minimal API with every route, keeping " + ScaffoldSpec.Camel(plural) + " in memory"), Row(service + ".csproj", "The project")];

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Entity"] = entity,
            ["entity"] = ScaffoldSpec.Camel(entity),
            ["entityWords"] = Words(entity),
            ["aEntity"] = WithArticle(Words(entity)),
            ["Plural"] = plural,
            ["plural"] = Words(plural),
            ["route"] = route,
            ["routeVar"] = ScaffoldSpec.Camel(plural),
            ["Service"] = service,
            ["Namespace"] = service + "Api",
            ["package"] = package,
            ["protoFile"] = protoFile,
            ["pluralSnake"] = pluralSnake,
            ["entitySnake"] = entitySnake,
            ["PluralPascalField"] = ScaffoldSpec.Pascal(pluralSnake),
            ["EntityPascalField"] = ScaffoldSpec.Pascal(entitySnake),
            ["BaseUrl"] = spec.BaseUrl,
            ["Port"] = port,
            ["HasRequired"] = spec.Fields.Any(f => f.Required),
            ["fields"] = fields,
            ["protocolName"] = grpc ? "gRPC" : "REST",
            ["schemaFile"] = grpc ? protoFile : "openapi.yaml",
            ["stubFiles"] = stubFiles,
            ["collectionFile"] = route + ".collection.json",
            ["testFile"] = route + ".smoke-test.json",
            ["discoveryUrl"] = DiscoveryUrl(spec),
            ["idVariable"] = IdVariable(spec),
        };
    }

    private static Dictionary<string, object?> FieldModel(ScaffoldField f, int index, int count)
    {
        var (oasType, oasFormat) = f.Type switch
        {
            "int" => ("integer", "int32"),
            "long" => ("integer", "int64"),
            "double" => ("number", "double"),
            "bool" => ("boolean", ""),
            "datetime" => ("string", "date-time"),
            "uuid" => ("string", "uuid"),
            _ => ("string", ""),
        };
        var csType = f.Type switch
        {
            "int" => "int",
            "long" => "long",
            "double" => "double",
            "bool" => "bool",
            "datetime" => "DateTimeOffset",
            "uuid" => "Guid",
            _ => "string",
        };
        var protoType = f.Type switch
        {
            "int" => "int32",
            "long" => "int64",
            "double" => "double",
            "bool" => "bool",
            _ => "string",
        };
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = f.Name,
            ["Pascal"] = ScaffoldSpec.Pascal(f.Name),
            ["snake"] = ScaffoldNames.Snake(f.Name),
            ["Required"] = f.Required,
            ["oasType"] = oasType,
            ["oasFormat"] = oasFormat,
            ["csNullable"] = csType + "?",
            ["protoType"] = protoType,
            // proto3 scalars other than string carry no presence unless
            // optional; without it a required int could not be told from 0.
            ["ProtoOptional"] = protoType != "string",
            ["protoNote"] = f.Type switch { "datetime" => "ISO 8601", "uuid" => "UUID", _ => "" },
            ["Number"] = index + 2,
            ["InputNumber"] = index + 1,
            ["First"] = index == 0,
            ["Last"] = index == count - 1,
        };
    }

    private static Dictionary<string, object?> Row(string path, string what) =>
        new(StringComparer.Ordinal) { ["path"] = path, ["what"] = what };

    // "an order", "an update", but "a user", "a unit": a vowel takes "an",
    // except a "u" that sounds like "you" — u, consonant, vowel. Right for
    // the names people give entities far more often than not.
    internal static string WithArticle(string words)
    {
        const string vowels = "aeiou";
        var w = words;
        var an = vowels.Contains(w[0], StringComparison.Ordinal)
            && !(w[0] == 'u' && w.Length > 2 && !vowels.Contains(w[1], StringComparison.Ordinal) && vowels.Contains(w[2], StringComparison.Ordinal));
        return (an ? "an " : "a ") + words;
    }

    private static string Words(string pascal) => ScaffoldNames.Snake(ScaffoldSpec.Camel(pascal)).Replace('_', ' ');

    private static string DiscoveryUrl(ScaffoldSpec spec) =>
        spec.Protocol == "rest" ? spec.BaseUrl + "/openapi.yaml" : spec.BaseUrl!;

    private static string IdVariable(ScaffoldSpec spec) => ScaffoldSpec.Camel(spec.Entity) + "Id";

    /// <summary>A value that satisfies the field's type, chosen from its name where one reads naturally.</summary>
    internal static JsonNode Sample(ScaffoldField f) => f.Type switch
    {
        "int" or "long" => JsonValue.Create(42),
        "double" => JsonValue.Create(1.5),
        "bool" => JsonValue.Create(true),
        "datetime" => JsonValue.Create("2026-01-01T00:00:00Z"),
        "uuid" => JsonValue.Create("00000000-0000-0000-0000-000000000001"),
        _ => JsonValue.Create(ScaffoldNames.Lower(f.Name) switch
        {
            var n when n.Contains("email", StringComparison.Ordinal) => "ada@example.com",
            var n when n.Contains("url", StringComparison.Ordinal) => "https://example.com",
            var n when n.Contains("phone", StringComparison.Ordinal) => "+49 40 123456",
            "name" or "fullname" or "displayname" => "Ada Lovelace",
            "firstname" => "Ada",
            "lastname" => "Lovelace",
            "title" => "First " + f.Name,
            "role" => "admin",
            "status" or "state" => "active",
            _ => "sample " + ScaffoldNames.Snake(f.Name).Replace('_', ' '),
        }),
    };

    private static JsonObject SampleInput(ScaffoldSpec spec, bool requiredOnly = false)
    {
        var o = new JsonObject();
        foreach (var f in spec.Fields.Where(f => !requiredOnly || f.Required)) o[f.Name] = Sample(f);
        return o;
    }

    // Messages as the workbench sends them: REST flattens path parameters
    // and body fields into one object; gRPC nests the input of an update.
    private static string Message(JsonObject o) => o.ToJsonString(s_json);

    /// <summary>
    /// The workbench collection: every operation with a sample, plus the
    /// error cases — an unknown id, and a create missing its required fields.
    /// Requests that need an id read it from <c>{{&lt;entity&gt;Id}}</c>.
    /// </summary>
    private static string Collection(ScaffoldSpec spec, Dictionary<string, object?> model)
    {
        var grpc = spec.Protocol == "grpc";
        var entity = spec.Entity;
        var plural = (string)model["Plural"]!;
        var serviceName = grpc ? model["package"] + "." + spec.Service : (string)model["route"]!;
        var serverUrl = DiscoveryUrl(spec);
        var idRef = "{{" + IdVariable(spec) + "}}";
        var route = (string)model["route"]!;

        JsonObject WithId(string id, JsonObject? input)
        {
            var o = new JsonObject { ["id"] = id };
            if (input is null) return o;
            if (grpc) o[(string)model["entitySnake"]!] = input;
            else foreach (var (k, v) in input) o[k] = v?.DeepClone();
            return o;
        }

        var items = new List<(string Suffix, string Method, JsonObject Body)>
        {
            ("list", (grpc ? "List" : "list") + plural, new JsonObject()),
            ("create", (grpc ? "Create" : "create") + entity, SampleInput(spec)),
            ("get", (grpc ? "Get" : "get") + entity, WithId(idRef, null)),
            ("update", (grpc ? "Update" : "update") + entity, WithId(idRef, SampleInput(spec))),
            ("delete", (grpc ? "Delete" : "delete") + entity, WithId(idRef, null)),
            ("get-missing", (grpc ? "Get" : "get") + entity, WithId("does-not-exist", null)),
        };
        if (spec.Fields.Any(f => f.Required))
        {
            var partial = SampleInput(spec);
            foreach (var f in spec.Fields.Where(f => f.Required)) partial.Remove(f.Name);
            items.Add(("create-missing-required", (grpc ? "Create" : "create") + entity, partial));
        }

        var collection = new JsonObject
        {
            ["id"] = "col_scaffold_" + route,
            ["name"] = spec.Service + " (scaffolded)",
            ["createdAt"] = 0,
            ["items"] = new JsonArray([.. items.Select(item =>
            {
                var body = Message(item.Body);
                return (JsonNode)new JsonObject
                {
                    ["id"] = "ci_scaffold_" + route + "_" + item.Suffix,
                    ["protocol"] = spec.Protocol,
                    ["service"] = serviceName,
                    ["method"] = item.Method,
                    ["methodType"] = "Unary",
                    ["body"] = body,
                    ["messages"] = new JsonArray(body),
                    ["metadata"] = null,
                    ["serverUrl"] = serverUrl,
                };
            })]),
        };
        return collection.ToJsonString(s_json) + "\n";
    }

    /// <summary>
    /// The smoke test for <c>bowire test</c>: the list answers, a create
    /// comes back with an id and the values sent, an unknown id is not
    /// found, and a create without its required fields is refused. Status
    /// names are Bowire's, not HTTP codes — 2xx is <c>OK</c>.
    /// </summary>
    private static string SmokeTest(ScaffoldSpec spec, Dictionary<string, object?> model)
    {
        var grpc = spec.Protocol == "grpc";
        var entity = spec.Entity;
        var plural = (string)model["Plural"]!;
        var serviceName = grpc ? model["package"] + "." + spec.Service : (string)model["route"]!;
        string M(string verb, string noun) => (grpc ? verb : char.ToLowerInvariant(verb[0]) + verb[1..]) + noun;

        var createAsserts = new JsonArray
        {
            Assert("status", "eq", "OK"),
            Assert("response.id", "exists", null),
        };
        foreach (var f in spec.Fields.Where(f => f.Type is "string" or "int" or "long" or "bool").Take(3))
        {
            createAsserts.Add(Assert("response." + f.Name, "eq", Sample(f).ToJsonString().Trim('"')));
        }

        var tests = new JsonArray
        {
            Test($"list {Words(plural)}", M("List", plural), new JsonObject(), [Assert("status", "eq", "OK")]),
            Test($"create {WithArticle(Words(entity))}", M("Create", entity), SampleInput(spec), createAsserts),
            Test($"an unknown id is not found", M("Get", entity), new JsonObject { ["id"] = "does-not-exist" },
                [Assert("status", "eq", "NotFound")]),
        };
        if (spec.Fields.Any(f => f.Required))
        {
            var partial = SampleInput(spec);
            foreach (var f in spec.Fields.Where(f => f.Required)) partial.Remove(f.Name);
            tests.Add(Test($"{WithArticle(Words(entity))} without its required fields is refused", M("Create", entity), partial,
                [Assert("status", "eq", "InvalidArgument")]));
        }

        var doc = new JsonObject
        {
            ["name"] = spec.Service + " smoke test",
            ["serverUrl"] = DiscoveryUrl(spec),
            ["protocol"] = spec.Protocol,
            ["tests"] = tests,
        };
        return doc.ToJsonString(s_json) + "\n";

        JsonNode Test(string name, string method, JsonObject message, JsonArray asserts) => new JsonObject
        {
            ["name"] = name,
            ["service"] = serviceName,
            ["method"] = method,
            ["messages"] = new JsonArray(Message(message)),
            ["assert"] = asserts,
        };
    }

    private static JsonObject Assert(string path, string op, string? expected)
    {
        var o = new JsonObject { ["path"] = path, ["op"] = op };
        if (expected is not null) o["expected"] = expected;
        return o;
    }

    private static string Render(string template, Dictionary<string, object?> model) =>
        ScaffoldTemplate.Render(LoadTemplate(template), model);

    internal static string LoadTemplate(string name)
    {
        var resource = "Kuestenlogik.Bowire.Scaffold.Templates." + name.Replace('/', '.');
        using var stream = typeof(ScaffoldGenerator).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"scaffold template '{name}' is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}

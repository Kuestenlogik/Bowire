// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.Rest.Tests;

/// <summary>
/// Unit coverage for <see cref="OpenApiDocumentBuilder"/>. Pins:
/// <list type="bullet">
///   <item>Top-level doc shape (openapi version, info, servers).</item>
///   <item>Path / verb mapping, multiple verbs under one path.</item>
///   <item>Parameters: explicit field sources + path-template backfill.</item>
///   <item>Body emission heuristic (POST/PUT/PATCH get a body).</item>
///   <item>Response synthesis from output-type fields.</item>
///   <item>Coverage annotation from BowireRecording.Steps.</item>
///   <item>Pure helpers (MapFieldType, ExtractPathPlaceholders,
///     VerbCarriesBody, CoverageKey).</item>
/// </list>
/// </summary>
public sealed class OpenApiDocumentBuilderTests
{
    // ---- pure helpers ---------------------------------------------

    [Theory]
    [InlineData("/users/{id}", new[] { "id" })]
    [InlineData("/orgs/{orgId}/repos/{repoId}", new[] { "orgId", "repoId" })]
    [InlineData("/no/placeholders", new string[0])]
    [InlineData("", new string[0])]
    [InlineData("/with/{star*}/suffix", new[] { "star" })] // 3.1-style explode marker stripped
    public void ExtractPathPlaceholders_finds_named_segments(string path, string[] expected)
    {
        var actual = OpenApiDocumentBuilder.ExtractPathPlaceholders(path);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("POST", true)]
    [InlineData("PUT", true)]
    [InlineData("PATCH", true)]
    [InlineData("GET", false)]
    [InlineData("DELETE", false)]
    [InlineData("HEAD", false)]
    [InlineData("OPTIONS", false)]
    public void VerbCarriesBody_matches_openapi_convention(string verb, bool expected)
        => Assert.Equal(expected, OpenApiDocumentBuilder.VerbCarriesBody(verb));

    [Theory]
    [InlineData("string", "string")]
    [InlineData("int32", "integer")]
    [InlineData("bool", "boolean")]
    [InlineData("double", "number")]
    [InlineData("bytes", "string")]
    [InlineData("anything-else", "string")]
    public void MapFieldType_picks_json_schema_type(string protoType, string expected)
    {
        var schema = OpenApiDocumentBuilder.MapFieldType(protoType);
        Assert.Equal(expected, schema["type"]);
    }

    [Fact]
    public void MapFieldType_bytes_carries_byte_format()
    {
        var schema = OpenApiDocumentBuilder.MapFieldType("bytes");
        // OpenAPI 3 uses `format: byte` for base64-encoded bytes
        Assert.Equal("byte", schema["format"]);
    }

    // ---- end-to-end document shape --------------------------------

    [Fact]
    public void Build_emits_openapi_3_with_info_and_server()
    {
        var svc = MakeService("Users", "1.2.3",
            ("getUser", "GET", "/users/{id}"));
        var yaml = OpenApiDocumentBuilder.Build("http://api.example.com", new[] { svc });

        Assert.Contains("openapi: 3.0.0", yaml);
        Assert.Contains("url: http://api.example.com", yaml);
        Assert.Contains("version: 1.2.3", yaml);
        Assert.Contains("/users/{id}:", yaml);
        Assert.Contains("get:", yaml);
        Assert.Contains("operationId: getUser", yaml);
    }

    [Fact]
    public void Build_groups_multiple_verbs_under_one_path()
    {
        var svc = MakeService("Users", "1.0.0",
            ("getUser", "GET", "/users/{id}"),
            ("updateUser", "PUT", "/users/{id}"),
            ("deleteUser", "DELETE", "/users/{id}"));
        var yaml = OpenApiDocumentBuilder.Build("http://h", new[] { svc });

        // One path, three verbs — assert all three keys appear once.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(yaml, "/users/\\{id\\}:"));
        Assert.Contains("get:", yaml);
        Assert.Contains("put:", yaml);
        Assert.Contains("delete:", yaml);
    }

    [Fact]
    public void Build_backfills_path_parameters_from_placeholders()
    {
        // Method has no Source=path fields — placeholder should still
        // produce a parameters[] entry with in:path required:true.
        var svc = MakeService("Users", "1.0.0",
            ("getUser", "GET", "/users/{id}"));
        var yaml = OpenApiDocumentBuilder.Build("http://h", new[] { svc });

        Assert.Contains("name: id", yaml);
        Assert.Contains("in: path", yaml);
        Assert.Contains("required: true", yaml);
    }

    [Fact]
    public void Build_emits_explicit_query_and_header_parameters()
    {
        var input = new BowireMessageInfo("Q", "Q", new List<BowireFieldInfo>
        {
            new BowireFieldInfo("page", 1, "int32", "LABEL_OPTIONAL", false, false, null, null) { Source = "query" },
            new BowireFieldInfo("authorization", 2, "string", "LABEL_REQUIRED", false, false, null, null) { Source = "header", Required = true },
        });
        var method = new BowireMethodInfo(
            Name: "list", FullName: "GET /things",
            ClientStreaming: false, ServerStreaming: false,
            InputType: input, OutputType: EmptyMessage(),
            MethodType: "Unary") { HttpMethod = "GET", HttpPath = "/things" };
        var svc = new BowireServiceInfo("Things", "rest", new List<BowireMethodInfo> { method });

        var yaml = OpenApiDocumentBuilder.Build("http://h", new[] { svc });

        Assert.Contains("name: page", yaml);
        Assert.Contains("in: query", yaml);
        Assert.Contains("name: authorization", yaml);
        Assert.Contains("in: header", yaml);
    }

    [Fact]
    public void Build_emits_request_body_for_POST_with_untagged_fields()
    {
        var input = new BowireMessageInfo("CreateUser", "CreateUser", new List<BowireFieldInfo>
        {
            new BowireFieldInfo("name", 1, "string", "LABEL_REQUIRED", false, false, null, null) { Required = true },
            new BowireFieldInfo("email", 2, "string", "LABEL_OPTIONAL", false, false, null, null),
        });
        var output = new BowireMessageInfo("User", "User",
            [new BowireFieldInfo("id", 1, "string", "LABEL_OPTIONAL", false, false, null, null)]);
        var method = new BowireMethodInfo(
            Name: "createUser", FullName: "POST /users",
            ClientStreaming: false, ServerStreaming: false,
            InputType: input, OutputType: output, MethodType: "Unary")
            { HttpMethod = "POST", HttpPath = "/users" };
        var svc = new BowireServiceInfo("Users", "rest", new List<BowireMethodInfo> { method });

        var yaml = OpenApiDocumentBuilder.Build("http://h", new[] { svc });

        Assert.Contains("requestBody:", yaml);
        Assert.Contains("application/json:", yaml);
        // Required marker propagated:
        Assert.Contains("- name", yaml);
    }

    [Fact]
    public void Build_does_not_emit_request_body_for_GET()
    {
        var svc = MakeService("Things", "1.0.0", ("list", "GET", "/things"));
        var yaml = OpenApiDocumentBuilder.Build("http://h", new[] { svc });

        Assert.DoesNotContain("requestBody:", yaml);
    }

    [Fact]
    public void Build_emits_200_response_with_output_schema()
    {
        var output = new BowireMessageInfo("Thing", "Thing", new List<BowireFieldInfo>
        {
            new BowireFieldInfo("id", 1, "string", "LABEL_REQUIRED", false, false, null, null) { Required = true },
            new BowireFieldInfo("count", 2, "int32", "LABEL_OPTIONAL", false, false, null, null),
        });
        var method = new BowireMethodInfo(
            Name: "getThing", FullName: "GET /things/{id}",
            ClientStreaming: false, ServerStreaming: false,
            InputType: EmptyMessage(), OutputType: output, MethodType: "Unary")
            { HttpMethod = "GET", HttpPath = "/things/{id}" };
        var svc = new BowireServiceInfo("Things", "rest", new List<BowireMethodInfo> { method });

        var yaml = OpenApiDocumentBuilder.Build("http://h", new[] { svc });

        // YamlDotNet's NullNamingConvention emits the string key
        // unquoted; some serialisers would quote it because '200'
        // parses as int, but ours uses string keys throughout.
        Assert.Matches(@"(^|\s)200:", yaml);
        Assert.Contains("type: object", yaml);
        Assert.Contains("type: integer", yaml);   // count
    }

    // ---- coverage --------------------------------------------------

    [Fact]
    public void Build_with_recording_emits_x_bowire_coverage_for_recorded_ops()
    {
        var svc = MakeService("Users", "1.0.0",
            ("getUser",    "GET",  "/users/{id}"),
            ("createUser", "POST", "/users"));

        var recording = new BowireRecording
        {
            Id = "rec-1",
            Name = "User flows",
            Steps =
            {
                new BowireRecordingStep { HttpVerb = "GET",  HttpPath = "/users/{id}" },
                new BowireRecordingStep { HttpVerb = "GET",  HttpPath = "/users/{id}" }, // 2 hits same op
                // No POST recorded — that op must be flagged as not recorded.
            }
        };

        var yaml = OpenApiDocumentBuilder.Build("http://h", new[] { svc }, recording);

        Assert.Contains("x-bowire-coverage:", yaml);
        // The GET op: recorded with 2 steps
        Assert.Contains("recorded: true", yaml);
        Assert.Contains("stepCount: 2", yaml);
        // The POST op: explicitly recorded:false because the recording
        // exists but didn't capture this verb/path.
        Assert.Contains("recorded: false", yaml);
    }

    [Fact]
    public void Build_without_recording_omits_coverage()
    {
        var svc = MakeService("Things", "1.0.0", ("list", "GET", "/things"));
        var yaml = OpenApiDocumentBuilder.Build("http://h", new[] { svc });

        Assert.DoesNotContain("x-bowire-coverage", yaml);
    }

    [Fact]
    public void CoverageKey_normalises_verb_case()
    {
        Assert.Equal(
            OpenApiDocumentBuilder.CoverageKey("get", "/users"),
            OpenApiDocumentBuilder.CoverageKey("GET", "/users"));
    }

    // ---- options + format ------------------------------------------

    [Fact]
    public void Build_options_override_title_and_version()
    {
        var svc = MakeService("S", "1.0.0", ("op", "GET", "/x"));
        var yaml = OpenApiDocumentBuilder.Build("http://h", new[] { svc },
            options: new OpenApiExportOptions { Title = "My API", Version = "9.9.9" });
        Assert.Contains("title: My API", yaml);
        Assert.Contains("version: 9.9.9", yaml);
    }

    [Fact]
    public void Build_json_format_emits_parseable_json()
    {
        var svc = MakeService("S", "1.0.0", ("op", "GET", "/x"));
        var json = OpenApiDocumentBuilder.Build("http://h", new[] { svc },
            options: new OpenApiExportOptions { Format = OpenApiExportFormat.Json });

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("3.0.0", doc.RootElement.GetProperty("openapi").GetString());
        Assert.True(doc.RootElement.TryGetProperty("paths", out _));
    }

    [Fact]
    public void Build_rejects_empty_serverUrl()
        => Assert.Throws<ArgumentException>(() =>
            OpenApiDocumentBuilder.Build("", Array.Empty<BowireServiceInfo>()));

    [Fact]
    public void Build_rejects_null_services()
        => Assert.Throws<ArgumentNullException>(() =>
            OpenApiDocumentBuilder.Build("http://h", null!));

    // ---- helpers ---------------------------------------------------

    private static BowireMessageInfo EmptyMessage()
        => new("Empty", "Empty", []);

    private static BowireServiceInfo MakeService(string name, string version,
        params (string opId, string verb, string path)[] methods)
    {
        var methodList = new List<BowireMethodInfo>();
        foreach (var (opId, verb, path) in methods)
        {
            methodList.Add(new BowireMethodInfo(
                Name: opId, FullName: $"{verb} {path}",
                ClientStreaming: false, ServerStreaming: false,
                InputType: EmptyMessage(), OutputType: EmptyMessage(),
                MethodType: "Unary")
                { HttpMethod = verb, HttpPath = path });
        }
        return new BowireServiceInfo(name, "rest", methodList) { Version = version };
    }
}

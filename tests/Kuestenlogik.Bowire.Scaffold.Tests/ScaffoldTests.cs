// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kuestenlogik.Bowire.Scaffold.Tests;

/// <summary>
/// #177 — a sentence or a spec in, schema + stub + collection + smoke test
/// out. The generated projects are built and run against `bowire test` by
/// hand for each template change (see docs/features/scaffolding.md); these
/// tests hold the parts that decide what gets generated.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2234:Pass system uri objects instead of strings", Justification = "Relative paths against the test server")]
public sealed class ScaffoldTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScaffoldSpec Users(string protocol = "rest") => new("User", protocol,
    [
        new ScaffoldField("email", "string", Required: true),
        new ScaffoldField("role", "string"),
        new ScaffoldField("age", "int"),
    ]);

    // ---- the parser ----------------------------------------------------

    [Fact]
    public void The_ticket_sentence_reads_as_a_user_with_email_and_role()
    {
        var (spec, notes) = ScaffoldIntentParser.Parse("REST CRUD for User with email + role");
        Assert.Equal("User", spec.Entity);
        Assert.Equal("rest", spec.Protocol);
        Assert.Equal(["email", "role"], spec.Fields.Select(f => f.Name));
        Assert.Empty(notes);
    }

    [Fact]
    public void Types_and_required_come_from_the_words_next_to_a_field()
    {
        var (spec, _) = ScaffoldIntentParser.Parse("gRPC service for orders with customer (string, required), total: double, paid bool, placedAt, isGift, quantity*");
        Assert.Equal("Order", spec.Entity);
        Assert.Equal("grpc", spec.Protocol);
        Assert.Collection(spec.Fields,
            f => Assert.Equal(("customer", "string", true), (f.Name, f.Type, f.Required)),
            f => Assert.Equal(("total", "double", false), (f.Name, f.Type, f.Required)),
            f => Assert.Equal(("paid", "bool", false), (f.Name, f.Type, f.Required)),
            f => Assert.Equal(("placedAt", "datetime", false), (f.Name, f.Type, f.Required)),
            f => Assert.Equal(("isGift", "bool", false), (f.Name, f.Type, f.Required)),
            f => Assert.Equal(("quantity", "int", true), (f.Name, f.Type, f.Required)));
    }

    [Fact]
    public void A_German_sentence_works_too()
    {
        var (spec, _) = ScaffoldIntentParser.Parse("REST-API für Kunden mit Name (Pflicht), E-Mail und Größe");
        Assert.Equal("Kunden", spec.Entity);
        Assert.Equal(["name", "email", "groesse"], spec.Fields.Select(f => f.Name));
        Assert.True(spec.Fields[0].Required);
    }

    [Fact]
    public void What_the_parser_assumes_it_says()
    {
        var (spec, notes) = ScaffoldIntentParser.Parse("something useful");
        Assert.Equal("Item", spec.Entity);
        Assert.Equal("rest", spec.Protocol);
        Assert.Single(spec.Fields);
        Assert.Equal(3, notes.Count);
    }

    [Fact]
    public void The_protocol_parameter_wins_over_the_sentence()
    {
        var (spec, _) = ScaffoldIntentParser.Parse("REST CRUD for User with email", "grpc");
        Assert.Equal("grpc", spec.Protocol);
    }

    // ---- normalization -------------------------------------------------

    [Fact]
    public void Normalizing_fixes_the_names_and_drops_id()
    {
        var (spec, errors) = new ScaffoldSpec("purchase orders", "REST",
            [new("Id", "uuid"), new("first name", "text", true), new("created_at", "timestamp")]).Normalize();
        Assert.Empty(errors);
        Assert.Equal("PurchaseOrders", spec!.Entity);
        Assert.Equal("rest", spec.Protocol);
        Assert.Equal("PurchaseOrderses", spec.Service);
        Assert.Equal([("firstName", "string"), ("createdAt", "datetime")], spec.Fields.Select(f => (f.Name, f.Type)));
        Assert.Equal("http://localhost:5000", spec.BaseUrl);
    }

    [Theory]
    [InlineData("User", "soap", "email", "string", "protocol")]
    [InlineData("1User", "rest", "email", "string", "entity")]
    [InlineData("User", "rest", "email", "money", "type")]
    [InlineData("User", "rest", "user", "string", "entity's own name")]
    [InlineData("User", "rest", "id", "string", "at least one field")]
    public void A_spec_that_cannot_be_fixed_says_why(string entity, string protocol, string field, string type, string expected)
    {
        var (spec, errors) = new ScaffoldSpec(entity, protocol, [new(field, type)]).Normalize();
        Assert.Null(spec);
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void A_service_named_like_its_entity_gets_a_suffix()
    {
        var (spec, _) = new ScaffoldSpec("News", "rest", [new("title", "string")], Service: "News").Normalize();
        Assert.Equal("NewsService", spec!.Service);
    }

    // ---- the template language -----------------------------------------

    [Fact]
    public void Templates_loop_branch_and_invert()
    {
        var model = new Dictionary<string, object?>
        {
            ["Name"] = "X",
            ["items"] = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { ["n"] = 1, ["Last"] = false },
                new Dictionary<string, object?> { ["n"] = 2, ["Last"] = true },
            },
            ["flag"] = false,
            ["note"] = "",
        };
        var text = ScaffoldTemplate.Render("{{Name}}({{#items}}{{n}}{{^Last}}, {{/Last}}{{/items}}){{#flag}}!{{/flag}}{{^flag}}?{{/flag}}{{#note}}n{{/note}}", model);
        Assert.Equal("X(1, 2)?", text);
    }

    [Fact]
    public void An_unknown_name_in_a_template_fails_instead_of_leaving_a_hole()
    {
        Assert.Throws<KeyNotFoundException>(() => ScaffoldTemplate.Render("{{Missing}}", new Dictionary<string, object?>()));
        Assert.Throws<FormatException>(() => ScaffoldTemplate.Render("{{#open}}x", new Dictionary<string, object?> { ["open"] = true }));
    }

    // ---- generation ----------------------------------------------------

    [Fact]
    public void A_REST_scaffold_has_every_artifact_and_the_same_bytes_every_time()
    {
        var first = ScaffoldGenerator.Generate(Users());
        var second = ScaffoldGenerator.Generate(Users());
        Assert.Equal(first, second);
        Assert.Equal(
            ["bowire-scaffold.json", "openapi.yaml", "Program.cs", "Users.csproj", "bowire/users.collection.json", "bowire/users.smoke-test.json", "README.md"],
            first.Select(f => f.Path));
        Assert.Equal(["spec", "schema", "stub", "stub", "collection", "test", "readme"], first.Select(f => f.Kind));
        Assert.DoesNotContain(first, f => f.Content.Contains("{{", StringComparison.Ordinal) && f.Kind is not "collection" and not "readme");
    }

    [Fact]
    public void The_OpenAPI_names_the_operations_the_collection_calls()
    {
        var files = ScaffoldGenerator.Generate(Users());
        var yaml = files.Single(f => f.Path == "openapi.yaml").Content;
        using var collection = JsonDocument.Parse(files.Single(f => f.Kind == "collection").Content);
        foreach (var item in collection.RootElement.GetProperty("items").EnumerateArray())
        {
            Assert.Equal("users", item.GetProperty("service").GetString());
            Assert.Contains("operationId: " + item.GetProperty("method").GetString(), yaml, StringComparison.Ordinal);
            Assert.Equal("http://localhost:5000/openapi.yaml", item.GetProperty("serverUrl").GetString());
        }
        Assert.Contains("      required:\n        - email\n", yaml, StringComparison.Ordinal);
        Assert.Contains("format: int32", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_collection_covers_the_error_cases_and_reads_the_id_from_a_variable()
    {
        using var doc = JsonDocument.Parse(ScaffoldGenerator.Generate(Users()).Single(f => f.Kind == "collection").Content);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["list", "create", "get", "update", "delete", "get-missing", "create-missing-required"],
            items.Select(i => i.GetProperty("id").GetString()!.Split("users_")[1]));
        Assert.Contains("{{userId}}", items[2].GetProperty("body").GetString(), StringComparison.Ordinal);
        // REST flattens the id and the body fields into one message.
        using var update = JsonDocument.Parse(items[3].GetProperty("body").GetString()!);
        Assert.Equal("ada@example.com", update.RootElement.GetProperty("email").GetString());
        using var missing = JsonDocument.Parse(items[6].GetProperty("body").GetString()!);
        Assert.False(missing.RootElement.TryGetProperty("email", out _));
    }

    [Fact]
    public void The_smoke_test_asserts_in_Bowire_status_names()
    {
        using var doc = JsonDocument.Parse(ScaffoldGenerator.Generate(Users()).Single(f => f.Kind == "test").Content);
        var tests = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();
        Assert.Equal(4, tests.Count);
        Assert.Equal("create a user", tests[1].GetProperty("name").GetString());
        var asserts = tests[1].GetProperty("assert").EnumerateArray().Select(a => a.GetProperty("path").GetString()).ToList();
        Assert.Equal(["status", "response.id", "response.email", "response.role", "response.age"], asserts);
        Assert.Equal("NotFound", tests[2].GetProperty("assert")[0].GetProperty("expected").GetString());
        Assert.Equal("InvalidArgument", tests[3].GetProperty("assert")[0].GetProperty("expected").GetString());
    }

    [Fact]
    public void A_gRPC_scaffold_has_a_proto_a_service_and_nested_update_input()
    {
        var files = ScaffoldGenerator.Generate(Users("grpc") with { Entity = "Order" });
        Assert.Equal(
            ["bowire-scaffold.json", "orders.proto", "OrdersService.cs", "Program.cs", "Orders.csproj", "bowire/orders.collection.json", "bowire/orders.smoke-test.json", "README.md"],
            files.Select(f => f.Path));
        var proto = files.Single(f => f.Path == "orders.proto").Content;
        Assert.Contains("package orders.v1;", proto, StringComparison.Ordinal);
        Assert.Contains("  optional int32 age = 4;", proto, StringComparison.Ordinal);
        Assert.Contains("  string email = 1; // required", proto, StringComparison.Ordinal);
        Assert.Contains("rpc CreateOrder (OrderInput) returns (Order);", proto, StringComparison.Ordinal);

        using var collection = JsonDocument.Parse(files.Single(f => f.Kind == "collection").Content);
        var update = collection.RootElement.GetProperty("items")[3];
        Assert.Equal("orders.v1.Orders", update.GetProperty("service").GetString());
        Assert.Equal("UpdateOrder", update.GetProperty("method").GetString());
        using var body = JsonDocument.Parse(update.GetProperty("body").GetString()!);
        Assert.Equal("ada@example.com", body.RootElement.GetProperty("order").GetProperty("email").GetString());

        var service = files.Single(f => f.Path == "OrdersService.cs").Content;
        Assert.Contains("if (input.HasAge) item.Age = input.Age;", service, StringComparison.Ordinal);
        Assert.Contains("if (string.IsNullOrEmpty(input.Email)) missing.Add(\"email\");", service, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("order", "an order")]
    [InlineData("update", "an update")]
    [InlineData("user", "a user")]
    [InlineData("unit", "a unit")]
    [InlineData("book", "a book")]
    public void The_article_follows_the_sound(string word, string expected) =>
        Assert.Equal(expected, ScaffoldGenerator.WithArticle(word));

    [Fact]
    public void An_entity_starting_with_a_vowel_gets_an()
    {
        var files = ScaffoldGenerator.Generate(Users() with { Entity = "Order" });
        Assert.Contains("summary: Create an order", files.Single(f => f.Path == "openapi.yaml").Content, StringComparison.Ordinal);
    }

    // ---- writing into the workspace ------------------------------------

    [Theory]
    [InlineData("../evil", "a.txt", "folder")]
    [InlineData("users", "../a.txt", "relative path")]
    [InlineData("users", "/etc/passwd", "relative path")]
    [InlineData("users", "C:/x.txt", "relative path")]
    [InlineData("a/b", "a.txt", "folder")]
    public void A_write_cannot_leave_its_folder(string folder, string path, string expected)
    {
        var (ok, errors) = BowireScaffoldEndpoints.ValidateWrite(new ScaffoldWriteRequest(folder, [new ScaffoldFile(path, "stub", "text", "x")]));
        Assert.Null(ok);
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_endpoints_parse_generate_and_refuse_a_bad_spec()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseTestServer();
        await using var app = b.Build();
        app.MapBowireScaffoldEndpoints("/bowire");
        await app.StartAsync(Ct);
        var http = app.GetTestClient();

        var parsed = await http.PostAsJsonAsync("/bowire/api/scaffold/parse", new { intent = "REST CRUD for User with email + role" }, Ct);
        using var parsedDoc = JsonDocument.Parse(await parsed.Content.ReadAsStringAsync(Ct));
        Assert.Equal("parser", parsedDoc.RootElement.GetProperty("source").GetString());
        Assert.Equal("User", parsedDoc.RootElement.GetProperty("spec").GetProperty("entity").GetString());

        var generated = await http.PostAsJsonAsync("/bowire/api/scaffold/generate", new { spec = Users() }, Ct);
        using var genDoc = JsonDocument.Parse(await generated.Content.ReadAsStringAsync(Ct));
        Assert.Equal(7, genDoc.RootElement.GetProperty("files").GetArrayLength());

        var bad = await http.PostAsJsonAsync("/bowire/api/scaffold/generate", new { spec = new { entity = "User", protocol = "soap", fields = Array.Empty<object>() } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}

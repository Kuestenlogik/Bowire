// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;
using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.Interop.Tests;

/// <summary>
/// .http import and export (#673, #674): the JetBrains dialect read into REST
/// items, every unsupported line reported, secrets never written, and a
/// round trip that keeps the requests.
/// </summary>
public sealed class HttpFileTests
{
    private const string Sample = """
        @host = https://api.example.com
        @token = abc

        ### Create user
        # @name createUser
        # @no-redirect
        POST {{host}}/users
            ?dry=true
            &trace=1
        Content-Type: application/json
        Authorization: Basic alice secret

        { "id": "{{$uuid}}", "at": {{$timestamp}} }

        > {% client.global.set("uid", response.body.id); %}
        >> ./out/user.json

        ###
        GET {{host}}/users/{{createUser.response.body.$.id}}
        Authorization: Bearer {{token}}

        ###
        POST {{host}}/upload
        Content-Type: application/json

        < ./payload.json
        """;

    private static ImportCollectionResponse Import(params (string Path, string Content)[] files)
    {
        var request = new ImportCollectionRequest();
        foreach (var (path, content) in files) request.Files.Add(new SourceFile { Path = path, Content = content });
        return InteropRpc.ImportCollection(request);
    }

    [Fact]
    public void Requests_Become_Rest_Items_With_Bowire_Variables()
    {
        var result = Import(("api.http", Sample));

        Assert.Equal("http", result.Format);
        var collection = Assert.Single(result.Collections);
        Assert.Equal("api", collection.Name);
        Assert.Equal(3, collection.Items.Count);

        var create = collection.Items[0];
        Assert.Equal("Create user", create.Name);
        Assert.Equal("rest", create.Protocol);
        Assert.Equal("POST", create.Method);
        Assert.Equal("{{host}}/users?dry=true&trace=1", create.Service);
        Assert.Equal("""{ "id": "{{uuid}}", "at": {{now}} }""", Assert.Single(create.Messages));
        // The `Basic user password` shorthand is encoded, as JetBrains sends it.
        Assert.Equal("Basic YWxpY2U6c2VjcmV0", create.Metadata["Authorization"]);

        Assert.Equal("{}", collection.Items[1].Messages[0]);
        Assert.Equal("GET {{host}}/users/{{createUser.response.body.$.id}}", collection.Items[1].Name);
    }

    [Fact]
    public void File_Variables_Become_An_Environment()
    {
        var env = Assert.Single(Import(("api.http", Sample)).Environments);
        Assert.Equal("api (file variables)", env.Name);
        Assert.Equal("https://api.example.com", env.Vars["host"]);
        Assert.Equal("abc", env.Vars["token"]);
    }

    [Fact]
    public void Everything_Unsupported_Is_Reported_Not_Dropped()
    {
        var report = Import(("api.http", Sample)).Report;
        var skipped = report.Entries.Where(e => e.Kind == ReportEntry.Types.Kind.Skipped).Select(e => e.Detail).ToList();

        Assert.Contains(skipped, d => d.Contains("response handler", StringComparison.Ordinal) && d.Contains("client.global", StringComparison.Ordinal));
        Assert.Contains(skipped, d => d.Contains(">> ./out/user.json", StringComparison.Ordinal));
        Assert.Contains(skipped, d => d.Contains("body from a file", StringComparison.Ordinal));
        Assert.Contains(skipped, d => d == "directive @no-redirect");
        // The literal Basic credentials are flagged; the Bearer {{token}} reference is not.
        var credential = Assert.Single(report.Credentials);
        Assert.Equal("Create user", credential.Subject);
        Assert.Equal("Authorization header", credential.Field);
    }

    [Fact]
    public void Env_Files_Merge_Shared_Values_And_Flag_Private_Ones()
    {
        var result = Import(
            ("api.http", "GET {{host}}/health\n"),
            ("http-client.env.json", """{ "$shared": { "host": "http://localhost:5000" }, "dev": { "token": "dev" }, "prod": { "host": "https://api.example.com" } }"""),
            ("http-client.private.env.json", """{ "prod": { "token": "s3cret" } }"""));

        var dev = result.Environments.Single(e => e.Name == "dev");
        Assert.Equal("http://localhost:5000", dev.Vars["host"]);
        var prod = result.Environments.Single(e => e.Name == "prod");
        Assert.Equal("https://api.example.com", prod.Vars["host"]);
        Assert.Equal("s3cret", prod.Vars["token"]);
        Assert.Contains(result.Report.Credentials, c => c.Subject == "prod" && c.Field == "private variable token");
    }

    [Fact]
    public void Websocket_And_Grpc_Requests_Are_Skipped_With_A_Reason()
    {
        var result = Import(("x.http", "WEBSOCKET ws://localhost/ws\n\n###\nGET https://example.com/\n"));
        Assert.Single(Assert.Single(result.Collections).Items);
        Assert.Contains(result.Report.Entries, e => e.Kind == ReportEntry.Types.Kind.Skipped && e.Detail.StartsWith("WEBSOCKET", StringComparison.Ordinal));
    }

    [Fact]
    public void A_File_That_Is_Not_Http_Is_Refused_As_Invalid_Argument()
    {
        var ex = Assert.Throws<RpcException>(() => Import(("notes.txt", "just some prose")));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public void Export_Writes_Requests_And_Never_A_Secret()
    {
        var request = new ExportHttpRequest();
        var collection = new Collection { Name = "Orders" };
        var item = new CollectionItem
        {
            Name = "Create order", Protocol = "rest", Service = "{{baseUrl}}/orders", Method = "POST", MethodType = "Unary",
        };
        item.Messages.Add("""{"id":"${uuid}","qty":2}""");
        item.Metadata["Authorization"] = "Bearer {{secret.apiToken}}";
        item.Metadata["X-Key"] = "{{keyring.orders/api}}";
        item.Metadata["__bowireMtls__"] = "{\"privateKey\":\"never\"}";
        collection.Items.Add(item);
        collection.Items.Add(new CollectionItem { Name = "Stream", Protocol = "grpc", Service = "orders.Orders", Method = "Watch", MethodType = "ServerStreaming" });
        request.Collections.Add(collection);
        var env = new V1.Environment { Name = "dev" };
        env.Vars["baseUrl"] = "http://localhost:5000";
        env.Auth["type"] = "bearer";
        request.Environments.Add(env);

        var result = InteropRpc.ExportHttp(request);

        Assert.Contains("### Create order\n# @name Create-order\nPOST {{baseUrl}}/orders\n", result.Http, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer {{apiToken}}", result.Http, StringComparison.Ordinal);
        Assert.Contains("X-Key: {{orders_api}}", result.Http, StringComparison.Ordinal);
        Assert.Contains("""{"id":"{{$uuid}}","qty":2}""", result.Http, StringComparison.Ordinal);
        Assert.Contains("#   apiToken", result.Http, StringComparison.Ordinal);
        Assert.DoesNotContain("__bowire", result.Http, StringComparison.Ordinal);
        Assert.DoesNotContain("never", result.Http, StringComparison.Ordinal);
        Assert.Contains("\"baseUrl\": \"http://localhost:5000\"", result.EnvJson, StringComparison.Ordinal);
        Assert.Contains(result.Report.Entries, e => e.Subject == "Stream" && e.Kind == ReportEntry.Types.Kind.Skipped);
        Assert.Contains(result.Report.Entries, e => e.Subject == "dev" && e.Detail.Contains("environment auth", StringComparison.Ordinal));
    }

    [Fact]
    public void Discovered_Methods_Export_From_Their_Verb_And_Path()
    {
        var request = new ExportHttpRequest();
        var collection = new Collection { Name = "Pets" };
        collection.Items.Add(new CollectionItem
        {
            Name = "getPet", Protocol = "rest", Service = "Pets", Method = "getPet", MethodType = "Unary",
            ServerUrl = "https://petstore.example/v3/", HttpMethod = "GET", HttpPath = "/pet/{{petId}}",
        });
        collection.Items.Add(new CollectionItem { Name = "noPath", Protocol = "rest", Service = "Pets", Method = "addPet", MethodType = "Unary" });
        request.Collections.Add(collection);

        var result = InteropRpc.ExportHttp(request);

        Assert.Contains("GET https://petstore.example/v3/pet/{{petId}}\n", result.Http, StringComparison.Ordinal);
        Assert.Contains(result.Report.Entries, e => e.Subject == "noPath" && e.Kind == ReportEntry.Types.Kind.Skipped);
    }

    [Fact]
    public void Export_Then_Import_Keeps_The_Requests()
    {
        var original = Import(("api.http", """
            ### List orders
            GET https://api.example.com/orders?page=1
            Accept: application/json

            ### Create order
            POST https://api.example.com/orders
            Content-Type: application/json
            X-Trace: {{traceId}}

            {"qty": 2, "id": "{{uuid}}"}
            """));
        var export = new ExportHttpRequest();
        export.Collections.Add(original.Collections);

        var roundTripped = Import(("api.http", InteropRpc.ExportHttp(export).Http));

        var before = original.Collections[0].Items;
        var after = roundTripped.Collections[0].Items;
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Method, after[i].Method);
            Assert.Equal(before[i].Service, after[i].Service);
            Assert.Equal(before[i].Messages, after[i].Messages);
            Assert.Equal(before[i].Metadata.OrderBy(h => h.Key), after[i].Metadata.OrderBy(h => h.Key));
        }
    }
}

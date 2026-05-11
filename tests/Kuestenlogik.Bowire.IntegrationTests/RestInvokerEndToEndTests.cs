// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Rest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// xUnit collection definition that serialises every test in
/// <see cref="RestInvokerEndToEndTests"/> + <see cref="BowireRestProtocolEndToEndTests"/>.
/// Each test opens an ephemeral TCP port via <c>GetFreeTcpPort()</c>
/// (bind to port 0, read, close), then hands that port to Kestrel.
/// Parallel test execution can — and on Linux CI does — race two tests
/// on the same returned port number, causing "address already in use".
/// Forcing the collection to run sequentially trades a few seconds of
/// wallclock for deterministic CI runs.
/// </summary>
[CollectionDefinition(nameof(RestInvokerEndToEndFixture))]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit collection definition must be public.")]
public sealed class RestInvokerEndToEndFixture { }

/// <summary>
/// Full-stack <see cref="RestInvoker"/> coverage that needs a real socket —
/// query / header / body field bucketing, repeated query, metadata header
/// passthrough, AWS SigV4 round-trip, server-error mapping, HEAD/DELETE
/// no-body paths, multipart array repetition and a malformed-base64 binary.
/// Single Kestrel host per test keeps each scenario isolated.
/// </summary>
[Collection(nameof(RestInvokerEndToEndFixture))]
public sealed class RestInvokerEndToEndTests
{
    [Fact]
    public async Task Query_Header_And_Body_Buckets_All_Reach_Server()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapPost("/things/{id}", async (HttpContext ctx, string id) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            return Results.Json(new
            {
                id,
                limitFirst = ctx.Request.Query["limit"].ToString(),
                tags = ctx.Request.Query["tags"].ToArray(),
                trace = ctx.Request.Headers["X-Trace"].ToString(),
                custom = ctx.Request.Headers["X-Custom"].ToString(),
                contentType = ctx.Request.ContentType ?? string.Empty,
                body
            });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var fields = new List<BowireFieldInfo>
            {
                new("id", 1, "string", "required", false, false, null, null) { Source = "path", Required = true },
                new("limit", 2, "int32", "optional", false, false, null, null) { Source = "query" },
                new("tags", 3, "string", "optional", false, true, null, null) { Source = "query" },
                new("X-Trace", 4, "string", "optional", false, false, null, null) { Source = "header" },
                new("name", 5, "string", "optional", false, false, null, null) { Source = "body" }
            };

            var method = new BowireMethodInfo(
                "Make", "POST /things/{id}", false, false,
                new BowireMessageInfo("Req", "Req", fields),
                new BowireMessageInfo("Res", "Res", []),
                "Unary")
            { HttpMethod = "POST", HttpPath = "/things/{id}" };

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            var json = """
                {
                  "id": "abc 42",
                  "limit": 5,
                  "tags": ["a", "b"],
                  "X-Trace": "trace-1",
                  "name": "widget"
                }
                """;

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["X-Custom"] = "hello"
            };

            var result = await RestInvoker.InvokeAsync(http, url, method,
                [json], metadata, TestContext.Current.CancellationToken);

            Assert.Equal("OK", result.Status);
            Assert.NotNull(result.Response);
            Assert.Contains("\"id\":\"abc 42\"", result.Response!, StringComparison.Ordinal);
            Assert.Contains("\"limitFirst\":\"5\"", result.Response!, StringComparison.Ordinal);
            Assert.Contains("\"tags\":[\"a\",\"b\"]", result.Response!, StringComparison.Ordinal);
            Assert.Contains("\"trace\":\"trace-1\"", result.Response!, StringComparison.Ordinal);
            Assert.Contains("\"custom\":\"hello\"", result.Response!, StringComparison.Ordinal);
            Assert.Contains("application/json", result.Response!, StringComparison.Ordinal);
            // The server echoes the request body back in its `body` field as
            // a JSON-encoded string, so the inner quotes round-trip escaped:
            // "body":"{\"name\":\"widget\"}". The unescaped substring is
            // therefore not present — assert on the escaped form instead.
            Assert.Contains("\\\"name\\\":\\\"widget\\\"", result.Response!, StringComparison.Ordinal);
            // Status response headers also carry the http_status entry
            Assert.Equal("200", result.Metadata["http_status"]);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Server_Error_Maps_To_Internal_Status()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/boom", () => Results.Problem("nope", statusCode: 503));
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var method = new BowireMethodInfo(
                "Boom", "GET /boom", false, false,
                new BowireMessageInfo("Req", "Req", []),
                new BowireMessageInfo("Res", "Res", []),
                "Unary")
            { HttpMethod = "GET", HttpPath = "/boom" };

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var result = await RestInvoker.InvokeAsync(http, url, method,
                ["{}"], null, TestContext.Current.CancellationToken);

            Assert.Equal("Unavailable", result.Status);
            Assert.Equal("503", result.Metadata["http_status"]);
            Assert.NotNull(result.Response);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Get_Drops_Body_Fields_Because_Verb_Cannot_Carry_Body()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/q", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            return Results.Json(new { contentType = ctx.Request.ContentType ?? string.Empty, body });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var fields = new List<BowireFieldInfo>
            {
                new("payload", 1, "string", "optional", false, false, null, null) { Source = "body" }
            };
            var method = new BowireMethodInfo(
                "Q", "GET /q", false, false,
                new BowireMessageInfo("Req", "Req", fields),
                new BowireMessageInfo("Res", "Res", []),
                "Unary")
            { HttpMethod = "GET", HttpPath = "/q" };

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var result = await RestInvoker.InvokeAsync(http, url, method,
                ["{\"payload\":\"ignored\"}"], null, TestContext.Current.CancellationToken);

            Assert.Equal("OK", result.Status);
            // Body present in the input gets dropped for verbs CanHaveBody=false.
            Assert.Contains("\"body\":\"\"", result.Response!, StringComparison.Ordinal);
            Assert.Contains("\"contentType\":\"\"", result.Response!, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Path_Template_With_Existing_Query_String_Appends_Extra_Params()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/search", (HttpContext ctx) => Results.Json(new
        {
            mode = ctx.Request.Query["mode"].ToString(),
            q = ctx.Request.Query["q"].ToString()
        }));
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var fields = new List<BowireFieldInfo>
            {
                // q field as query param; the path template already contains ?mode=...
                new("q", 1, "string", "optional", false, false, null, null) { Source = "query" }
            };
            var method = new BowireMethodInfo(
                "S", "GET /search?mode=fast", false, false,
                new BowireMessageInfo("Req", "Req", fields),
                new BowireMessageInfo("Res", "Res", []),
                "Unary")
            { HttpMethod = "GET", HttpPath = "/search?mode=fast" };

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var result = await RestInvoker.InvokeAsync(http, url, method,
                ["{\"q\":\"shoes\"}"], null, TestContext.Current.CancellationToken);

            Assert.Equal("OK", result.Status);
            Assert.Contains("\"mode\":\"fast\"", result.Response!, StringComparison.Ordinal);
            Assert.Contains("\"q\":\"shoes\"", result.Response!, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AwsSigV4_Marker_Signs_Request_Authorization_Reaches_Server()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapPost("/sig", (HttpContext ctx) => Results.Json(new
        {
            auth = ctx.Request.Headers["Authorization"].ToString(),
            amzDate = ctx.Request.Headers["X-Amz-Date"].ToString(),
            sha = ctx.Request.Headers["X-Amz-Content-Sha256"].ToString(),
            sessTok = ctx.Request.Headers["X-Amz-Security-Token"].ToString()
        }));
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var method = new BowireMethodInfo(
                "Sig", "POST /sig", false, false,
                new BowireMessageInfo("Req", "Req", []),
                new BowireMessageInfo("Res", "Res", []),
                "Unary")
            { HttpMethod = "POST", HttpPath = "/sig" };

            var sigConfig = """
                {
                  "accessKey": "AKIATESTKEY1234",
                  "secretKey": "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
                  "region": "us-east-1",
                  "service": "execute-api",
                  "sessionToken": "FwoGZXIvYXdzEJP"
                }
                """;

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["__bowireAwsSigV4__"] = sigConfig
            };

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var result = await RestInvoker.InvokeAsync(http, url, method,
                ["{}"], metadata, TestContext.Current.CancellationToken);

            Assert.Equal("OK", result.Status);
            Assert.Contains("AWS4-HMAC-SHA256", result.Response!, StringComparison.Ordinal);
            Assert.Contains("FwoGZXIvYXdzEJP", result.Response!, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AwsSigV4_Marker_Bad_Json_Is_Silently_Ignored_Request_Still_Sent()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/ping", (HttpContext ctx) => Results.Json(new
        {
            auth = ctx.Request.Headers["Authorization"].ToString()
        }));
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var method = new BowireMethodInfo(
                "Ping", "GET /ping", false, false,
                new BowireMessageInfo("Req", "Req", []),
                new BowireMessageInfo("Res", "Res", []),
                "Unary")
            { HttpMethod = "GET", HttpPath = "/ping" };

            // Garbage JSON on the SigV4 marker — TryParse returns null and the
            // signing step gets skipped without erroring the request.
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["__bowireAwsSigV4__"] = "not json"
            };

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var result = await RestInvoker.InvokeAsync(http, url, method,
                ["{}"], metadata, TestContext.Current.CancellationToken);

            Assert.Equal("OK", result.Status);
            Assert.Contains("\"auth\":\"\"", result.Response!, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Multipart_Repeated_Array_Field_Emits_One_Part_Per_Element()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapPost("/tags", async (HttpRequest req) =>
        {
            var form = await req.ReadFormAsync();
            var tags = form["tag"].ToArray();
            return Results.Json(new { tags, contentType = req.ContentType ?? string.Empty });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var fields = new List<BowireFieldInfo>
            {
                new("tag", 1, "string", "optional", false, true, null, null) { Source = "formdata" }
            };
            var method = new BowireMethodInfo(
                "T", "POST /tags", false, false,
                new BowireMessageInfo("Req", "Req", fields),
                new BowireMessageInfo("Res", "Res", []),
                "Unary")
            { HttpMethod = "POST", HttpPath = "/tags" };

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var result = await RestInvoker.InvokeAsync(http, url, method,
                ["{\"tag\":[\"red\",\"blue\",\"green\"]}"], null, TestContext.Current.CancellationToken);

            Assert.Equal("OK", result.Status);
            Assert.Contains("multipart/form-data", result.Response!, StringComparison.Ordinal);
            Assert.Contains("\"red\"", result.Response!, StringComparison.Ordinal);
            Assert.Contains("\"blue\"", result.Response!, StringComparison.Ordinal);
            Assert.Contains("\"green\"", result.Response!, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Multipart_Binary_With_Invalid_Base64_Sends_Empty_File()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapPost("/up", async (HttpRequest req) =>
        {
            var form = await req.ReadFormAsync();
            var file = form.Files.Count > 0 ? form.Files[0] : null;
            return Results.Json(new { length = file?.Length ?? -1L });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var fields = new List<BowireFieldInfo>
            {
                new("file", 1, "string", "required", false, false, null, null)
                {
                    Source = "formdata",
                    IsBinary = true
                }
            };
            var method = new BowireMethodInfo(
                "U", "POST /up", false, false,
                new BowireMessageInfo("Req", "Req", fields),
                new BowireMessageInfo("Res", "Res", []),
                "Unary")
            { HttpMethod = "POST", HttpPath = "/up" };

            // Garbage base64 — FromBase64String throws; the invoker falls back
            // to an empty byte array so the request still ships.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var result = await RestInvoker.InvokeAsync(http, url, method,
                ["{\"file\":{\"filename\":\"x.bin\",\"data\":\"@@@not-base64@@@\"}}"],
                null, TestContext.Current.CancellationToken);

            Assert.Equal("OK", result.Status);
            Assert.Contains("\"length\":0", result.Response!, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Empty_Response_Body_Becomes_Null_Response_Property()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/empty", () => Results.NoContent());
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var method = new BowireMethodInfo(
                "E", "GET /empty", false, false,
                new BowireMessageInfo("Req", "Req", []),
                new BowireMessageInfo("Res", "Res", []),
                "Unary")
            { HttpMethod = "GET", HttpPath = "/empty" };

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var result = await RestInvoker.InvokeAsync(http, url, method,
                ["{}"], null, TestContext.Current.CancellationToken);

            Assert.Equal("OK", result.Status);
            Assert.Null(result.Response);
            Assert.Equal("204", result.Metadata["http_status"]);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>
/// End-to-end coverage for <see cref="BowireRestProtocol"/> as the user
/// experiences it: discover an OpenAPI doc hosted on a Kestrel host, invoke
/// a method by service/method name, exercise the transcoded
/// <see cref="IInlineHttpInvoker"/> path, and verify the cache-priming
/// fall-through on cold invocation.
/// </summary>
[Collection(nameof(RestInvokerEndToEndFixture))]
public sealed class BowireRestProtocolEndToEndTests
{
    [Fact]
    public async Task Discover_And_Invoke_Against_Real_OpenApi_Server_Round_Trips()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        const string openApi = $$"""
            {
              "openapi": "3.0.0",
              "info": { "title": "Pets API", "version": "1.0" },
              "servers": [{ "url": "BASEURL" }],
              "paths": {
                "/pets/{id}": {
                  "get": {
                    "operationId": "getPet",
                    "tags": ["Pets"],
                    "parameters": [
                      { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var swaggerJson = openApi.Replace("BASEURL", url, StringComparison.Ordinal);
        app.MapGet("/swagger/v1/swagger.json", () => Results.Content(swaggerJson, "application/json"));
        app.MapGet("/pets/{id}", (string id) => Results.Json(new { id, name = "Rex" }));
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var protocol = new BowireRestProtocol();
            var services = await protocol.DiscoverAsync(
                url + "/swagger/v1/swagger.json", showInternalServices: false, TestContext.Current.CancellationToken);

            var svc = Assert.Single(services);
            Assert.Equal("Pets", svc.Name);
            // OriginUrl preserves the exact URL the discovery was driven by
            // (here: the swagger.json URL), not just the base host.
            Assert.Equal(url + "/swagger/v1/swagger.json", svc.OriginUrl);

            // Hit the cache: invocation uses the OpenAPI server URL as base.
            var result = await protocol.InvokeAsync(
                serverUrl: url + "/swagger/v1/swagger.json",
                service: "Pets",
                method: "getPet",
                jsonMessages: ["{\"id\":\"42\"}"],
                showInternalServices: false,
                metadata: null,
                ct: TestContext.Current.CancellationToken);

            Assert.Equal("OK", result.Status);
            Assert.Contains("\"id\":\"42\"", result.Response!, StringComparison.Ordinal);
            Assert.Contains("\"name\":\"Rex\"", result.Response!, StringComparison.Ordinal);

            // Unknown service/method on a known cache → structured error.
            var miss = await protocol.InvokeAsync(
                serverUrl: url + "/swagger/v1/swagger.json",
                service: "Pets",
                method: "nope",
                jsonMessages: ["{}"],
                showInternalServices: false,
                metadata: null,
                ct: TestContext.Current.CancellationToken);
            Assert.Equal("Error", miss.Status);
            Assert.Contains("Unknown REST method", miss.Metadata["error"], StringComparison.Ordinal);

            // Transcoded HTTP path — uses the InvokeHttpAsync shim directly.
            var transcoded = await ((IInlineHttpInvoker)protocol).InvokeHttpAsync(
                url,
                new BowireMethodInfo("Get", "GET /pets/{id}", false, false,
                    new BowireMessageInfo("In", "In",
                    [
                        new("id", 1, "string", "required", false, false, null, null) { Source = "path", Required = true }
                    ]),
                    new BowireMessageInfo("Out", "Out", []), "Unary")
                {
                    HttpMethod = "GET",
                    HttpPath = "/pets/{id}"
                },
                ["{\"id\":\"99\"}"],
                metadata: null,
                ct: TestContext.Current.CancellationToken);

            Assert.Equal("OK", transcoded.Status);
            Assert.Contains("\"id\":\"99\"", transcoded.Response!, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Cold_Cache_Lazy_Discovery_Returns_Error_For_Non_OpenApi_Url()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        // The discovery URL itself returns HTML — discovery rejects it, the
        // cache stays empty, and the protocol surfaces a structured error.
        app.MapGet("/", () => Results.Content("<html>not openapi</html>", "text/html"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var protocol = new BowireRestProtocol();
            var result = await protocol.InvokeAsync(
                serverUrl: url,
                service: "Anything",
                method: "any",
                jsonMessages: ["{}"],
                showInternalServices: false,
                metadata: null,
                ct: TestContext.Current.CancellationToken);

            Assert.Equal("Error", result.Status);
            Assert.Contains("No OpenAPI document", result.Metadata["error"], StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Initialize_With_Configuration_Builds_HttpClient_From_Factory()
    {
        // Just verifies Initialize doesn't throw on a real IConfiguration —
        // the underlying BowireHttpClientFactory has its own tests; here
        // we only need the Initialize branch covered.
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/", () => Results.Text("hi"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var sp = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
                .AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
                    new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
                .BuildServiceProvider();

            var protocol = new BowireRestProtocol();
            protocol.Initialize(sp);

            // No throw + DiscoverAsync still works with the new HttpClient.
            var services = await protocol.DiscoverAsync(
                serverUrl: "", showInternalServices: false, TestContext.Current.CancellationToken);
            Assert.Empty(services);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Discover_Merges_Uploads_With_Url_Discovery_Uploads_Win()
    {
        // Upload registers a "Pets" service. URL discovery also yields a
        // "Pets" service (same tag) — the merge step drops the URL copy.
        const string uploaded = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Uploaded Pets", "version": "1" },
              "paths": {
                "/pets": {
                  "get": {
                    "operationId": "listPetsUploaded",
                    "tags": ["Pets"],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;
        const string remote = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Remote Pets", "version": "1" },
              "paths": {
                "/pets": {
                  "get": {
                    "operationId": "listPetsRemote",
                    "tags": ["Pets"],
                    "responses": { "200": { "description": "ok" } }
                  }
                },
                "/owners": {
                  "get": {
                    "operationId": "listOwners",
                    "tags": ["Owners"],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/openapi.json", () => Results.Content(remote, "application/json"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        OpenApiUploadStore.Clear();
        OpenApiUploadStore.Add(uploaded, "uploaded.json");
        try
        {
            var protocol = new BowireRestProtocol();
            var services = await protocol.DiscoverAsync(
                url + "/openapi.json", showInternalServices: false, TestContext.Current.CancellationToken);

            // Pets (from upload, shadows remote) + Owners (from remote).
            Assert.Equal(2, services.Count);
            var pets = services.Single(s => s.Name == "Pets");
            Assert.True(pets.IsUploaded);
            Assert.Equal("listPetsUploaded", pets.Methods[0].Name);
            Assert.Contains(services, s => s.Name == "Owners");
        }
        finally
        {
            OpenApiUploadStore.Clear();
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Discover_Returns_Empty_When_OpenApi_Doc_Url_Yields_Nothing()
    {
        // Drives the DiscoverInternalAsync 'discovered is null' branch:
        // server returns 404 → cache stays empty → empty list back.
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/openapi.json", () => Results.StatusCode(404));
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var protocol = new BowireRestProtocol();
            var services = await protocol.DiscoverAsync(
                url + "/openapi.json",
                showInternalServices: false,
                TestContext.Current.CancellationToken);

            Assert.Empty(services);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

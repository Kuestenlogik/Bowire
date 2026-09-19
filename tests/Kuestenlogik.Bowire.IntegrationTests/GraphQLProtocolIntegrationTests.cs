// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.GraphQL;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end tests for <see cref="BowireGraphQLProtocol"/> against a
/// minimal hand-rolled GraphQL server: introspection round-trip, generated
/// query invocation, verbatim query passthrough.
/// </summary>
public sealed class GraphQLProtocolIntegrationTests
{
    private static readonly JsonSerializerOptions s_caseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task Discover_Builds_Query_And_Mutation_Services_From_Introspection()
    {
        await using var host = await PluginTestHost.StartAsync(MapGraphQLEndpoint);
        using var protocol = new BowireGraphQLProtocol();

        var services = await protocol.DiscoverAsync(host.BaseUrl + "/graphql", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Equal(["Query", "Mutation"], services.Select(s => s.Name).ToArray());

        var query = services.Single(s => s.Name == "Query");
        Assert.Contains(query.Methods, m => m.Name == "ping");

        var mutation = services.Single(s => s.Name == "Mutation");
        Assert.Contains(mutation.Methods, m => m.Name == "echo");
        var echo = mutation.Methods.Single(m => m.Name == "echo");
        Assert.Single(echo.InputType.Fields);
        Assert.True(echo.InputType.Fields[0].Required);
        Assert.Equal("string", echo.InputType.Fields[0].Type);
    }

    [Fact]
    public async Task Invoke_VariablesOnly_BuildsAndSendsGeneratedQuery()
    {
        await using var host = await PluginTestHost.StartAsync(MapGraphQLEndpoint);
        using var protocol = new BowireGraphQLProtocol();

        var result = await protocol.InvokeAsync(
            host.BaseUrl + "/graphql",
            service: "Query",
            method: "ping",
            jsonMessages: ["{}"],
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("\"pong\"", result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_VerbatimQueryShape_PassesQueryThroughUnchanged()
    {
        await using var host = await PluginTestHost.StartAsync(MapGraphQLEndpoint);
        using var protocol = new BowireGraphQLProtocol();

        // The "method" name is `ping`, but the body specifies a verbatim
        // `echo` mutation — the plugin must forward that operation literally
        // instead of regenerating one based on the method name.
        const string verbatim = """
            { "query": "mutation echo($text: String!) { echo(text: $text) }",
              "variables": { "text": "verbatim wins" } }
            """;

        var result = await protocol.InvokeAsync(
            host.BaseUrl + "/graphql",
            service: "Query",
            method: "ping",
            jsonMessages: [verbatim],
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Contains("verbatim wins", result.Response, StringComparison.Ordinal);
    }

    /// <summary>
    /// Minimal GraphQL server. Returns a tiny introspection result with
    /// Query.ping and Mutation.echo, and routes incoming operations by
    /// substring match — enough for the tests above. The introspection
    /// response is hand-rolled JSON because anonymous types with leading
    /// underscores (`__schema`) round-trip unpredictably through
    /// JsonNamingPolicy.CamelCase.
    /// </summary>
    // ---- #710: operationName on the wire ----

    [Fact]
    public async Task A_Single_Named_Operation_Names_Itself_On_The_Wire()
    {
        // Nobody asked for it. The plugin parses the document, finds one
        // operation, and sends its name -- which costs nothing and is what
        // every other GraphQL client does.
        await using var host = await PluginTestHost.StartAsync(MapGraphQLEndpoint);
        using var protocol = new BowireGraphQLProtocol();

        var result = await InvokeVerbatimAsync(protocol, host,
            """{"query":"query Only { whichOperation }"}""");

        Assert.Equal("OK", result.Status);
        Assert.Contains("\"whichOperation\": \"Only\"", result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Several_Operations_Send_No_Name_Unless_One_Was_Chosen()
    {
        // The honest half of #710. With two operations and no choice the
        // plugin must not guess: guessing would run an operation nobody
        // picked and report it as a success.
        await using var host = await PluginTestHost.StartAsync(MapGraphQLEndpoint);
        using var protocol = new BowireGraphQLProtocol();

        var result = await InvokeVerbatimAsync(protocol, host,
            """{"query":"query A { whichOperation }\nquery B { whichOperation }"}""");

        Assert.Equal("OK", result.Status);
        Assert.Contains("\"whichOperation\": null", result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Chosen_Operation_Is_The_One_The_Server_Is_Told_To_Run()
    {
        // What the request builder's picker produces. Without this a
        // multi-operation document is simply not runnable.
        await using var host = await PluginTestHost.StartAsync(MapGraphQLEndpoint);
        using var protocol = new BowireGraphQLProtocol();

        var result = await InvokeVerbatimAsync(protocol, host,
            """{"query":"query A { whichOperation }\nquery B { whichOperation }","operationName":"B"}""");

        Assert.Equal("OK", result.Status);
        Assert.Contains("\"whichOperation\": \"B\"", result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Document_That_Does_Not_Parse_Never_Reaches_The_Server()
    {
        // #710 gap 5. The saving is not the round trip -- it is that the
        // answer names the problem and where it is, instead of arriving as
        // whatever wording the server chose for a body it could not read.
        await using var host = await PluginTestHost.StartAsync(MapGraphQLEndpoint);
        using var protocol = new BowireGraphQLProtocol();

        var result = await InvokeVerbatimAsync(protocol, host,
            """{"query":"query Broken { ping "}""");

        Assert.NotEqual("OK", result.Status);
        Assert.Null(result.Response);
        // Whose complaint this is, so an operator does not go looking at
        // their server for a parser that lives here.
        Assert.Contains("Bowire could not parse", result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Document_That_Parses_Is_Still_Sent()
    {
        // The guard must not become a second opinion on valid documents.
        await using var host = await PluginTestHost.StartAsync(MapGraphQLEndpoint);
        using var protocol = new BowireGraphQLProtocol();

        var result = await InvokeVerbatimAsync(protocol, host,
            """{"query":"query Fine { ping }"}""");

        Assert.Equal("OK", result.Status);
        Assert.Contains("pong", result.Response, StringComparison.Ordinal);
    }

    // ---- #713: queries over GET ----

    [Fact]
    public async Task A_Query_Can_Go_Over_Get_When_Asked()
    {
        // The server records the verb it was called with, because that is
        // the whole claim: nothing else about the response would differ.
        var verbs = new List<string>();
        await using var host = await PluginTestHost.StartAsync(app => MapVerbRecorder(app, verbs));
        using var protocol = new BowireGraphQLProtocol();

        var result = await protocol.InvokeAsync(
            host.BaseUrl + "/graphql", service: "Query", method: "ping",
            jsonMessages: ["""{"query":"query P { ping }"}"""],
            showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                [BowireGraphQLProtocol.HttpMethodMetadataKey] = "get",
            },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Equal(["GET"], verbs);
    }

    [Fact]
    public async Task Without_The_Flag_It_Still_Posts()
    {
        // The default does not move. Every server Bowire reaches today
        // keeps working exactly as it did.
        var verbs = new List<string>();
        await using var host = await PluginTestHost.StartAsync(app => MapVerbRecorder(app, verbs));
        using var protocol = new BowireGraphQLProtocol();

        await protocol.InvokeAsync(
            host.BaseUrl + "/graphql", service: "Query", method: "ping",
            jsonMessages: ["""{"query":"query P { ping }"}"""],
            showInternalServices: false, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["POST"], verbs);
    }

    [Fact]
    public async Task A_Mutation_Over_Get_Is_Refused_Before_It_Is_Sent()
    {
        // The guard that makes the feature safe to offer. GraphQL over GET
        // is defined for queries because intermediaries may retry, prefetch
        // and cache a GET -- a mutation behind that verb is a write someone
        // else may decide to repeat. Refused here, so nothing reaches the
        // wire at all.
        var verbs = new List<string>();
        await using var host = await PluginTestHost.StartAsync(app => MapVerbRecorder(app, verbs));
        using var protocol = new BowireGraphQLProtocol();

        var result = await protocol.InvokeAsync(
            host.BaseUrl + "/graphql", service: "Query", method: "echo",
            jsonMessages: ["""{"query":"mutation M { echo }"}"""],
            showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                [BowireGraphQLProtocol.HttpMethodMetadataKey] = "get",
            },
            ct: TestContext.Current.CancellationToken);

        Assert.NotEqual("OK", result.Status);
        Assert.Contains("queries only", result.Status, StringComparison.Ordinal);
        Assert.Empty(verbs);
    }

    [Fact]
    public async Task The_Instruction_Itself_Is_Not_Sent_As_A_Header()
    {
        // It is a word to Bowire, not to the server. Leaving it on the
        // request would put an internal flag into somebody's access log and,
        // worse, into a signed-header calculation.
        var seenHeaders = new List<string>();
        await using var host = await PluginTestHost.StartAsync(app =>
            app.MapMethods("/graphql", ["GET", "POST"], async (HttpContext ctx) =>
            {
                seenHeaders.AddRange(ctx.Request.Headers.Select(h => h.Key));
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("""{ "data": { "ping": "pong" } }""");
            }));
        using var protocol = new BowireGraphQLProtocol();

        await protocol.InvokeAsync(
            host.BaseUrl + "/graphql", service: "Query", method: "ping",
            jsonMessages: ["""{"query":"query P { ping }"}"""],
            showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                [BowireGraphQLProtocol.HttpMethodMetadataKey] = "get",
                ["X-Tenant"] = "harbour",
            },
            ct: TestContext.Current.CancellationToken);

        Assert.Contains("X-Tenant", seenHeaders);
        Assert.DoesNotContain(BowireGraphQLProtocol.HttpMethodMetadataKey, seenHeaders);
    }

    // ---- #713: Automatic Persisted Queries ----

    [Fact]
    public async Task The_First_Call_Sends_A_Hash_And_No_Document()
    {
        // The entire point of APQ. If the document went along on the first
        // leg there would be nothing saved and nothing gained.
        var bodies = new List<string>();
        await using var host = await PluginTestHost.StartAsync(app => MapApqServer(app, bodies, knowsHash: true));
        using var protocol = new BowireGraphQLProtocol();

        var result = await InvokeApqAsync(protocol, host, """{"query":"query P { ping }"}""");

        Assert.Equal("OK", result.Status);
        var only = Assert.Single(bodies);
        Assert.Contains("sha256Hash", only, StringComparison.Ordinal);
        Assert.DoesNotContain("\"query\"", only, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Server_That_Has_Not_Seen_It_Gets_The_Document_On_The_Second_Call()
    {
        // The half without which APQ is worse than useless: a client that
        // stopped here would fail every first call against every server it
        // had not already primed, and the failure would read as the server
        // being broken.
        var bodies = new List<string>();
        await using var host = await PluginTestHost.StartAsync(app => MapApqServer(app, bodies, knowsHash: false));
        using var protocol = new BowireGraphQLProtocol();

        var result = await InvokeApqAsync(protocol, host, """{"query":"query P { ping }"}""");

        Assert.Equal("OK", result.Status);
        Assert.Equal(2, bodies.Count);
        Assert.DoesNotContain("\"query\"", bodies[0], StringComparison.Ordinal);
        // Second leg: document AND hash, so the server can store it and the
        // next call is one request again.
        Assert.Contains("\"query\"", bodies[1], StringComparison.Ordinal);
        Assert.Contains("sha256Hash", bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Server_That_Refuses_Apq_Is_Not_Asked_Twice_Again()
    {
        // PERSISTED_QUERY_NOT_SUPPORTED will not change during a session.
        // Remembering it turns a permanent double request into a single
        // one, so the second call sends the document straight away.
        var bodies = new List<string>();
        await using var host = await PluginTestHost.StartAsync(app => MapApqServer(app, bodies, knowsHash: false, supported: false));
        using var protocol = new BowireGraphQLProtocol();

        await InvokeApqAsync(protocol, host, """{"query":"query P { ping }"}""");
        Assert.Equal(2, bodies.Count);

        await InvokeApqAsync(protocol, host, """{"query":"query P { ping }"}""");
        Assert.Equal(3, bodies.Count);
        Assert.Contains("\"query\"", bodies[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_The_Flag_Nothing_About_The_Request_Changes()
    {
        var bodies = new List<string>();
        await using var host = await PluginTestHost.StartAsync(app => MapApqServer(app, bodies, knowsHash: true));
        using var protocol = new BowireGraphQLProtocol();

        await protocol.InvokeAsync(
            host.BaseUrl + "/graphql", service: "Query", method: "ping",
            jsonMessages: ["""{"query":"query P { ping }"}"""],
            showInternalServices: false, ct: TestContext.Current.CancellationToken);

        var only = Assert.Single(bodies);
        Assert.Contains("\"query\"", only, StringComparison.Ordinal);
        Assert.DoesNotContain("persistedQuery", only, StringComparison.Ordinal);
    }

    private static Task<InvokeResult> InvokeApqAsync(
        BowireGraphQLProtocol protocol, PluginTestHost host, string payload)
        => protocol.InvokeAsync(
            host.BaseUrl + "/graphql", service: "Query", method: "ping",
            jsonMessages: [payload],
            showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                [BowireGraphQLProtocol.PersistedQueryMetadataKey] = "on",
            },
            ct: TestContext.Current.CancellationToken);

    // ---- #713: batching ----

    [Fact]
    public async Task Several_Documents_Go_Out_As_One_Array()
    {
        // InvokeAsync has always taken a LIST of messages and this plugin
        // has always read only the first, dropping the rest without a word.
        // Batching is what makes the plural mean something.
        var bodies = new List<string>();
        await using var host = await PluginTestHost.StartAsync(app => MapBatchServer(app, bodies));
        using var protocol = new BowireGraphQLProtocol();

        var result = await InvokeBatchAsync(protocol, host,
            ["""{"query":"query A { ping }"}""", """{"query":"query B { ping }"}"""]);

        Assert.Equal("OK", result.Status);
        var sent = Assert.Single(bodies);
        Assert.StartsWith("[", sent.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("query A", sent, StringComparison.Ordinal);
        Assert.Contains("query B", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Answers_Come_Back_In_The_Order_They_Were_Sent()
    {
        // There is no id to match an answer to its request -- position is
        // the entire contract. A batch that reordered would hand the
        // caller somebody else's data with no way to notice.
        var bodies = new List<string>();
        await using var host = await PluginTestHost.StartAsync(app => MapBatchServer(app, bodies));
        using var protocol = new BowireGraphQLProtocol();

        var result = await InvokeBatchAsync(protocol, host,
            ["""{"query":"query First { ping }"}""", """{"query":"query Second { ping }"}"""]);

        using var doc = JsonDocument.Parse(result.Response!);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        var answers = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("data").GetProperty("echo").GetString()).ToArray();
        Assert.Equal(["First", "Second"], answers);
    }

    [Fact]
    public async Task A_Single_Document_Batches_Too_Rather_Than_Being_A_Special_Case()
    {
        // A caller that asked for a batch gets one, even of size one. The
        // alternative -- quietly sending a bare object instead -- would
        // make the response shape depend on the count, which is the kind of
        // thing that breaks a caller's parser on a slow day.
        var bodies = new List<string>();
        await using var host = await PluginTestHost.StartAsync(app => MapBatchServer(app, bodies));
        using var protocol = new BowireGraphQLProtocol();

        var result = await InvokeBatchAsync(protocol, host, ["""{"query":"query Only { ping }"}"""]);

        Assert.StartsWith("[", Assert.Single(bodies).TrimStart(), StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(result.Response!);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task Without_The_Flag_The_Body_Is_A_Single_Object_As_Before()
    {
        var bodies = new List<string>();
        await using var host = await PluginTestHost.StartAsync(app => MapBatchServer(app, bodies));
        using var protocol = new BowireGraphQLProtocol();

        await protocol.InvokeAsync(
            host.BaseUrl + "/graphql", service: "Query", method: "ping",
            jsonMessages: ["""{"query":"query A { ping }"}"""],
            showInternalServices: false, ct: TestContext.Current.CancellationToken);

        Assert.StartsWith("{", Assert.Single(bodies).TrimStart(), StringComparison.Ordinal);
    }

    private static Task<InvokeResult> InvokeBatchAsync(
        BowireGraphQLProtocol protocol, PluginTestHost host, List<string> messages)
        => protocol.InvokeAsync(
            host.BaseUrl + "/graphql", service: "Query", method: "ping",
            jsonMessages: messages,
            showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                [BowireGraphQLProtocol.BatchMetadataKey] = "on",
            },
            ct: TestContext.Current.CancellationToken);

    // ---- #713: file uploads ----

    [Fact]
    public async Task A_Message_With_Files_Goes_Out_As_Multipart()
    {
        // No flag for this one: a caller either has bytes to send or does
        // not. Asking them to say so twice is a way to get it wrong.
        string? contentType = null;
        var parts = new List<string>();
        await using var host = await PluginTestHost.StartAsync(app =>
            app.MapPost("/graphql", async (HttpContext ctx) =>
            {
                contentType = ctx.Request.ContentType;
                if (ctx.Request.HasFormContentType)
                {
                    var form = await ctx.Request.ReadFormAsync();
                    parts.AddRange(form.Keys);
                    parts.AddRange(form.Files.Select(f => "file:" + f.Name + ":" + f.FileName));
                }
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("""{ "data": { "upload": "ok" } }""");
            }));
        using var protocol = new BowireGraphQLProtocol();

        var result = await protocol.InvokeAsync(
            host.BaseUrl + "/graphql", service: "Mutation", method: "upload",
            jsonMessages: ["""
                {"query":"mutation Up($file: Upload!) { upload(file: $file) }",
                 "variables":{"file":null},
                 "files":[{"variablePath":"variables.file","name":"chart.png","contentType":"image/png","base64":"aGVsbG8="}]}
                """],
            showInternalServices: false, ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.StartsWith("multipart/form-data", contentType!, StringComparison.Ordinal);
        Assert.Contains("operations", parts);
        Assert.Contains("map", parts);
        Assert.Contains("file:0:chart.png", parts);
    }

    [Fact]
    public async Task Without_Files_The_Body_Stays_Plain_Json()
    {
        string? contentType = null;
        await using var host = await PluginTestHost.StartAsync(app =>
            app.MapPost("/graphql", async (HttpContext ctx) =>
            {
                contentType = ctx.Request.ContentType;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("""{ "data": { "ping": "pong" } }""");
            }));
        using var protocol = new BowireGraphQLProtocol();

        await protocol.InvokeAsync(
            host.BaseUrl + "/graphql", service: "Query", method: "ping",
            jsonMessages: ["""{"query":"query P { ping }"}"""],
            showInternalServices: false, ct: TestContext.Current.CancellationToken);

        Assert.StartsWith("application/json", contentType!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Files_Over_Get_Are_Refused_Rather_Than_Dropped()
    {
        // A multipart body has no URL form. Silently sending the operation
        // without its files would look like it worked and produce a row
        // with a null where a document should be.
        var reached = 0;
        await using var host = await PluginTestHost.StartAsync(app =>
            app.MapMethods("/graphql", ["GET", "POST"], (HttpContext ctx) =>
            {
                Interlocked.Increment(ref reached);
                return Results.Json(new { data = new { ok = true } });
            }));
        using var protocol = new BowireGraphQLProtocol();

        var result = await protocol.InvokeAsync(
            host.BaseUrl + "/graphql", service: "Query", method: "upload",
            jsonMessages: ["""
                {"query":"query Up($file: Upload!) { upload(file: $file) }",
                 "files":[{"variablePath":"variables.file","base64":"aGVsbG8="}]}
                """],
            showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                [BowireGraphQLProtocol.HttpMethodMetadataKey] = "get",
            },
            ct: TestContext.Current.CancellationToken);

        Assert.NotEqual("OK", result.Status);
        Assert.Contains("cannot carry file uploads", result.Status, StringComparison.Ordinal);
        Assert.Equal(0, reached);
    }

    /// <summary>
    /// A /graphql that answers an array with an array, echoing each entry's
    /// operation name so ordering is observable.
    /// </summary>
    private static void MapBatchServer(WebApplication app, List<string> bodies)
    {
        app.MapPost("/graphql", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            bodies.Add(body);
            ctx.Response.ContentType = "application/json";

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                await ctx.Response.WriteAsync("""{ "data": { "ping": "pong" } }""");
                return;
            }

            var answers = doc.RootElement.EnumerateArray().Select(entry =>
            {
                var query = entry.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
                var name = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault() ?? "";
                return $$"""{ "data": { "echo": "{{name}}" } }""";
            });
            await ctx.Response.WriteAsync("[" + string.Join(",", answers) + "]");
        });
    }

    /// <summary>
    /// A /graphql that behaves like an APQ server: records every body, and
    /// answers a hash-only request either from its store or with the miss
    /// the convention defines.
    /// </summary>
    private static void MapApqServer(
        WebApplication app, List<string> bodies, bool knowsHash, bool supported = true)
    {
        app.MapPost("/graphql", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            bodies.Add(body);

            var hasDocument = body.Contains("\"query\"", StringComparison.Ordinal);
            ctx.Response.ContentType = "application/json";

            if (!hasDocument && !knowsHash)
            {
                var code = supported ? "PERSISTED_QUERY_NOT_FOUND" : "PERSISTED_QUERY_NOT_SUPPORTED";
                await ctx.Response.WriteAsync(
                    $$"""{ "errors": [{ "message": "miss", "extensions": { "code": "{{code}}" } }] }""");
                return;
            }

            await ctx.Response.WriteAsync("""{ "data": { "ping": "pong" } }""");
        });
    }

    /// <summary>A /graphql that answers either verb and records which it got.</summary>
    private static void MapVerbRecorder(WebApplication app, List<string> verbs)
    {
        app.MapMethods("/graphql", ["GET", "POST"], async (HttpContext ctx) =>
        {
            verbs.Add(ctx.Request.Method);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{ "data": { "ping": "pong" } }""");
        });
    }

    private static Task<InvokeResult> InvokeVerbatimAsync(
        BowireGraphQLProtocol protocol, PluginTestHost host, string payload)
        => protocol.InvokeAsync(
            host.BaseUrl + "/graphql",
            service: "Query",
            method: "whichOperation",
            jsonMessages: [payload],
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);

    private static void MapGraphQLEndpoint(WebApplication app)
    {
        app.MapPost("/graphql", async (HttpContext ctx) =>
        {
            var request = await JsonSerializer.DeserializeAsync<GraphQLRequest>(ctx.Request.Body, s_caseInsensitive);
            if (request?.Query is null)
            {
                ctx.Response.StatusCode = 400;
                return;
            }

            string responseJson;
            if (request.Query.Contains("__schema", StringComparison.Ordinal))
            {
                responseJson = IntrospectionResponse;
            }
            else if (request.Query.Contains("ping", StringComparison.Ordinal))
            {
                responseJson = """{ "data": { "ping": "pong" } }""";
            }
            else if (request.Query.Contains("echo", StringComparison.Ordinal))
            {
                var text = request.Variables.TryGetValue("text", out var t) ? t.GetString() ?? "" : "";
                responseJson = $$"""{ "data": { "echo": "{{text}}" } }""";
            }
            else if (request.Query.Contains("whichOperation", StringComparison.Ordinal))
            {
                // #710 — the server answers with what it was told to run.
                // A GraphQL server with several operations in one document
                // cannot proceed without this, so echoing it is the only
                // way to prove it arrived.
                responseJson = request.OperationName is null
                    ? """{ "data": { "whichOperation": null } }"""
                    : $$"""{ "data": { "whichOperation": "{{request.OperationName}}" } }""";
            }
            else
            {
                responseJson = """{ "errors": [{ "message": "unknown operation" }] }""";
            }

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(responseJson);
        });
    }

    private sealed class GraphQLRequest
    {
        public string? Query { get; set; }
        public Dictionary<string, JsonElement> Variables { get; set; } = new();

        /// <summary>#710 — what the server is now told, and was not before.</summary>
        public string? OperationName { get; set; }
    }

    private const string IntrospectionResponse = """
    {
      "data": {
        "__schema": {
          "queryType": { "name": "Query" },
          "mutationType": { "name": "Mutation" },
          "subscriptionType": null,
          "types": [
            {
              "kind": "OBJECT",
              "name": "Query",
              "fields": [
                {
                  "name": "ping",
                  "args": [],
                  "type": { "kind": "SCALAR", "name": "String", "ofType": null }
                }
              ],
              "inputFields": null,
              "enumValues": null
            },
            {
              "kind": "OBJECT",
              "name": "Mutation",
              "fields": [
                {
                  "name": "echo",
                  "args": [
                    {
                      "name": "text",
                      "type": {
                        "kind": "NON_NULL",
                        "name": null,
                        "ofType": { "kind": "SCALAR", "name": "String", "ofType": null }
                      }
                    }
                  ],
                  "type": { "kind": "SCALAR", "name": "String", "ofType": null }
                }
              ],
              "inputFields": null,
              "enumValues": null
            }
          ]
        }
      }
    }
    """;
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kuestenlogik.Bowire.Ai;
using Kuestenlogik.Bowire.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// In-process tests for <c>POST /api/ai/fuzz-values</c> (#62). Drives
/// the endpoint via a TestServer with a stub <see cref="IChatClient"/>
/// returning canned envelopes, covering happy path JSON, markdown-
/// fence recovery, garbage fallback, severity normalisation (the
/// "user classifies, not the model" guarantee), 20-row cap, mixed-
/// type values, malformed row skip, and the 503 / 400 error paths.
/// </summary>
[Collection("BowireUserContext")]
public sealed class BowireAiFuzzValuesEndpointTests : IDisposable
{
    private readonly IBowireUserStore _originalStore;
    private readonly string _tempRoot;

    public BowireAiFuzzValuesEndpointTests()
    {
        _originalStore = BowireUserContext.Current;
        _tempRoot = Path.Combine(Path.GetTempPath(), $"bowire-fuzz-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        BowireUserContext.Current = new TempStore(_tempRoot);
    }

    public void Dispose()
    {
        BowireUserContext.Current = _originalStore;
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task FuzzValues_Returns_503_When_No_IChatClient_Registered()
    {
        using var host = BuildHostWithoutClient();
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/fuzz-values",
            new { fieldName = "id", fieldType = "int32" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task FuzzValues_Returns_400_For_Missing_FieldName_Or_Type()
    {
        using var host = BuildHostWithStub("""{"values":[]}""");
        using var client = host.GetTestClient();

        var noName = await client.PostAsJsonAsync("/api/ai/fuzz-values",
            new { fieldType = "int32" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);

        var noType = await client.PostAsJsonAsync("/api/ai/fuzz-values",
            new { fieldName = "id" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, noType.StatusCode);
    }

    [Fact]
    public async Task FuzzValues_Parses_Clean_Json_Envelope()
    {
        const string Canned = """
        {
          "values": [
            {"value": 0, "why": "boundary: zero", "severity": "info"},
            {"value": -1, "why": "boundary: negative on uint-shaped field", "severity": "low"},
            {"value": 2147483648, "why": "int32 overflow", "severity": "medium"}
          ]
        }
        """;
        using var host = BuildHostWithStub(Canned);
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/fuzz-values",
            new { fieldName = "id", fieldType = "int32" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<FuzzValuesResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(3, body!.Values.Length);
        Assert.Equal(JsonValueKind.Number, body.Values[0].Value.ValueKind);
        Assert.Equal("info", body.Values[0].Severity);
        Assert.Equal("medium", body.Values[2].Severity);
    }

    [Fact]
    public async Task FuzzValues_Survives_Mixed_Type_Values()
    {
        // The endpoint passes JsonElement through; a model that mixes
        // strings + numbers + null in one batch shouldn't break the
        // parser. Critical for messaging payloads where the same
        // "field" can semantically accept multiple JSON kinds.
        const string Mixed = """
        {
          "values": [
            {"value": "", "why": "empty string", "severity": "info"},
            {"value": null, "why": "explicit null", "severity": "low"},
            {"value": true, "why": "wrong-type coercion", "severity": "low"},
            {"value": "9999999999999999999999", "why": "stringified overflow", "severity": "medium"}
          ]
        }
        """;
        using var host = BuildHostWithStub(Mixed);
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/fuzz-values",
            new { fieldName = "amount", fieldType = "double" },
            TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<FuzzValuesResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(4, body!.Values.Length);
        Assert.Equal(JsonValueKind.String, body.Values[0].Value.ValueKind);
        Assert.Equal(JsonValueKind.Null, body.Values[1].Value.ValueKind);
        Assert.Equal(JsonValueKind.True, body.Values[2].Value.ValueKind);
        Assert.Equal(JsonValueKind.String, body.Values[3].Value.ValueKind);
    }

    [Fact]
    public async Task FuzzValues_Recovers_When_Model_Wraps_Json_In_Markdown()
    {
        const string Wrapped = """
        Here are some boundary values:

        ```json
        {"values": [{"value": "<script>", "why": "tag fragment", "severity": "low"}]}
        ```
        """;
        using var host = BuildHostWithStub(Wrapped);
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/fuzz-values",
            new { fieldName = "name", fieldType = "string" },
            TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<FuzzValuesResponse>(TestContext.Current.CancellationToken);
        var row = Assert.Single(body!.Values);
        Assert.Equal("<script>", row.Value.GetString());
    }

    [Fact]
    public async Task FuzzValues_Falls_Back_To_Empty_For_Garbage()
    {
        using var host = BuildHostWithStub("can't generate these");
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/fuzz-values",
            new { fieldName = "x", fieldType = "string" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<FuzzValuesResponse>(TestContext.Current.CancellationToken);
        Assert.Empty(body!.Values);
    }

    [Fact]
    public async Task FuzzValues_Caps_Output_At_20_Rows()
    {
        // Build a 30-row envelope. The endpoint must trim to 20 so a
        // hallucinating model can't blow the frontend's picker.
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"values\":[");
        for (var i = 0; i < 30; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"value\":").Append(i).Append(",\"why\":\"r").Append(i).Append("\",\"severity\":\"info\"}");
        }
        sb.Append("]}");
        using var host = BuildHostWithStub(sb.ToString());
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/fuzz-values",
            new { fieldName = "x", fieldType = "int32" },
            TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<FuzzValuesResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(20, body!.Values.Length);
    }

    [Fact]
    public async Task FuzzValues_Caps_Severity_At_Medium()
    {
        // The "user classifies, not the model" guarantee: a model that
        // says "critical" or "high" still surfaces as medium so the
        // workbench's UI never marks a fuzz value as auto-finding.
        const string HighSeverity = """
        {
          "values": [
            {"value": "DROP TABLE users", "why": "obviously malicious", "severity": "critical"},
            {"value": 1, "why": "ok", "severity": "high"}
          ]
        }
        """;
        using var host = BuildHostWithStub(HighSeverity);
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/fuzz-values",
            new { fieldName = "name", fieldType = "string" },
            TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<FuzzValuesResponse>(TestContext.Current.CancellationToken);
        Assert.Equal("medium", body!.Values[0].Severity);
        Assert.Equal("medium", body.Values[1].Severity);
    }

    [Fact]
    public async Task FuzzValues_Skips_Rows_Without_Value_Property()
    {
        // Malformed rows (missing "value") get dropped so one bad
        // entry doesn't lose the rest of the batch.
        const string Partial = """
        {
          "values": [
            {"value": "ok", "why": "valid", "severity": "info"},
            {"why": "no value field", "severity": "low"},
            {"value": 42, "why": "also valid", "severity": "info"}
          ]
        }
        """;
        using var host = BuildHostWithStub(Partial);
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/fuzz-values",
            new { fieldName = "x", fieldType = "string" },
            TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<FuzzValuesResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(2, body!.Values.Length);
        Assert.Equal("ok", body.Values[0].Value.GetString());
        Assert.Equal(42, body.Values[1].Value.GetInt32());
    }

    private static IHost BuildHostWithStub(string canned)
    {
#pragma warning disable CA2000
        var chatClient = new StubChatClient(canned);
#pragma warning restore CA2000
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireAiEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s =>
                   {
                       s.AddRouting();
                       s.AddSingleton<IChatClient>(chatClient);
                       s.AddBowireAi(new ConfigurationBuilder().Build());
                   });
            })
            .Start();
    }

    private static IHost BuildHostWithoutClient()
    {
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowireAiEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s =>
                   {
                       s.AddRouting();
                       s.AddSingleton(new BowireAiOptions());
                       s.AddSingleton(sp => new BowireAiRuntime(sp.GetRequiredService<BowireAiOptions>()));
                   });
            })
            .Start();
    }

    private sealed record FuzzValuesResponse(FuzzValueRow[] Values);
    private sealed record FuzzValueRow(JsonElement Value, string Why, string Severity);

    private sealed class StubChatClient(string responseText) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TempStore(string root) : IBowireUserStore
    {
        public string GetUserPath(string filename) => Path.Combine(root, filename);
    }
}

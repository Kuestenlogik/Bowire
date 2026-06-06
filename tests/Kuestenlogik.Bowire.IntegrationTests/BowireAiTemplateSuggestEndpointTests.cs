// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
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
/// In-process tests for <c>POST /api/ai/template-suggest</c> +
/// <c>POST /api/ai/template-save</c> (#60). Drives the endpoints
/// via a TestServer with a stub <see cref="IChatClient"/> returning
/// canned YAML, covering happy path + YAML-fence extraction + class
/// validation + filename-traversal guard + save round-trip.
/// </summary>
[Collection("BowireUserContext")]
public sealed class BowireAiTemplateSuggestEndpointTests : IDisposable
{
    private readonly IBowireUserStore _originalStore;
    private readonly string _tempRoot;

    public BowireAiTemplateSuggestEndpointTests()
    {
        _originalStore = BowireUserContext.Current;
        _tempRoot = Path.Combine(Path.GetTempPath(), $"bowire-tpl-test-{Guid.NewGuid():N}");
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
    public async Task Suggest_Returns_503_When_No_IChatClient_Registered()
    {
        using var host = BuildHostWithoutClient();
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/template-suggest",
            new { path = "/api/orders/{id}", @class = "idor" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task Suggest_Returns_400_For_Missing_Path()
    {
        using var host = BuildHostWithStub("id: test");
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/template-suggest",
            new { @class = "idor" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Suggest_Returns_400_For_Unknown_Class()
    {
        using var host = BuildHostWithStub("id: test");
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/template-suggest",
            new { path = "/x", @class = "totally-made-up-class" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("supported", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Suggest_Returns_Yaml_For_Known_Class()
    {
        const string CannedYaml = """
            id: bowire-ai-idor-orders
            info:
              name: IDOR probe on /api/orders/{id}
              author: bowire-ai
              severity: high
            http:
              - method: GET
                path:
                  - "{{BaseURL}}/api/orders/1"
            """;
        using var host = BuildHostWithStub(CannedYaml);
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/template-suggest",
            new { path = "/api/orders/{id}", @class = "idor", verb = "GET", protocol = "rest" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<SuggestResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Contains("id: bowire-ai-idor-orders", body!.Yaml);
        Assert.EndsWith(".yaml", body.SuggestedFilename);
    }

    [Fact]
    public async Task Suggest_Strips_Markdown_Fences()
    {
        const string Wrapped = """
            Sure, here's the template:

            ```yaml
            id: stripped-fences
            info:
              name: ok
            ```

            Hope that helps!
            """;
        using var host = BuildHostWithStub(Wrapped);
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/template-suggest",
            new { path = "/x", @class = "idor" },
            TestContext.Current.CancellationToken);

        var body = await resp.Content.ReadFromJsonAsync<SuggestResponse>(TestContext.Current.CancellationToken);
        Assert.Equal("id: stripped-fences\ninfo:\n  name: ok", body!.Yaml.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Save_Round_Trips_Yaml_To_User_Store()
    {
        // CA2000: TempStore lifetime is the test fixture's.
        using var host = BuildHostWithoutClient();   // /save doesn't need a client
        using var client = host.GetTestClient();
        const string Yaml = "id: round-trip\ninfo:\n  name: ok";

        var resp = await client.PostAsJsonAsync("/api/ai/template-save",
            new { filename = "bowire-ai-test.yaml", yaml = Yaml },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<SaveResponse>(TestContext.Current.CancellationToken);
        Assert.True(body!.Saved);
        Assert.True(File.Exists(body.Path));
        Assert.Equal(Yaml, await File.ReadAllTextAsync(body.Path, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("../escape.yaml")]
    [InlineData("subdir/x.yaml")]
    [InlineData("x.exe")]
    [InlineData("")]
    public async Task Save_Rejects_Path_Traversal_And_Bad_Extensions(string filename)
    {
        using var host = BuildHostWithoutClient();
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/template-save",
            new { filename, yaml = "id: x" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Save_Accepts_Yml_Extension_Too()
    {
        // .yml is just as valid as .yaml; refusing it would be petty
        // and frustrate users who hand-author templates.
        using var host = BuildHostWithoutClient();
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync("/api/ai/template-save",
            new { filename = "ok.yml", yaml = "id: x" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
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

    private sealed record SuggestResponse(string Yaml, string SuggestedFilename, string? ModelId);
    private sealed record SaveResponse(bool Saved, string Path);

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

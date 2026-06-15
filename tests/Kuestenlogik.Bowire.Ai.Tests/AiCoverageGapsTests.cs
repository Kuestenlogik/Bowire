// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

// CA1849: file IO calls are intentionally synchronous in test bodies —
//   the temp store paths sit on local disk and the production code under
//   test is itself synchronous (BowireAiUserConfigStore + AppendAuditLog
//   use sync File.* APIs).
// CA1861: anonymous-object payloads in the request bodies use array
//   literals; promoting them to static readonly fields would lose the
//   anonymous-record inference the JSON serializer needs.
// xUnit1051: AIFunction.InvokeAsync overloads accept a CancellationToken
//   but the function bodies under test never block on it; passing
//   TestContext's token would be noise.
#pragma warning disable CA1849, CA1861, xUnit1051

using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Ai.Tests;

/// <summary>
/// Coverage-gap tests filling in branches the original five test files
/// missed (the AI-helper endpoints — triage, threat-model, template-suggest,
/// template-save, fuzz-values — plus the chat tool-call flow, the probe
/// happy paths, the host-managed config + delete branches, and the
/// runtime error paths that surface ProblemDetails). Sits in its own
/// file so the existing suites stay intact while the new pins push the
/// assembly's measured coverage past 90%.
/// </summary>
[Collection("BowireUserContext")]
public sealed class AiCoverageGapsTests : IDisposable
{
    private readonly IBowireUserStore _originalStore;
    private readonly string _tempRoot;

    public AiCoverageGapsTests()
    {
        _originalStore = BowireUserContext.Current;
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"bowire-ai-gaps-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        BowireUserContext.Current = new TempUserStore(_tempRoot);
    }

    public void Dispose()
    {
        BowireUserContext.Current = _originalStore;
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    // ----- /api/ai/triage --------------------------------------------

    [Fact]
    public async Task Triage_NoClient_Returns503_WithStructuredError()
    {
        using var host = BuildHostWithoutChatClient();
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/triage",
            new { title = "test" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Contains("IChatClient", body.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Triage_InvalidJson_Returns400()
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(new RecordingChatClient("{\"realScore\":80}"));
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/triage")
        {
            Content = new StringContent("{not valid", Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Contains("JSON", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Triage_MissingTitle_Returns400()
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(new RecordingChatClient("ignored"));
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/triage",
            new { title = "" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Contains("title", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Triage_HappyPath_ParsesVerdict_And_EchoesRaw()
    {
        // Model returns a fenced JSON verdict — TryParseVerdict slices
        // out the outermost {…} block and clamps realScore into [0,100].
        var modelOutput = """
            Sure, here you go:
            {
              "realScore": 250,
              "reasoning": "evidence is strong",
              "fix": "validate input"
            }
            """;
        using var stub = new RecordingChatClient(modelOutput, modelId: "test-model");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/triage",
            new
            {
                title = "Possible IDOR on /pets/{id}",
                category = "idor",
                evidence = "GET /pets/5 returned another user's pet",
                method = "GET",
                endpoint = "/pets/{id}",
                statusCode = "200",
                protocol = "rest",
                notes = "Discovered via fuzz",
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        // realScore is clamped: 250 → 100.
        Assert.Equal(100, body.GetProperty("realScore").GetInt32());
        Assert.Equal("evidence is strong", body.GetProperty("reasoning").GetString());
        Assert.Equal("validate input", body.GetProperty("fix").GetString());
        Assert.Equal("test-model", body.GetProperty("modelId").GetString());
        // The user prompt should have included the prompt fields.
        Assert.NotNull(stub.LastMessages);
        var userPrompt = stub.LastMessages!.Last(m => m.Role == ChatRole.User).Text;
        Assert.Contains("idor", userPrompt, StringComparison.Ordinal);
        Assert.Contains("Possible IDOR", userPrompt, StringComparison.Ordinal);
        Assert.Contains("GET /pets/5", userPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Triage_LongEvidence_IsTruncatedInPrompt()
    {
        // Evidence longer than 4 KB collapses to a 4 KB head + a
        // "[truncated]" marker so the prompt stays bounded.
        var longEvidence = new string('A', 5000);
        using var stub = new RecordingChatClient("""{"realScore":40,"reasoning":"weak","fix":""}""");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/triage",
            new { title = "t", evidence = longEvidence },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var userPrompt = stub.LastMessages!.Last(m => m.Role == ChatRole.User).Text;
        Assert.Contains("[truncated]", userPrompt, StringComparison.Ordinal);
        // Prompt body shouldn't carry all 5000 A's verbatim.
        Assert.DoesNotContain(new string('A', 4500), userPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Triage_MalformedJsonResponse_FallsBack_To50_Default_Verdict()
    {
        // Model returns prose with no JSON braces at all.
        using var stub = new RecordingChatClient("I don't know what to say here.");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/triage",
            new { title = "t" },
            TestContext.Current.CancellationToken);

        var body = await ReadJsonAsync(resp);
        Assert.Equal(50, body.GetProperty("realScore").GetInt32());
        Assert.Contains("don't know", body.GetProperty("reasoning").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Triage_EmptyModelResponse_FallsBackToManualReviewMessage()
    {
        using var stub = new RecordingChatClient("");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/triage",
            new { title = "t" },
            TestContext.Current.CancellationToken);

        var body = await ReadJsonAsync(resp);
        Assert.Equal(50, body.GetProperty("realScore").GetInt32());
        Assert.Contains("manual review", body.GetProperty("reasoning").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Triage_LongPlainTextResponse_TruncatesReasoning()
    {
        // No braces, > 240 chars — TryParseVerdict caps the reasoning
        // at 240 chars + ellipsis.
        var longText = new string('X', 500);
        using var stub = new RecordingChatClient(longText);
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/triage",
            new { title = "t" },
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);
        var reasoning = body.GetProperty("reasoning").GetString();
        Assert.NotNull(reasoning);
        Assert.EndsWith("…", reasoning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Triage_ProviderException_Returns502_WithExceptionType()
    {
        using var stub = new ThrowingChatClient(new InvalidOperationException("provider blew up"));
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/triage",
            new { title = "t" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Equal("provider blew up", body.GetProperty("error").GetString());
        Assert.Equal("InvalidOperationException", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Triage_Canceled_Returns499()
    {
        using var stub = new ThrowingChatClient(new OperationCanceledException("cancelled"));
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/triage",
            new { title = "t" },
            TestContext.Current.CancellationToken);

        Assert.Equal(499, (int)resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Equal("canceled", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Triage_GarbageBracesResponse_FallsBack_To50()
    {
        // JSON parse fails inside the brace slice — TryParseVerdict
        // falls back to the "couldn't parse" verdict (score 50,
        // raw text in reasoning).
        using var stub = new RecordingChatClient("{not parseable}");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/triage",
            new { title = "t" },
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);
        Assert.Equal(50, body.GetProperty("realScore").GetInt32());
        Assert.Contains("not parseable", body.GetProperty("reasoning").GetString(), StringComparison.Ordinal);
    }

    // ----- /api/ai/threat-model ---------------------------------------

    [Fact]
    public async Task ThreatModel_NoClient_Returns503()
    {
        using var host = BuildHostWithoutChatClient();
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/threat-model",
            new { endpoints = new[] { new { endpointId = "a", path = "/a" } } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task ThreatModel_InvalidJson_Returns400()
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(new RecordingChatClient(""));
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/threat-model")
        {
            Content = new StringContent("not json", Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ThreatModel_EmptyEndpointsArray_Returns400()
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(new RecordingChatClient(""));
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/threat-model",
            new { endpoints = Array.Empty<object>() },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Contains("endpoints", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThreatModel_HappyPath_ReturnsRankedRows()
    {
        var modelOutput = """
            {
              "ranked": [
                {"endpointId":"a","risk":7,"why":"writes user data","suggestedTemplates":["mass-assignment","idor"]},
                {"endpointId":"b","risk":11,"why":"clamp this","suggestedTemplates":[]}
              ]
            }
            """;
        using var stub = new RecordingChatClient(modelOutput, modelId: "rank-model");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/threat-model",
            new
            {
                endpoints = new[]
                {
                    new { endpointId = "a", path = "/pets/{id}", verb = "PATCH", protocol = "rest", service = "pets", inputShape = (string?)"{id,name}", authState = (string?)"authenticated" },
                    new { endpointId = "b", path = "/health", verb = "GET", protocol = "rest", service = "core", inputShape = (string?)null, authState = (string?)null },
                },
                topN = 5,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        var ranked = body.GetProperty("ranked");
        Assert.Equal(2, ranked.GetArrayLength());
        Assert.Equal("a", ranked[0].GetProperty("endpointId").GetString());
        Assert.Equal(7, ranked[0].GetProperty("risk").GetInt32());
        Assert.Equal(2, ranked[0].GetProperty("suggestedTemplates").GetArrayLength());
        // 11 clamped to 10.
        Assert.Equal(10, ranked[1].GetProperty("risk").GetInt32());
        Assert.Equal(2, body.GetProperty("inputCount").GetInt32());
        Assert.False(body.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task ThreatModel_TruncatesAt200_AndReportsTruncated()
    {
        var endpoints = Enumerable.Range(0, 250).Select(i => new
        {
            endpointId = $"e{i}",
            path = $"/p{i}",
            inputShape = new string('z', 300),  // exercises the 200-char input-shape truncate
        }).ToArray();
        using var stub = new RecordingChatClient("""{"ranked":[]}""");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/threat-model",
            new { endpoints },
            TestContext.Current.CancellationToken);

        var body = await ReadJsonAsync(resp);
        Assert.Equal(200, body.GetProperty("inputCount").GetInt32());
        Assert.True(body.GetProperty("truncated").GetBoolean());
        // The prompt should have only 200 ids; pick a high one not in the slice.
        var userPrompt = stub.LastMessages!.Last(m => m.Role == ChatRole.User).Text;
        Assert.Contains("e0", userPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("e249", userPrompt, StringComparison.Ordinal);
        // The "…" continuation marker appears because the input-shape was 300 chars.
        Assert.Contains("…", userPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThreatModel_GarbageResponse_ReturnsEmptyRanked()
    {
        // The body has braces but inside they're malformed → JsonException
        // path → empty ranking, not a 500.
        using var stub = new RecordingChatClient("preamble {ranked: not-json} trailing");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/threat-model",
            new { endpoints = new[] { new { endpointId = "a", path = "/a" } } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Equal(0, body.GetProperty("ranked").GetArrayLength());
    }

    [Fact]
    public async Task ThreatModel_NoBracesResponse_ReturnsEmptyRanked()
    {
        using var stub = new RecordingChatClient("totally unrelated prose");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/threat-model",
            new { endpoints = new[] { new { endpointId = "a", path = "/a" } } },
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);
        Assert.Equal(0, body.GetProperty("ranked").GetArrayLength());
    }

    [Fact]
    public async Task ThreatModel_ResponseHasNonArrayRanked_ReturnsEmpty()
    {
        // "ranked" exists but isn't an array → falls through to empty.
        using var stub = new RecordingChatClient("""{"ranked": "should be an array"}""");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/threat-model",
            new { endpoints = new[] { new { endpointId = "a", path = "/a" } } },
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);
        Assert.Equal(0, body.GetProperty("ranked").GetArrayLength());
    }

    [Fact]
    public async Task ThreatModel_RowsWithoutEndpointId_AreSkipped()
    {
        var modelOutput = """
            {
              "ranked": [
                {"risk":5,"why":"no id"},
                "not an object",
                {"endpointId":"keep","risk":3,"why":"kept","suggestedTemplates":["ok",""]}
              ]
            }
            """;
        using var stub = new RecordingChatClient(modelOutput);
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/threat-model",
            new { endpoints = new[] { new { endpointId = "x", path = "/x" } } },
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);
        var ranked = body.GetProperty("ranked");
        Assert.Equal(1, ranked.GetArrayLength());
        Assert.Equal("keep", ranked[0].GetProperty("endpointId").GetString());
        // Empty suggested template strings are filtered out by the parser.
        Assert.Equal(1, ranked[0].GetProperty("suggestedTemplates").GetArrayLength());
        Assert.Equal("ok", ranked[0].GetProperty("suggestedTemplates")[0].GetString());
    }

    [Fact]
    public async Task ThreatModel_TopNCapsTheRows()
    {
        // 3 rows in the model output, topN=1 → only the first kept.
        var modelOutput = """
            {"ranked":[
              {"endpointId":"a","risk":1,"why":"x","suggestedTemplates":[]},
              {"endpointId":"b","risk":2,"why":"y","suggestedTemplates":[]},
              {"endpointId":"c","risk":3,"why":"z","suggestedTemplates":[]}
            ]}
            """;
        using var stub = new RecordingChatClient(modelOutput);
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/threat-model",
            new { endpoints = new[] { new { endpointId = "a", path = "/a" } }, topN = 1 },
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);
        Assert.Equal(1, body.GetProperty("ranked").GetArrayLength());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(51)]
    public async Task ThreatModel_InvalidTopN_FallsBackTo10(int badTopN)
    {
        // topN <= 0 or > 50 collapses to default 10.
        var rows = string.Join(",", Enumerable.Range(0, 15)
            .Select(i => $"{{\"endpointId\":\"e{i}\",\"risk\":1,\"why\":\"\",\"suggestedTemplates\":[]}}"));
        using var stub = new RecordingChatClient($"{{\"ranked\":[{rows}]}}");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/threat-model",
            new { endpoints = new[] { new { endpointId = "x", path = "/x" } }, topN = badTopN },
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);
        Assert.Equal(10, body.GetProperty("ranked").GetArrayLength());
    }

    [Fact]
    public async Task ThreatModel_ProviderException_Returns502()
    {
        using var stub = new ThrowingChatClient(new HttpRequestException("upstream down"));
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/threat-model",
            new { endpoints = new[] { new { endpointId = "a", path = "/a" } } },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Equal("HttpRequestException", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ThreatModel_Canceled_Returns499()
    {
        using var stub = new ThrowingChatClient(new OperationCanceledException());
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/threat-model",
            new { endpoints = new[] { new { endpointId = "a", path = "/a" } } },
            TestContext.Current.CancellationToken);
        Assert.Equal(499, (int)resp.StatusCode);
    }

    // ----- /api/ai/template-suggest -----------------------------------

    [Fact]
    public async Task TemplateSuggest_NoClient_Returns503()
    {
        using var host = BuildHostWithoutChatClient();
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/template-suggest",
            new { path = "/x", @class = "idor" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task TemplateSuggest_InvalidJson_Returns400()
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(new RecordingChatClient(""));
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/template-suggest")
        {
            Content = new StringContent("bogus", Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData("", "idor")]
    [InlineData("/x", "")]
    [InlineData("   ", "idor")]
    public async Task TemplateSuggest_MissingFields_Returns400(string path, string cls)
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(new RecordingChatClient(""));
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/template-suggest",
            new { path, @class = cls },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task TemplateSuggest_UnknownClass_Returns400_ListsSupported()
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(new RecordingChatClient(""));
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/template-suggest",
            new { path = "/x", @class = "nonsense-class" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        var err = body.GetProperty("error").GetString();
        Assert.Contains("unknown class", err, StringComparison.OrdinalIgnoreCase);
        // The full list is surfaced so the client UI can show a hint.
        Assert.Contains("idor", err, StringComparison.Ordinal);
        Assert.Contains("ssrf", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TemplateSuggest_HappyPath_ExtractsFencedYaml()
    {
        // Model wraps YAML in ```yaml … ``` fences — ExtractYaml strips
        // them and the response surfaces the inner YAML, the raw text,
        // and a deterministic suggestedFilename.
        var yaml = "id: my-idor-probe\ninfo:\n  name: IDOR";
        using var stub = new RecordingChatClient($"```yaml\n{yaml}\n```", modelId: "yml-model");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/template-suggest",
            new
            {
                path = "/pets/{id}",
                @class = "IDOR",   // upper-cased on purpose; matcher is case-insensitive
                verb = "GET",
                protocol = "rest",
                service = "pets",
                inputShape = "{\"id\":1}",
                authState = "authenticated",
                notes = "discovered via fuzz",
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Equal(yaml, body.GetProperty("yaml").GetString());
        Assert.Contains("yaml", body.GetProperty("raw").GetString(), StringComparison.Ordinal);
        Assert.Equal("yml-model", body.GetProperty("modelId").GetString());
        var fn = body.GetProperty("suggestedFilename").GetString();
        Assert.StartsWith("bowire-ai-", fn, StringComparison.Ordinal);
        Assert.EndsWith("-idor.yaml", fn, StringComparison.Ordinal);
        // The Class is lowercased; path slug strips non-alnum + collapses.
        Assert.DoesNotContain("--", fn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TemplateSuggest_UnfencedResponse_Returns_AsIs()
    {
        var yaml = "id: thing\ninfo:\n  name: thing";
        using var stub = new RecordingChatClient(yaml);
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/template-suggest",
            new { path = "/x", @class = "ssrf" },
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);
        Assert.Equal(yaml, body.GetProperty("yaml").GetString());
    }

    [Fact]
    public async Task TemplateSuggest_EmptyResponse_ReturnsEmptyYaml()
    {
        using var stub = new RecordingChatClient("");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/template-suggest",
            new { path = "/x", @class = "ssrf" },
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);
        Assert.Equal("", body.GetProperty("yaml").GetString());
    }

    [Fact]
    public async Task TemplateSuggest_FenceWithoutClose_FallsBackToFullText()
    {
        // Open ``` but no closing — ExtractYaml returns the entire text
        // (not the broken slice).
        using var stub = new RecordingChatClient("```yaml\nid: never-closed");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/template-suggest",
            new { path = "/x", @class = "ssrf" },
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);
        Assert.Contains("id: never-closed", body.GetProperty("yaml").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TemplateSuggest_LongInputShape_TruncatesInPrompt()
    {
        var longShape = new string('S', 2000);
        using var stub = new RecordingChatClient("yaml");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        await client.PostAsJsonAsync(
            "/api/ai/template-suggest",
            new { path = "/x", @class = "idor", inputShape = longShape, notes = "hi", verb = "POST", protocol = "rest", service = "svc", authState = "any" },
            TestContext.Current.CancellationToken);
        var userPrompt = stub.LastMessages!.Last(m => m.Role == ChatRole.User).Text;
        Assert.Contains("…", userPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('S', 1600), userPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TemplateSuggest_VeryLongPath_FilenameCapsAt60SlugChars()
    {
        // BuildFilename caps the path-slug at 60 chars, lowercases the
        // class, collapses runs of '-', falls back to "endpoint" when
        // the slug is empty.
        var longPath = "/a/" + new string('z', 200);
        using var stub = new RecordingChatClient("yaml");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/template-suggest",
            new { path = longPath, @class = "OPEN-REDIRECT" },
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);
        var fn = body.GetProperty("suggestedFilename").GetString()!;
        Assert.EndsWith("-open-redirect.yaml", fn, StringComparison.Ordinal);
        // bowire-ai- prefix + ≤ 60 slug chars + "-open-redirect.yaml"
        Assert.True(fn.Length <= "bowire-ai-".Length + 60 + "-open-redirect.yaml".Length + 1);
    }

    [Fact]
    public async Task TemplateSuggest_OnlySymbolsPath_FallsBackToEndpointSlug()
    {
        using var stub = new RecordingChatClient("yaml");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/template-suggest",
            new { path = "///!@#", @class = "idor" },
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);
        Assert.Equal("bowire-ai-endpoint-idor.yaml", body.GetProperty("suggestedFilename").GetString());
    }

    [Fact]
    public async Task TemplateSuggest_ProviderException_Returns502()
    {
        using var stub = new ThrowingChatClient(new InvalidOperationException("boom"));
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/template-suggest",
            new { path = "/x", @class = "idor" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Fact]
    public async Task TemplateSuggest_Canceled_Returns499()
    {
        using var stub = new ThrowingChatClient(new OperationCanceledException());
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/template-suggest",
            new { path = "/x", @class = "idor" },
            TestContext.Current.CancellationToken);
        Assert.Equal(499, (int)resp.StatusCode);
    }

    // ----- /api/ai/template-save --------------------------------------

    [Fact]
    public async Task TemplateSave_InvalidJson_Returns400()
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/template-save")
        {
            Content = new StringContent("not-json", Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData("", "yaml: ok")]
    [InlineData("ok.yaml", "")]
    public async Task TemplateSave_MissingRequired_Returns400(string filename, string yaml)
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/template-save",
            new { filename, yaml },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData("../escape.yaml")]
    [InlineData("foo/bar.yaml")]
    [InlineData("plain.txt")]
    [InlineData("no-extension")]
    public async Task TemplateSave_RejectsUnsafeFilename(string filename)
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/template-save",
            new { filename, yaml = "id: x" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Contains("filename", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("safe.yaml")]
    [InlineData("safe.YML")]
    [InlineData("safe.YAML")]
    public async Task TemplateSave_WritesFile_UnderTemplatesDir(string filename)
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/template-save",
            new { filename, yaml = "id: test\ninfo:\n  name: t" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.True(body.GetProperty("saved").GetBoolean());
        var savedPath = body.GetProperty("path").GetString()!;
        Assert.True(File.Exists(savedPath));
        Assert.EndsWith(filename, savedPath, StringComparison.Ordinal);
        Assert.Equal("id: test\ninfo:\n  name: t", await File.ReadAllTextAsync(savedPath, TestContext.Current.CancellationToken));
    }

    // ----- /api/ai/fuzz-values ----------------------------------------

    [Fact]
    public async Task FuzzValues_NoClient_Returns503()
    {
        using var host = BuildHostWithoutChatClient();
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/fuzz-values",
            new { fieldName = "f", fieldType = "string" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task FuzzValues_InvalidJson_Returns400()
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(new RecordingChatClient(""));
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/fuzz-values")
        {
            Content = new StringContent("not-json", Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData("", "string")]
    [InlineData("f", "")]
    public async Task FuzzValues_MissingFields_Returns400(string name, string type)
    {
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(new RecordingChatClient(""));
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/fuzz-values",
            new { fieldName = name, fieldType = type },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task FuzzValues_HappyPath_ParsesValues_AndNormalisesSeverity()
    {
        var modelOutput = """
            {
              "values": [
                {"value": "", "why": "empty string", "severity": "low"},
                {"value": 0, "why": "zero", "severity": "INFO"},
                {"value": null, "why": "null", "severity": "medium"},
                {"value": "critical-stuff", "why": "ouch", "severity": "critical"},
                {"value": "no-sev", "why": ""},
                {"why": "missing value, dropped"},
                "not an object"
              ]
            }
            """;
        using var stub = new RecordingChatClient(modelOutput, modelId: "fz-model");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/fuzz-values",
            new
            {
                fieldName = "petId",
                fieldType = "int32",
                fieldSchema = "{\"minimum\":1}",
                fieldExample = "1",
                methodName = "getPet",
                service = "pets",
                protocol = "rest",
                notes = "edge boundaries please",
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        var values = body.GetProperty("values");
        // Five rows survive: "" / 0 / null / "critical-stuff" / "no-sev".
        // The "missing value" row is dropped, the non-object string skipped.
        Assert.Equal(5, values.GetArrayLength());
        Assert.Equal("low", values[0].GetProperty("severity").GetString());
        // Case-insensitive normalisation: INFO → info.
        Assert.Equal("info", values[1].GetProperty("severity").GetString());
        Assert.Equal("medium", values[2].GetProperty("severity").GetString());
        // "critical" collapses to medium (advisory-only ceiling).
        Assert.Equal("medium", values[3].GetProperty("severity").GetString());
        // Empty severity falls back to "info".
        Assert.Equal("info", values[4].GetProperty("severity").GetString());

        Assert.Equal("petId", body.GetProperty("fieldName").GetString());
        Assert.Equal("int32", body.GetProperty("fieldType").GetString());
        Assert.Equal("fz-model", body.GetProperty("modelId").GetString());
        // Prompt covers the optional fields.
        var prompt = stub.LastMessages!.Last(m => m.Role == ChatRole.User).Text;
        Assert.Contains("petId", prompt, StringComparison.Ordinal);
        Assert.Contains("int32", prompt, StringComparison.Ordinal);
        Assert.Contains("minimum", prompt, StringComparison.Ordinal);
        Assert.Contains("getPet", prompt, StringComparison.Ordinal);
        Assert.Contains("pets", prompt, StringComparison.Ordinal);
        Assert.Contains("rest", prompt, StringComparison.Ordinal);
        Assert.Contains("edge boundaries", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FuzzValues_CapsAt20()
    {
        // 25 rows in, 20 out.
        var rows = string.Join(",", Enumerable.Range(0, 25)
            .Select(i => $"{{\"value\":{i},\"why\":\"\",\"severity\":\"info\"}}"));
        using var stub = new RecordingChatClient($"{{\"values\":[{rows}]}}");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/fuzz-values",
            new { fieldName = "n", fieldType = "int32" },
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);
        Assert.Equal(20, body.GetProperty("values").GetArrayLength());
    }

    [Fact]
    public async Task FuzzValues_LongSchema_TruncatesInPrompt()
    {
        var longSchema = new string('S', 2000);
        using var stub = new RecordingChatClient("""{"values":[]}""");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        await client.PostAsJsonAsync(
            "/api/ai/fuzz-values",
            new { fieldName = "f", fieldType = "string", fieldSchema = longSchema },
            TestContext.Current.CancellationToken);
        var prompt = stub.LastMessages!.Last(m => m.Role == ChatRole.User).Text;
        Assert.Contains("…", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("preamble {not json} trailer")]
    [InlineData("totally unrelated prose")]
    [InlineData("")]
    public async Task FuzzValues_GarbageResponse_ReturnsEmptyArray(string modelOutput)
    {
        using var stub = new RecordingChatClient(modelOutput);
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/fuzz-values",
            new { fieldName = "f", fieldType = "string" },
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);
        Assert.Equal(0, body.GetProperty("values").GetArrayLength());
    }

    [Fact]
    public async Task FuzzValues_NonArrayValues_ReturnsEmpty()
    {
        using var stub = new RecordingChatClient("""{"values": "should be array"}""");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/fuzz-values",
            new { fieldName = "f", fieldType = "string" },
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);
        Assert.Equal(0, body.GetProperty("values").GetArrayLength());
    }

    [Fact]
    public async Task FuzzValues_ProviderException_Returns502()
    {
        using var stub = new ThrowingChatClient(new InvalidOperationException("nope"));
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/fuzz-values",
            new { fieldName = "f", fieldType = "string" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Fact]
    public async Task FuzzValues_Canceled_Returns499()
    {
        using var stub = new ThrowingChatClient(new OperationCanceledException());
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/fuzz-values",
            new { fieldName = "f", fieldType = "string" },
            TestContext.Current.CancellationToken);
        Assert.Equal(499, (int)resp.StatusCode);
    }

    // ----- /api/ai/chat — tool calls + error paths --------------------

    [Fact]
    public async Task Chat_WithWorkbenchContext_BuildsTools_AndForwardsThem()
    {
        // The chat endpoint adds workbench tools when Context is set.
        // The stub captures ChatOptions so we can assert the Tools array
        // contains the expected read-only tools.
        using var stub = new RecordingChatClient("ok", modelId: "m");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/chat",
            new
            {
                messages = new[] { new { role = "user", content = "list services" } },
                context = new
                {
                    serverUrls = new[] { "http://localhost:5000" },
                    services = new[]
                    {
                        new
                        {
                            name = "petstore",
                            protocol = "rest",
                            originUrl = "http://localhost:5000",
                            methods = new[]
                            {
                                new
                                {
                                    name = "getPet",
                                    description = "fetch a pet",
                                    methodType = "GET",
                                    inputTypeName = "PetId",
                                    inputFields = new[] { new { name = "id", type = "int", optional = false } },
                                    outputTypeName = "Pet",
                                },
                            },
                        },
                    },
                    recent = new[] { new { service = "petstore", method = "getPet", status = "200" } },
                    allowInvoke = false,
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(stub.LastOptions);
        Assert.NotNull(stub.LastOptions!.Tools);
        var toolNames = stub.LastOptions.Tools!.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("bowire_list_services", toolNames);
        Assert.Contains("bowire_describe_method", toolNames);
        Assert.Contains("bowire_recent_history", toolNames);
        Assert.Contains("bowire_open_method", toolNames);
        // AllowInvoke=false → no invoke tool registered.
        Assert.DoesNotContain("bowire_invoke", toolNames);
    }

    [Fact]
    public async Task Chat_WithAllowInvoke_RegistersInvokeTool()
    {
        using var stub = new RecordingChatClient("ok");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        await client.PostAsJsonAsync(
            "/api/ai/chat",
            new
            {
                messages = new[] { new { role = "user", content = "hi" } },
                context = new
                {
                    services = new[] { new { name = "svc", protocol = "rest" } },
                    allowInvoke = true,
                },
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(stub.LastOptions);
        Assert.Contains("bowire_invoke", stub.LastOptions!.Tools!.Select(t => t.Name));
    }

    [Fact]
    public async Task Chat_WithoutContext_NoTools_Forwarded()
    {
        using var stub = new RecordingChatClient("ok");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        await client.PostAsJsonAsync(
            "/api/ai/chat",
            new { messages = new[] { new { role = "user", content = "hi" } } },
            TestContext.Current.CancellationToken);

        // tools.Count == 0 ⇒ ChatOptions left null.
        Assert.Null(stub.LastOptions);
    }

    [Fact]
    public async Task Chat_MapRole_RecognisesEveryDocumentedRole()
    {
        // MapRole branches: user / assistant / system / tool / anything-else.
        using var stub = new RecordingChatClient("ok");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        await client.PostAsJsonAsync(
            "/api/ai/chat",
            new
            {
                messages = new[]
                {
                    new { role = "system", content = "be helpful" },
                    new { role = "ASSISTANT", content = "ok" },
                    new { role = "TOOL", content = "ran the tool" },
                    new { role = "unknown-role", content = "falls back to user" },
                    new { role = "user", content = "go" },
                },
            },
            TestContext.Current.CancellationToken);

        var roles = stub.LastMessages!.Select(m => m.Role).ToList();
        Assert.Equal(ChatRole.System, roles[0]);
        Assert.Equal(ChatRole.Assistant, roles[1]);
        Assert.Equal(ChatRole.Tool, roles[2]);
        Assert.Equal(ChatRole.User, roles[3]);
        Assert.Equal(ChatRole.User, roles[4]);
    }

    [Fact]
    public async Task Chat_ModelEmitsToolCalls_AreSurfacedInResponse()
    {
        // A response that includes FunctionCallContent in its messages
        // should land in the toolCalls field of the response JSON.
        var toolCall = new FunctionCallContent("call-1", "bowire_list_services", new Dictionary<string, object?>
        {
            ["limit"] = 5,
        });
        var assistantMsg = new ChatMessage(ChatRole.Assistant, new List<AIContent> { toolCall, new TextContent("done") });
        var response = new ChatResponse(assistantMsg);
        using var stub = new RecordingChatClient(response);
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/chat",
            new { messages = new[] { new { role = "user", content = "hi" } } },
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);

        Assert.Equal(JsonValueKind.Array, body.GetProperty("toolCalls").ValueKind);
        Assert.Equal(1, body.GetProperty("toolCalls").GetArrayLength());
        Assert.Equal("bowire_list_services", body.GetProperty("toolCalls")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Chat_ModelNotFound_Returns502_WithModelExtension()
    {
        // 404 from the provider → structured ProblemDetails with
        // model + endpoint extensions so the UI can render an actionable
        // "ollama pull X" hint.
        using var stub = new ThrowingChatClient(new HttpRequestException(
            "Response status code does not indicate success: 404 (Not Found).",
            inner: null,
            statusCode: HttpStatusCode.NotFound));
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions
            {
                ProviderId = "ollama",
                Endpoint = "http://localhost:11434",
                Model = "missing-model:7b",
            });
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/chat",
            new { messages = new[] { new { role = "user", content = "hi" } } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        var body = await ReadJsonAsync(resp);
        Assert.Equal("urn:bowire:ai:model-not-found", body.GetProperty("type").GetString());
        Assert.Equal("missing-model:7b", body.GetProperty("model").GetString());
        Assert.Equal("http://localhost:11434", body.GetProperty("endpoint").GetString());
        Assert.Contains("ollama pull missing-model:7b", body.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_ProviderException_Returns502_WithExceptionType()
    {
        using var stub = new ThrowingChatClient(new InvalidOperationException("unexpected"));
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/chat",
            new { messages = new[] { new { role = "user", content = "hi" } } },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Equal("urn:bowire:ai:provider-error", body.GetProperty("type").GetString());
        Assert.Equal("InvalidOperationException", body.GetProperty("exceptionType").GetString());
        Assert.Contains("unexpected", body.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_Canceled_Returns499_ProblemDetails()
    {
        using var stub = new ThrowingChatClient(new OperationCanceledException("client gone"));
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/chat",
            new { messages = new[] { new { role = "user", content = "hi" } } },
            TestContext.Current.CancellationToken);
        Assert.Equal(499, (int)resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Equal("urn:bowire:canceled", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Chat_NullBodyJson_TreatedAsMissingMessages()
    {
        // POST body literally `null` deserialises to null ChatRequest →
        // the "missing messages" branch fires.
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(new RecordingChatClient(""));
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/chat")
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Contains("messages", body.GetProperty("title").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    // ----- Workbench tool surface (read-only + invoke) ----------------

    [Fact]
    public async Task Chat_DescribeMethod_Tool_ReturnsMethodDetails()
    {
        // Invoke the bowire_describe_method AIFunction directly by
        // pulling it out of the chat handler's ChatOptions. This pins
        // both the closure-over-context behaviour and the unknown-service
        // / unknown-method error branches without needing a real LLM.
        using var stub = new RecordingChatClient("ok");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        await client.PostAsJsonAsync(
            "/api/ai/chat",
            new
            {
                messages = new[] { new { role = "user", content = "hi" } },
                context = new
                {
                    services = new[]
                    {
                        new
                        {
                            name = "pets",
                            protocol = "rest",
                            originUrl = "http://localhost",
                            methods = new[]
                            {
                                new { name = "getPet", description = "fetch", methodType = "GET", inputTypeName = "Id", outputTypeName = "Pet" },
                            },
                        },
                    },
                    allowInvoke = false,
                },
            },
            TestContext.Current.CancellationToken);

        var describe = stub.LastOptions!.Tools!
            .OfType<AIFunction>()
            .First(t => t.Name == "bowire_describe_method");

        // Happy path: known service + method.
        var ok = await describe.InvokeAsync(new AIFunctionArguments
        {
            { "service", "PETS" },     // case-insensitive match
            { "method", "getPet" },
        });
        var okText = ok?.ToString() ?? "";
        Assert.Contains("getPet", okText, StringComparison.Ordinal);

        // Unknown service.
        var noSvc = await describe.InvokeAsync(new AIFunctionArguments
        {
            { "service", "nope" },
            { "method", "x" },
        });
        Assert.Contains("not in the workbench", noSvc?.ToString() ?? "", StringComparison.Ordinal);

        // Known service, unknown method.
        var noMethod = await describe.InvokeAsync(new AIFunctionArguments
        {
            { "service", "pets" },
            { "method", "missing" },
        });
        Assert.Contains("not on service", noMethod?.ToString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_RecentHistory_Tool_Defaults_And_Limit_Apply()
    {
        using var stub = new RecordingChatClient("ok");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        await client.PostAsJsonAsync(
            "/api/ai/chat",
            new
            {
                messages = new[] { new { role = "user", content = "hi" } },
                context = new
                {
                    services = Array.Empty<object>(),
                    recent = Enumerable.Range(0, 8).Select(i => new { service = "s", method = $"m{i}", status = "200" }).ToArray(),
                },
            },
            TestContext.Current.CancellationToken);

        var recent = stub.LastOptions!.Tools!
            .OfType<AIFunction>()
            .First(t => t.Name == "bowire_recent_history");

        // Default limit (null arg): min(5, 8) = 5 rows.
        var defaultResult = await recent.InvokeAsync(new AIFunctionArguments { { "limit", null } });
        var defaultJson = JsonSerializer.Serialize(defaultResult);
        Assert.Contains("m0", defaultJson, StringComparison.Ordinal);
        Assert.Contains("m4", defaultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("m5", defaultJson, StringComparison.Ordinal);

        // Explicit limit higher than the list: capped at the list length.
        var biggerResult = await recent.InvokeAsync(new AIFunctionArguments { { "limit", 50 } });
        var biggerJson = JsonSerializer.Serialize(biggerResult);
        Assert.Contains("m7", biggerJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_OpenMethod_Tool_Returns_Ok_For_Known_And_Error_For_Unknown()
    {
        using var stub = new RecordingChatClient("ok");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        await client.PostAsJsonAsync(
            "/api/ai/chat",
            new
            {
                messages = new[] { new { role = "user", content = "hi" } },
                context = new
                {
                    services = new[]
                    {
                        new
                        {
                            name = "pets",
                            protocol = "rest",
                            methods = new[] { new { name = "getPet" } },
                        },
                    },
                },
            },
            TestContext.Current.CancellationToken);

        var open = stub.LastOptions!.Tools!
            .OfType<AIFunction>()
            .First(t => t.Name == "bowire_open_method");

        var ok = await open.InvokeAsync(new AIFunctionArguments
        {
            { "service", "pets" },
            { "method", "getPet" },
        });
        var okJson = JsonSerializer.Serialize(ok);
        Assert.Contains("\"ok\":true", okJson, StringComparison.Ordinal);

        var badSvc = await open.InvokeAsync(new AIFunctionArguments
        {
            { "service", "nope" },
            { "method", "x" },
        });
        var badSvcJson = JsonSerializer.Serialize(badSvc);
        Assert.Contains("\"ok\":false", badSvcJson, StringComparison.Ordinal);

        var badMethod = await open.InvokeAsync(new AIFunctionArguments
        {
            { "service", "pets" },
            { "method", "missing" },
        });
        Assert.Contains("\"ok\":false", JsonSerializer.Serialize(badMethod), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_ListServices_Tool_ReportsCount_AndMethodNames()
    {
        using var stub = new RecordingChatClient("ok");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        await client.PostAsJsonAsync(
            "/api/ai/chat",
            new
            {
                messages = new[] { new { role = "user", content = "hi" } },
                context = new
                {
                    services = new[]
                    {
                        new
                        {
                            name = "pets",
                            protocol = (string?)"rest",
                            originUrl = (string?)"http://localhost",
                            methods = new[] { new { name = "a" }, new { name = "b" } },
                        },
                        new
                        {
                            name = "core",
                            protocol = (string?)null,
                            originUrl = (string?)null,
                            methods = new[] { new { name = "" } }.Take(0).ToArray(),  // empty methods → null-coalesce branch
                        },
                    },
                },
            },
            TestContext.Current.CancellationToken);

        var list = stub.LastOptions!.Tools!
            .OfType<AIFunction>()
            .First(t => t.Name == "bowire_list_services");
        var result = await list.InvokeAsync(new AIFunctionArguments());
        var json = JsonSerializer.Serialize(result);
        Assert.Contains("pets", json, StringComparison.Ordinal);
        Assert.Contains("core", json, StringComparison.Ordinal);
        Assert.Contains("\"methodCount\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"methodCount\":0", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_InvokeTool_UnknownService_Returns_Error()
    {
        var args = new AIFunctionArguments
        {
            { "service", "nope" }, { "method", "x" }, { "messageJson", null },
        };
        using var stub = new ToolInvokingChatClient("bowire_invoke", args);
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        await client.PostAsJsonAsync(
            "/api/ai/chat",
            new
            {
                messages = new[] { new { role = "user", content = "hi" } },
                context = new
                {
                    services = new[] { new { name = "pets", methods = new[] { new { name = "getPet" } } } },
                    allowInvoke = true,
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Contains("not in the workbench", stub.ToolResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_InvokeTool_UnknownMethod_Returns_Error()
    {
        var args = new AIFunctionArguments
        {
            { "service", "pets" }, { "method", "ghost" }, { "messageJson", null },
        };
        using var stub = new ToolInvokingChatClient("bowire_invoke", args);
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        await client.PostAsJsonAsync(
            "/api/ai/chat",
            new
            {
                messages = new[] { new { role = "user", content = "hi" } },
                context = new
                {
                    services = new[] { new { name = "pets", methods = new[] { new { name = "getPet" } } } },
                    allowInvoke = true,
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Contains("not on service", stub.ToolResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_InvokeTool_NoRegistry_Returns_Error()
    {
        // Known service+method, but no protocol registry → "Protocol registry
        // not available" branch.
        var args = new AIFunctionArguments
        {
            { "service", "pets" }, { "method", "getPet" }, { "messageJson", "{}" },
        };
        using var stub = new ToolInvokingChatClient("bowire_invoke", args);
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        await client.PostAsJsonAsync(
            "/api/ai/chat",
            new
            {
                messages = new[] { new { role = "user", content = "hi" } },
                context = new
                {
                    services = new[] { new { name = "pets", methods = new[] { new { name = "getPet" } } } },
                    allowInvoke = true,
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Contains("registry not available", stub.ToolResultJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Chat_InvokeTool_WithRegistry_HappyPath_AppendsAuditLog()
    {
        var fakeProtocol = new FakeProtocol("rest", okResponse: "{\"ok\":true}");
        var registry = new BowireProtocolRegistry();
        registry.Register(fakeProtocol);

        var args = new AIFunctionArguments
        {
            { "service", "pets" }, { "method", "getPet" }, { "messageJson", "{\"id\":1}" },
        };
        using var stub = new ToolInvokingChatClient("bowire_invoke", args);
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddSingleton(registry);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        await client.PostAsJsonAsync(
            "/api/ai/chat",
            new
            {
                messages = new[] { new { role = "user", content = "hi" } },
                context = new
                {
                    services = new[]
                    {
                        new
                        {
                            name = "pets",
                            protocol = "rest",
                            originUrl = "http://localhost:5000",
                            methods = new[] { new { name = "getPet" } },
                        },
                    },
                    allowInvoke = true,
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Contains("\"ok\":true", stub.ToolResultJson, StringComparison.Ordinal);
        Assert.Contains("200", stub.ToolResultJson, StringComparison.Ordinal);
        Assert.Equal("http://localhost:5000", fakeProtocol.LastServerUrl);
        Assert.Equal("{\"id\":1}", fakeProtocol.LastMessages![0]);

        // Audit log file landed in the temp user store.
        var auditPath = Path.Combine(_tempRoot, ".ai-actions.jsonl");
        Assert.True(File.Exists(auditPath));
        var line = (await File.ReadAllTextAsync(auditPath, TestContext.Current.CancellationToken)).Trim();
        Assert.Contains("\"service\":\"pets\"", line, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"200\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_InvokeTool_RegistryWithoutMatchingProtocol_ReturnsError()
    {
        var fakeProtocol = new FakeProtocol("grpc", okResponse: "{}");
        var registry = new BowireProtocolRegistry();
        registry.Register(fakeProtocol);

        var args = new AIFunctionArguments
        {
            { "service", "pets" }, { "method", "getPet" }, { "messageJson", null },
        };
        using var stub = new ToolInvokingChatClient("bowire_invoke", args);
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddSingleton(registry);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        // Service.Protocol = "rest" — registry only has grpc → GetById
        // returns null → "No protocol plugin loaded" branch.
        await client.PostAsJsonAsync(
            "/api/ai/chat",
            new
            {
                messages = new[] { new { role = "user", content = "hi" } },
                context = new
                {
                    services = new[]
                    {
                        new
                        {
                            name = "pets",
                            protocol = "rest",
                            methods = new[] { new { name = "getPet" } },
                        },
                    },
                    allowInvoke = true,
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Contains("No protocol plugin", stub.ToolResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_InvokeTool_ProtocolThrows_LogsErrorAndReturnsErrorPayload()
    {
        var fakeProtocol = new FakeProtocol("rest", throwOnInvoke: new InvalidOperationException("simulated invoke failure"));
        var registry = new BowireProtocolRegistry();
        registry.Register(fakeProtocol);

        // Pass an empty messageJson so the "default to {}" fallback runs.
        var args = new AIFunctionArguments
        {
            { "service", "pets" }, { "method", "getPet" }, { "messageJson", "" },
        };
        using var stub = new ToolInvokingChatClient("bowire_invoke", args);
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddSingleton(registry);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        // No serverUrl on the service — fall back to wbCtx.ServerUrls[0]
        // (covers that branch). Also no protocol on the service → use
        // registry.Protocols[0].
        await client.PostAsJsonAsync(
            "/api/ai/chat",
            new
            {
                messages = new[] { new { role = "user", content = "hi" } },
                context = new
                {
                    serverUrls = new[] { "http://fallback:9000" },
                    services = new[]
                    {
                        new
                        {
                            name = "pets",
                            protocol = (string?)null,
                            originUrl = (string?)null,
                            methods = new[] { new { name = "getPet" } },
                        },
                    },
                    allowInvoke = true,
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Contains("simulated invoke failure", stub.ToolResultJson, StringComparison.Ordinal);
        Assert.Contains("\"ok\":false", stub.ToolResultJson, StringComparison.Ordinal);
        Assert.Equal("http://fallback:9000", fakeProtocol.LastServerUrl);

        // Audit log records the error entry too.
        var auditPath = Path.Combine(_tempRoot, ".ai-actions.jsonl");
        Assert.True(File.Exists(auditPath));
        var logContents = await File.ReadAllTextAsync(auditPath, TestContext.Current.CancellationToken);
        Assert.Contains("simulated invoke failure", logContents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_InvokeTool_AuditWriteFails_DoesNotBreakInvoke()
    {
        // Pre-create .ai-actions.jsonl as a DIRECTORY so File.AppendAllText
        // throws — exercises the swallow-the-audit-failure catch block in
        // AppendAuditLog. The invoke result should still come back OK.
        Directory.CreateDirectory(Path.Combine(_tempRoot, ".ai-actions.jsonl"));

        var fakeProtocol = new FakeProtocol("rest", okResponse: "{\"ok\":true}");
        var registry = new BowireProtocolRegistry();
        registry.Register(fakeProtocol);

        var args = new AIFunctionArguments
        {
            { "service", "pets" }, { "method", "getPet" }, { "messageJson", "{}" },
        };
        using var stub = new ToolInvokingChatClient("bowire_invoke", args);
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddSingleton(registry);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/chat",
            new
            {
                messages = new[] { new { role = "user", content = "hi" } },
                context = new
                {
                    services = new[]
                    {
                        new
                        {
                            name = "pets",
                            protocol = "rest",
                            originUrl = "http://localhost:5000",
                            methods = new[] { new { name = "getPet" } },
                        },
                    },
                    allowInvoke = true,
                },
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // Despite the audit writing failing, the tool's invoke succeeded.
        Assert.Contains("\"ok\":true", stub.ToolResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_InvokeTool_EmptyRegistry_FallsThrough_To_NoProtocolError()
    {
        // Registry with zero protocols → Protocols[0] guard fires.
        var emptyRegistry = new BowireProtocolRegistry();

        var args = new AIFunctionArguments
        {
            { "service", "pets" }, { "method", "getPet" }, { "messageJson", "{}" },
        };
        using var stub = new ToolInvokingChatClient("bowire_invoke", args);
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddSingleton(emptyRegistry);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        await client.PostAsJsonAsync(
            "/api/ai/chat",
            new
            {
                messages = new[] { new { role = "user", content = "hi" } },
                context = new
                {
                    services = new[]
                    {
                        new
                        {
                            name = "pets",
                            protocol = (string?)null,
                            methods = new[] { new { name = "getPet" } },
                        },
                    },
                    allowInvoke = true,
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Contains("No protocol plugin", stub.ToolResultJson, StringComparison.Ordinal);
    }

    // ----- /api/ai/probe-local — actual probe paths -------------------

    [Fact]
    public async Task ProbeLocal_OllamaSuccessPath_ParsesModelList()
    {
        // Stand up a tiny in-process HTTP listener on the canonical
        // Ollama port. The endpoint's static ProbeHttp client is hard-
        // coded to 127.0.0.1:11434, so the success branch of
        // ProbeOllamaAsync fires when the listener serves a /api/tags
        // response. Skips if the port is already bound (CI machine with
        // an actual Ollama install) so the test stays best-effort.
        using var listener = TryStartListener(11434, "/api/tags/",
            "{\"models\":[{\"name\":\"llama3.2:3b\"},{\"name\":\"qwen2.5:7b\"}]}");
        if (listener is null) return; // port unavailable — skip

        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions { AutoDetectLocal = true });
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.GetAsync(
            new Uri("/api/ai/probe-local", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        // ollama key is a populated object when the success branch fired.
        if (body.GetProperty("ollama").ValueKind == JsonValueKind.Object)
        {
            var ollama = body.GetProperty("ollama");
            Assert.Equal("ollama", ollama.GetProperty("provider").GetString());
            Assert.Equal("http://127.0.0.1:11434", ollama.GetProperty("endpoint").GetString());
            var models = ollama.GetProperty("models");
            Assert.True(models.GetArrayLength() >= 1);
        }
    }

    [Fact]
    public async Task ProbeLocal_LmStudioSuccessPath_ParsesModelList()
    {
        // Same as the Ollama test, against the LM Studio canonical port.
        // LM Studio uses the OpenAI shape: /v1/models with { data: [{id, ...}] }.
        using var listener = TryStartListener(1234, "/v1/models",
            "{\"data\":[{\"id\":\"mistral-7b\"},{\"id\":\"qwen-coder\"}]}");
        if (listener is null) return;

        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions { AutoDetectLocal = true });
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.GetAsync(
            new Uri("/api/ai/probe-local", UriKind.Relative),
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);
        if (body.GetProperty("lmstudio").ValueKind == JsonValueKind.Object)
        {
            var lm = body.GetProperty("lmstudio");
            Assert.Equal("lmstudio", lm.GetProperty("provider").GetString());
            Assert.Equal("http://127.0.0.1:1234", lm.GetProperty("endpoint").GetString());
        }
    }

    private static System.Net.HttpListener? TryStartListener(int port, string path, string responseBody)
    {
        try
        {
            var listener = new System.Net.HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    System.Net.HttpListenerContext? ctx = null;
                    try { ctx = await listener.GetContextAsync(); }
                    catch { return; }
                    try
                    {
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        var bytes = Encoding.UTF8.GetBytes(responseBody);
                        await ctx.Response.OutputStream.WriteAsync(bytes);
                        ctx.Response.Close();
                    }
                    catch { /* swallow — the test only cares about the first hit */ }
                }
            });
            return listener;
        }
        catch
        {
            return null; // port already bound or insufficient permissions
        }
    }

    [Fact]
    public async Task ProbeLocal_AutoDetectOn_AnswersWithBothProbeKeys()
    {
        // Auto-detect on → the probe runs against localhost:11434 and :1234.
        // Whether either is up depends on the dev box; we only assert the
        // shape (200 + both keys present) so the test stays green
        // regardless of the local environment. When Ollama / LM Studio
        // are running locally the success branch fires; when they're
        // absent the catch path returns null. Either way the response
        // surface is { ollama: <object|null>, lmstudio: <object|null> }
        // and exercises the inner JSON parser by virtue of doing the
        // network round-trip.
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions { AutoDetectLocal = true });
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.GetAsync(
            new Uri("/api/ai/probe-local", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.True(body.TryGetProperty("ollama", out var ollama));
        Assert.True(body.TryGetProperty("lmstudio", out var lm));
        // Each key is either null (catch path) or an object (success path).
        Assert.True(ollama.ValueKind == JsonValueKind.Null || ollama.ValueKind == JsonValueKind.Object);
        Assert.True(lm.ValueKind == JsonValueKind.Null || lm.ValueKind == JsonValueKind.Object);
    }

    // ----- /api/ai/config — extra branches ---------------------------

    [Fact]
    public async Task PostConfig_HostManaged_SkipsRuntimeUpdate_ButPersists()
    {
        // Host owns IChatClient → POST /api/ai/config saves the pick
        // but doesn't touch the runtime. The status payload's hostManaged
        // stays true.
        using var hostClient = new RecordingChatClient("host");
        var runtimeBefore = (BowireAiRuntime?)null;
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions { Model = "before:1b" });
            s.AddSingleton<IChatClient>(hostClient);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        runtimeBefore = host.Services.GetRequiredService<BowireAiRuntime>();
        var modelBefore = runtimeBefore.Options.Model;
        using var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            "/api/ai/config",
            new { providerId = "ollama", endpoint = "http://localhost:11434", model = "saved-but-not-applied:7b" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.True(body.GetProperty("hostManaged").GetBoolean());
        // Echoed value is the requested one.
        Assert.Equal("saved-but-not-applied:7b", body.GetProperty("model").GetString());
        // But the runtime didn't actually swap — host owns the client.
        Assert.Equal(modelBefore, runtimeBefore.Options.Model);
        // The file landed on disk.
        Assert.True(File.Exists(Path.Combine(_tempRoot, "ai-config.json")));
    }

    [Fact]
    public async Task DeleteConfig_HostManaged_UsesPersistedAsApplied()
    {
        // Host owns IChatClient → DELETE doesn't touch the runtime; it
        // returns the persisted global config (if any) as the applied set.
        BowireAiUserConfigStore.Save(new BowireAiOptions { Model = "global-disk:1b" });
        BowireAiUserConfigStore.Save(new BowireAiOptions { Model = "ws-pick:7b" }, workspaceId: "personal");

        using var hostClient = new RecordingChatClient("host");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions { Model = "runtime-default:1b" });
            s.AddSingleton<IChatClient>(hostClient);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.DeleteAsync(
            new Uri("/api/ai/config?workspaceId=personal", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.True(body.GetProperty("hostManaged").GetBoolean());
        // The persisted global config (model=global-disk:1b) wins over the
        // runtime defaults because the resolver TryLoad returns non-null.
        Assert.Equal("global-disk:1b", body.GetProperty("model").GetString());
        Assert.False(File.Exists(Path.Combine(_tempRoot, "ai-config.personal.json")));
    }

    [Fact]
    public async Task DeleteConfig_NotHostManaged_NoGlobalFile_FallsBackToRuntimeOptions()
    {
        // No global ai-config.json on disk; one workspace override → DELETE
        // it. The runtime path constructs a BowireAiOptions copy from
        // runtime.Options because TryLoad() returns null.
        BowireAiUserConfigStore.Save(new BowireAiOptions { Model = "ws-pick:7b" }, workspaceId: "personal");

        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions
            {
                ProviderId = "ollama",
                Endpoint = "http://localhost:11434",
                Model = "runtime-baseline:1b",
                AutoDetectLocal = true,
            });
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        var resp = await client.DeleteAsync(
            new Uri("/api/ai/config?workspaceId=personal", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.False(body.GetProperty("hostManaged").GetBoolean());
        Assert.Equal("runtime-baseline:1b", body.GetProperty("model").GetString());
    }

    [Fact]
    public async Task PostConfig_NullJsonBody_Returns400_RequestBodyRequired()
    {
        // Body literally `null` deserialises to null → "Request body required" branch.
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/config")
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Contains("Request body required", body.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostConfig_PersistFails_Returns500()
    {
        // Swap in a user store whose path lookup throws → Save() bubbles
        // up → the handler's catch produces a structured 500.
        BowireUserContext.Current = new ThrowingUserStore();
        try
        {
            using var host = BuildHost(register: s =>
            {
                s.AddSingleton(new BowireAiOptions());
                s.AddBowireAi(new ConfigurationBuilder().Build());
            });
            using var client = host.GetTestClient();

            var resp = await client.PostAsJsonAsync(
                "/api/ai/config",
                new { providerId = "ollama", endpoint = "http://localhost:11434", model = "x" },
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
            var body = await ReadJsonAsync(resp);
            Assert.Contains("Failed to persist", body.GetProperty("error").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            BowireUserContext.Current = new TempUserStore(_tempRoot);
        }
    }

    [Fact]
    public async Task TemplateSave_WriteFails_Returns500()
    {
        // Pre-create the templates dir AS A FILE so File.WriteAllTextAsync
        // can't write inside it (or set up an unwritable path). The
        // user-store seam makes this easy: hand it a path that already
        // exists as a file.
        BowireUserContext.Current = new ThrowingUserStore();
        try
        {
            using var host = BuildHost(register: s =>
            {
                s.AddSingleton(new BowireAiOptions());
                s.AddBowireAi(new ConfigurationBuilder().Build());
            });
            using var client = host.GetTestClient();

            var resp = await client.PostAsJsonAsync(
                "/api/ai/template-save",
                new { filename = "x.yaml", yaml = "id: x" },
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
            var body = await ReadJsonAsync(resp);
            Assert.Contains("Failed to save template", body.GetProperty("error").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            BowireUserContext.Current = new TempUserStore(_tempRoot);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ThreatModel_WhitespaceModelOutput_ReturnsEmpty(string? modelOutput)
    {
        using var stub = new RecordingChatClient(modelOutput ?? "");
        using var host = BuildHost(register: s =>
        {
            s.AddSingleton(new BowireAiOptions());
            s.AddSingleton<IChatClient>(stub);
            s.AddBowireAi(new ConfigurationBuilder().Build());
        });
        using var client = host.GetTestClient();
        var resp = await client.PostAsJsonAsync(
            "/api/ai/threat-model",
            new { endpoints = new[] { new { endpointId = "a", path = "/a" } } },
            TestContext.Current.CancellationToken);
        var body = await ReadJsonAsync(resp);
        Assert.Equal(0, body.GetProperty("ranked").GetArrayLength());
    }

    // ----- BowireAiUserConfigStore — RemoveOverride / HasOverride catches

    [Fact]
    public void RemoveOverride_StoreThrows_DoesNotPropagate()
    {
        // A user-store implementation that throws on path lookup must
        // not crash the DELETE flow — RemoveOverride is best-effort.
        BowireUserContext.Current = new ThrowingUserStore();
        try
        {
            BowireAiUserConfigStore.RemoveOverride("personal");
        }
        finally
        {
            BowireUserContext.Current = new TempUserStore(_tempRoot);
        }
    }

    [Fact]
    public void HasOverride_StoreThrows_ReturnsFalse()
    {
        BowireUserContext.Current = new ThrowingUserStore();
        try
        {
            Assert.False(BowireAiUserConfigStore.HasOverride("personal"));
        }
        finally
        {
            BowireUserContext.Current = new TempUserStore(_tempRoot);
        }
    }

    // ----- helpers ---------------------------------------------------

    private static IHost BuildHost(Action<IServiceCollection> register)
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
                       register(s);
                   });
            })
            .Start();
    }

    private static IHost BuildHostWithoutChatClient()
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
                       s.AddSingleton<BowireAiRuntime>(sp => new BowireAiRuntime(sp.GetRequiredService<BowireAiOptions>()));
                   });
            })
            .Start();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private sealed class TempUserStore(string root) : IBowireUserStore
    {
        public string GetUserPath(string filename) => Path.Combine(root, filename);
    }

    private sealed class ThrowingUserStore : IBowireUserStore
    {
        public string GetUserPath(string filename)
            => throw new IOException("user store unavailable");
    }

    /// <summary>
    /// Records the last call's messages + options + returns a
    /// preconfigured ChatResponse (text or a fully-formed response).
    /// </summary>
    private sealed class RecordingChatClient : IChatClient
    {
        private readonly ChatResponse _response;

        public IList<ChatMessage>? LastMessages { get; private set; }
        public ChatOptions? LastOptions { get; private set; }

        public RecordingChatClient(string responseText, string? modelId = null)
        {
            _response = new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText))
            {
                ModelId = modelId,
            };
        }

        public RecordingChatClient(ChatResponse response)
        {
            _response = response;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastMessages = [.. messages];
            LastOptions = options;
            return Task.FromResult(_response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastMessages = [.. messages];
            LastOptions = options;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, _response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Chat client that invokes one of the workbench tools on the spot
    /// (i.e. while the request scope is still alive — the bowire_invoke
    /// tool closes over the request's IServiceProvider). The result is
    /// surfaced via <see cref="ToolResultJson"/> so the test can pin
    /// what the tool returned.
    /// </summary>
    private sealed class ToolInvokingChatClient : IChatClient
    {
        public string ToolName { get; }
        public AIFunctionArguments Arguments { get; }
        public string? ToolResultJson { get; private set; }
        public Exception? ToolException { get; private set; }

        public ToolInvokingChatClient(string toolName, AIFunctionArguments args)
        {
            ToolName = toolName;
            Arguments = args;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var tool = options?.Tools?.OfType<AIFunction>().FirstOrDefault(t => t.Name == ToolName);
            if (tool is null)
            {
                ToolResultJson = "{\"error\":\"tool not found\"}";
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "tool missing"));
            }
            try
            {
                var result = await tool.InvokeAsync(Arguments, cancellationToken);
                ToolResultJson = JsonSerializer.Serialize(result);
            }
            catch (Exception ex)
            {
                ToolException = ex;
                ToolResultJson = "{\"thrown\":\"" + ex.Message + "\"}";
            }
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ThrowingChatClient(Exception ex) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromException<ChatResponse>(ex);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw ex;

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Minimal IBowireProtocol for the invoke-tool tests. Captures the
    /// invocation arguments + returns a configurable result; can throw
    /// instead to exercise the catch path inside the invoke tool.
    /// </summary>
    private sealed class FakeProtocol : IBowireProtocol
    {
        private readonly string _okResponse;
        private readonly Exception? _throwOnInvoke;

        public FakeProtocol(string id, string okResponse = "{}", Exception? throwOnInvoke = null)
        {
            Id = id;
            Name = id;
            _okResponse = okResponse;
            _throwOnInvoke = throwOnInvoke;
        }

        public string Name { get; }
        public string Id { get; }
        public string IconSvg => "<svg/>";

        public string? LastServerUrl { get; private set; }
        public List<string>? LastMessages { get; private set; }

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            LastServerUrl = serverUrl;
            LastMessages = jsonMessages;
            if (_throwOnInvoke is not null) throw _throwOnInvoke;
            return Task.FromResult(new InvokeResult(_okResponse, DurationMs: 42, Status: "200", Metadata: []));
        }

        public IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => AsyncEnumerable.Empty<string>();

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }

    private static class AsyncEnumerable
    {
        public static IAsyncEnumerable<T> Empty<T>() => new EmptyAsync<T>();

        private sealed class EmptyAsync<T> : IAsyncEnumerable<T>, IAsyncEnumerator<T>
        {
            public T Current => default!;
            public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
            public ValueTask DisposeAsync() => default;
            public ValueTask<bool> MoveNextAsync() => new(false);
        }
    }
}

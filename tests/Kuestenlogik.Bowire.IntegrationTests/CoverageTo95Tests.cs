// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Pin the BowireSemanticsEndpoints POST/DELETE validation contracts +
/// the malformed-body catch path. Existing integration tests cover the
/// happy path on <c>GET /api/semantics/effective</c> and <c>/api/ui/extensions</c>;
/// this file fills the validation branches the POST/DELETE annotation
/// routes use to surface user errors as structured 400 ProblemDetails.
/// </summary>
public sealed class CoverageTo95Tests
{
    private static async Task<WebApplication> BuildAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddBowire(opts => opts.SchemaHintsPath = string.Empty);

        var app = builder.Build();
        app.MapBowire("/bowire");
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    [Fact]
    public async Task POST_annotation_returns_400_when_body_is_not_json()
    {
        // Drives ReadAnnotationRequestAsync's JsonException catch
        // (lines ~504-506 in the source).
        var app = await BuildAppAsync();
        await using (app)
        {
            var client = app.GetTestClient();
            using var content = new StringContent(
                "{this isn't valid json", System.Text.Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync(
                new Uri("/bowire/api/semantics/annotation", UriKind.Relative),
                content,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(
                TestContext.Current.CancellationToken);
            Assert.Equal(
                "urn:bowire:invalid-input",
                problem.GetProperty("type").GetString());
            Assert.Contains(
                "valid JSON",
                problem.GetProperty("title").GetString() ?? "",
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(null, "method", "$.x", "coordinate.latitude", "session", "service")]
    [InlineData("svc", null, "$.x", "coordinate.latitude", "session", "method")]
    [InlineData("svc", "method", null, "coordinate.latitude", "session", "jsonPath")]
    [InlineData("svc", "method", "$.x", null, "session", "semantic")]
    [InlineData("svc", "method", "$.x", "coordinate.latitude", null, "scope")]
    public async Task POST_annotation_returns_400_when_required_field_is_missing(
        string? service, string? method, string? jsonPath,
        string? semantic, string? scope, string expectedField)
    {
        // Drives the ValidateWriteRequest branches one at a time
        // (lines ~510-538 in the source).
        var app = await BuildAppAsync();
        await using (app)
        {
            var client = app.GetTestClient();
            var body = new
            {
                service,
                method,
                jsonPath,
                semantic,
                scope,
            };

            using var resp = await client.PostAsJsonAsync(
                "/bowire/api/semantics/annotation",
                body,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(
                TestContext.Current.CancellationToken);
            var detail = problem.GetProperty("detail").GetString() ?? "";
            // The validator names the offending field — pins the operator-
            // facing error so a future rename breaks loudly.
            Assert.Contains(expectedField, detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(null, "method", "$.x", "session", "service")]
    [InlineData("svc", null, "$.x", "session", "method")]
    [InlineData("svc", "method", null, "session", "jsonPath")]
    [InlineData("svc", "method", "$.x", null, "scope")]
    public async Task DELETE_annotation_returns_400_when_required_field_is_missing(
        string? service, string? method, string? jsonPath,
        string? scope, string expectedField)
    {
        // Drives the ValidateDeleteRequest branches (lines ~541-565).
        // DELETE has no semantic field — that's the only schema difference
        // from POST.
        var app = await BuildAppAsync();
        await using (app)
        {
            var client = app.GetTestClient();
            var body = new { service, method, jsonPath, scope };

            using var req = new HttpRequestMessage(
                HttpMethod.Delete,
                new Uri("/bowire/api/semantics/annotation", UriKind.Relative))
            {
                Content = JsonContent.Create(body),
            };
            using var resp = await client.SendAsync(req, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(
                TestContext.Current.CancellationToken);
            var detail = problem.GetProperty("detail").GetString() ?? "";
            Assert.Contains(expectedField, detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task POST_annotation_returns_400_when_scope_is_unknown()
    {
        // Drives the default arm of the scope switch (line ~203-208) —
        // a syntactically-valid scope value that isn't one of session/
        // user/project must surface as a structured 400.
        var app = await BuildAppAsync();
        await using (app)
        {
            var client = app.GetTestClient();
            using var resp = await client.PostAsJsonAsync(
                "/bowire/api/semantics/annotation",
                new
                {
                    service = "svc",
                    method = "method",
                    jsonPath = "$.x",
                    semantic = "coordinate.latitude",
                    scope = "global-not-supported",
                },
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(
                TestContext.Current.CancellationToken);
            Assert.Contains(
                "scope",
                problem.GetProperty("title").GetString() ?? "",
                StringComparison.OrdinalIgnoreCase);
        }
    }
}

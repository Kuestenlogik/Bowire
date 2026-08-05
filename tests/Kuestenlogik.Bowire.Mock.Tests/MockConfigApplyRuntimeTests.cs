// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Mock.Management;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Protocol.Rest;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// #561 end-to-end: a mock configuration applied to a RUNNING schema mock
/// takes effect live — a field override changes the served response, and a
/// conditional rule serves its variant when the request predicate matches
/// (via the existing matcher, as a higher-priority stub) and falls back to the
/// overridden default otherwise.
/// </summary>
[Collection("MockHostSerialised")]
public sealed class MockConfigApplyRuntimeTests
{
    private const string OrdersOpenApi = """
        openapi: 3.0.3
        info:
          title: Orders
          version: 1.0.0
        paths:
          /orders:
            post:
              operationId: createOrder
              tags: [Orders]
              responses:
                '200':
                  description: OK
                  content:
                    application/json:
                      schema:
                        type: object
                        properties:
                          status:
                            type: string
        """;

    static MockConfigApplyRuntimeTests()
    {
        BowireOpenApiAdapterRegistry.Register(
            new Kuestenlogik.Bowire.Protocol.Rest.OpenApi3.OpenApi3Adapter());
    }

    private static BowireMockHostManager NewManager() =>
        new(new IBowireMockSchemaSource[] { new OpenApiMockSchemaSource() },
            Array.Empty<IBowireMockLiveSchemaHandler>(),
            Array.Empty<IBowireMockHostingExtension>());

    [Fact]
    public async Task ApplyConfig_Override_And_Conditional_Rule_Take_Effect_Live()
    {
        await using var manager = NewManager();
        var handle = await manager.StartFromSchemaAsync(
            "openapi", OrdersOpenApi, schemaPath: null, "orders", port: 0,
            TestContext.Current.CancellationToken);

        var config = new MockConfiguration();
        config.FieldOverrides.Add(new MockFieldOverride
        {
            Service = "Orders",
            Method = "createOrder",
            JsonPath = "$.status",
            Value = JsonSerializer.SerializeToElement("overridden"),
        });
        config.ConditionalRules.Add(new MockConditionalRule
        {
            Service = "Orders",
            Method = "createOrder",
            When = new MockRulePredicate { JsonPath = "$.role", EqualTo = "admin" },
            Response = JsonSerializer.SerializeToElement(new { status = "admin-order" }),
        });

        Assert.True(manager.ApplyConfig(handle.MockId, config));

        using var http = new HttpClient();

        // Predicate does NOT match → the overridden default response.
        Assert.Equal("overridden", await PostStatus(http, handle.Url, """{"role":"guest"}"""));

        // Predicate matches → the conditional-rule variant wins (priority).
        Assert.Equal("admin-order", await PostStatus(http, handle.Url, """{"role":"admin"}"""));

        // Re-applying an empty config recomputes from the baseline — the
        // override + rule are gone, the schema-typed sample is back.
        Assert.True(manager.ApplyConfig(handle.MockId, new MockConfiguration()));
        Assert.Equal("sample", await PostStatus(http, handle.Url, """{"role":"admin"}"""));

        await manager.StopAsync(handle.MockId, TestContext.Current.CancellationToken);
    }

    private static async Task<string?> PostStatus(HttpClient http, string baseUrl, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await http.PostAsync(new Uri($"{baseUrl}/orders"), content, TestContext.Current.CancellationToken);
        var text = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("status").GetString();
    }
}

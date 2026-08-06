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

    [Fact]
    public async Task ApplyConfig_Require_Auth_Gates_Requests_With_401()
    {
        await using var manager = NewManager();
        var handle = await manager.StartFromSchemaAsync(
            "openapi", OrdersOpenApi, schemaPath: null, "orders", port: 0,
            TestContext.Current.CancellationToken);

        var config = new MockConfiguration
        {
            Auth = new MockAuthRequirement { Required = true, Scheme = "bearer", Credential = "s3cret" },
        };
        Assert.True(manager.ApplyConfig(handle.MockId, config));

        using var http = new HttpClient();

        // No credential → 401 before replay.
        using (var noAuth = new StringContent("{}", Encoding.UTF8, "application/json"))
        {
            var resp = await http.PostAsync(new Uri($"{handle.Url}/orders"), noAuth, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // Correct bearer token → 200 (replayed).
        using (var req = new HttpRequestMessage(HttpMethod.Post, new Uri($"{handle.Url}/orders")))
        {
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "s3cret");
            var resp = await http.SendAsync(req, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        }

        // Wrong token → 401.
        using (var req = new HttpRequestMessage(HttpMethod.Post, new Uri($"{handle.Url}/orders")))
        {
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "nope");
            var resp = await http.SendAsync(req, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // The control surface stays reachable while auth is on — the gate
        // exempts /__bowire/mock/* so scenario control keeps working. No control
        // token is set here, so the only 401 that could appear is the auth gate's;
        // asserting it is absent proves the exemption fires (the endpoint itself
        // answers 404/200, not our 401).
        using (var ctrl = new HttpRequestMessage(HttpMethod.Get, new Uri($"{handle.Url}/__bowire/mock/status")))
        {
            var resp = await http.SendAsync(ctrl, TestContext.Current.CancellationToken);
            Assert.NotEqual(System.Net.HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // Toggle auth off (empty config clears it) → 200 without a credential.
        Assert.True(manager.ApplyConfig(handle.MockId, new MockConfiguration()));
        using (var off = new StringContent("{}", Encoding.UTF8, "application/json"))
        {
            var resp = await http.PostAsync(new Uri($"{handle.Url}/orders"), off, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        }

        await manager.StopAsync(handle.MockId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AuthRecordingId_Resolves_To_A_Captured_Credential_That_Gates_Live()
    {
        await using var manager = NewManager();
        var handle = await manager.StartFromSchemaAsync(
            "openapi", OrdersOpenApi, schemaPath: null, "orders", port: 0,
            TestContext.Current.CancellationToken);

        // The config references a captured recording, not a typed token.
        var config = new MockConfiguration
        {
            Auth = new MockAuthRequirement { Required = true, AuthRecordingId = "rec-1" },
        };

        // Resolve exactly the way the /config/apply endpoint does, then apply.
        var resolver = new StubAuthResolver("rec-1", new MockAuthResolution("captured-tok", "bearer", null));
        Assert.Equal(MockAuthRecordingResolution.Outcome.Resolved,
            MockAuthRecordingResolution.Apply(config, resolver, workspaceId: null));
        Assert.Equal("captured-tok", config.Auth!.Credential);

        Assert.True(manager.ApplyConfig(handle.MockId, config));

        using var http = new HttpClient();

        // No credential → 401.
        using (var noAuth = new StringContent("{}", Encoding.UTF8, "application/json"))
        {
            var resp = await http.PostAsync(new Uri($"{handle.Url}/orders"), noAuth, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // The captured credential → 200.
        using (var req = new HttpRequestMessage(HttpMethod.Post, new Uri($"{handle.Url}/orders")))
        {
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "captured-tok");
            var resp = await http.SendAsync(req, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        }

        // A different token → 401.
        using (var req = new HttpRequestMessage(HttpMethod.Post, new Uri($"{handle.Url}/orders")))
        {
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "nope");
            var resp = await http.SendAsync(req, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        await manager.StopAsync(handle.MockId, TestContext.Current.CancellationToken);
    }

    private sealed class StubAuthResolver(string id, MockAuthResolution resolution) : IAuthRecordingResolver
    {
        public MockAuthResolution? TryResolve(string authRecordingId, string? workspaceId) =>
            string.Equals(authRecordingId, id, StringComparison.Ordinal) ? resolution : null;
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

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Linting;
using Kuestenlogik.Bowire.Protocol.Rest.OpenApi3;

namespace Kuestenlogik.Bowire.Protocol.Rest.Tests;

/// <summary>
/// Declared error responses reach <c>BowireMethodInfo.ErrorTypes</c> (#583),
/// and from there the <c>BWR-LINT-PII-IN-ERROR</c> rule.
/// </summary>
/// <remarks>
/// The rule was deferred in #189 because the method model had no error type to
/// read. OpenAPI is where errors are declared, so this is where the model gets
/// them — end to end, from a document to a finding, because a field discovery
/// fills and no rule reads (or the reverse) is exactly the gap that kept this
/// deferred.
/// </remarks>
public sealed class OpenApiErrorTypesTests
{
    private const string Doc = """
        {
          "openapi": "3.0.0",
          "info": { "title": "Customers", "version": "1" },
          "paths": {
            "/customers/{id}": {
              "get": {
                "operationId": "getCustomer",
                "parameters": [{ "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }],
                "responses": {
                  "200": { "description": "ok", "content": { "application/json": { "schema": {
                    "type": "object", "properties": { "id": { "type": "string" } } } } } },
                  "404": { "description": "not found", "content": { "application/problem+json": { "schema": {
                    "type": "object", "properties": { "title": { "type": "string" }, "email": { "type": "string" } } } } } },
                  "422": { "description": "invalid", "content": { "application/json": { "schema": {
                    "type": "object", "properties": { "detail": { "type": "string" }, "email": { "type": "string" } } } } } },
                  "default": { "description": "anything else", "content": { "application/json": { "schema": {
                    "type": "object", "properties": { "code": { "type": "integer" } } } } } },
                  "500": { "description": "no body declared" }
                }
              }
            },
            "/health": {
              "get": { "operationId": "health", "responses": { "200": { "description": "ok" } } }
            }
          }
        }
        """;

    private static async Task<List<Models.BowireServiceInfo>> Build(string doc)
    {
        var parsed = await OpenApiDiscovery.ParseRawAsync(doc, TestContext.Current.CancellationToken);
        return OpenApiDiscovery.BuildServices(parsed!.Document);
    }

    [Fact]
    public async Task Every_Declared_Error_With_A_Json_Body_Becomes_An_Error_Type()
    {
        var method = (await Build(Doc)).SelectMany(s => s.Methods).Single(m => m.Name == "getCustomer");

        Assert.NotNull(method.ErrorTypes);
        // 404 (problem+json counts as JSON), 422 and default — not the 500,
        // which declares no body, and never the 200.
        Assert.Equal(3, method.ErrorTypes!.Count);
        Assert.Contains(method.ErrorTypes, e => e.Name.EndsWith("404", StringComparison.Ordinal));
        Assert.Contains(method.ErrorTypes, e => e.Name.EndsWith("default", StringComparison.Ordinal));
        Assert.DoesNotContain(method.ErrorTypes, e => e.Name.EndsWith("200", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_Operation_That_Declares_No_Errors_Has_None_Not_An_Empty_List()
    {
        // Null, so a snapshot of an API without declared errors stays exactly
        // what it was before error types existed.
        var method = (await Build(Doc)).SelectMany(s => s.Methods).Single(m => m.Name == "health");
        Assert.Null(method.ErrorTypes);
    }

    [Fact]
    public async Task The_Lint_Rule_Finds_The_Email_Once_Though_Two_Errors_Carry_It()
    {
        var findings = BowireSchemaLinter.CreateDefault().Lint(await Build(Doc));

        var pii = Assert.Single(findings, f => f.RuleId == "BWR-LINT-PII-IN-ERROR");
        Assert.Equal("getCustomer", pii.Method);
        Assert.Equal("email", pii.Field);
        Assert.Equal(BowireLintSeverity.Medium, pii.Severity);
        // It is in an error, not the success response — that rule stays quiet.
        Assert.DoesNotContain(findings, f => f.RuleId == "BWR-LINT-PII-RESPONSE");
    }
}

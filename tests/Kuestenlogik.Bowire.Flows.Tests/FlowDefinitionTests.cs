// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Flows.Expectations;

namespace Kuestenlogik.Bowire.Flows.Tests;

/// <summary>
/// Round-trip + adapter coverage. The CLI runner (T2) consumes a Flow
/// JSON document with EITHER the v2.2 expectations[] array or the legacy
/// v2.1 assertions[] tuples or both side-by-side; these tests pin the
/// merge contract that
/// <see cref="FlowStep.EffectiveExpectations"/> implements so a v2.2 file
/// keeps validating against future flow files.
/// </summary>
public class FlowDefinitionTests
{
    private static readonly JsonSerializerOptions KebabEnumOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    [Fact]
    public void Deserialises_V22_ExpectationsArray()
    {
        const string json = """
        {
          "id": "flow_a",
          "name": "Demo",
          "nodes": [
            {
              "id": "node_1",
              "type": "request",
              "protocol": "rest",
              "service": "GET",
              "method": "/users/42",
              "expectations": [
                { "id":"exp_1", "kind":"status",    "operator":"equals", "expected":"200" },
                { "id":"exp_2", "kind":"body-path", "operator":"equals", "target":"$.user.id", "expected":"42" }
              ]
            }
          ]
        }
        """;
        var flow = JsonSerializer.Deserialize<FlowDefinition>(json, KebabEnumOptions);
        Assert.NotNull(flow);
        Assert.Equal("flow_a", flow!.Id);
        Assert.Single(flow.Nodes);
        var step = flow.Nodes[0];
        Assert.Equal(2, step.Expectations.Count);
        Assert.Equal(FlowExpectationKind.Status, step.Expectations[0].Kind);
        Assert.Equal(FlowExpectationOperator.Equals, step.Expectations[0].Operator);
        Assert.Equal("200", step.Expectations[0].Expected);
        Assert.Equal(FlowExpectationKind.BodyPath, step.Expectations[1].Kind);
        Assert.Equal("$.user.id", step.Expectations[1].Target);
    }

    [Fact]
    public void LegacyTuple_StatusEq_MapsToStatusEquals()
    {
        var exp = FlowExpectation.FromLegacyTuple("status", "eq", "200");
        Assert.Equal(FlowExpectationKind.Status, exp.Kind);
        Assert.Equal(FlowExpectationOperator.Equals, exp.Operator);
        Assert.Equal("200", exp.Expected);
    }

    [Fact]
    public void LegacyTuple_BodyPath_MapsAndPreservesPath()
    {
        var exp = FlowExpectation.FromLegacyTuple("$.user.name", "contains", "Ada");
        Assert.Equal(FlowExpectationKind.BodyPath, exp.Kind);
        Assert.Equal("$.user.name", exp.Target);
        Assert.Equal(FlowExpectationOperator.Contains, exp.Operator);
    }

    [Fact]
    public void LegacyTuple_DurationMs_MapsToLatency()
    {
        var exp = FlowExpectation.FromLegacyTuple("durationMs", "lt", "500");
        Assert.Equal(FlowExpectationKind.Latency, exp.Kind);
        Assert.Equal(FlowExpectationOperator.LessThan, exp.Operator);
    }

    [Fact]
    public void LegacyTuple_MissingMapsToNotExists()
    {
        var exp = FlowExpectation.FromLegacyTuple("$.error", "missing", null);
        Assert.Equal(FlowExpectationOperator.NotExists, exp.Operator);
    }

    [Fact]
    public void EffectiveExpectations_MergesV22AndLegacy()
    {
        var step = new FlowStep
        {
            Id = "node_1",
            Type = "request",
            Expectations =
            {
                new FlowExpectation { Kind = FlowExpectationKind.Status, Operator = FlowExpectationOperator.Equals, Expected = "200" },
            },
            Assertions = new()
            {
                new LegacyAssertionTuple { Path = "$.user.id", Op = "eq", Value = "42" },
            },
        };
        var effective = step.EffectiveExpectations();
        Assert.Equal(2, effective.Count);
        Assert.Equal(FlowExpectationKind.Status, effective[0].Kind);
        Assert.Equal(FlowExpectationKind.BodyPath, effective[1].Kind);
        Assert.Equal("$.user.id", effective[1].Target);
    }

    [Fact]
    public void EffectiveExpectations_EmptyWhenBothAreEmpty()
    {
        var step = new FlowStep { Id = "node_1", Type = "request" };
        Assert.Empty(step.EffectiveExpectations());
    }

    [Fact]
    public void Deserialises_FlowWithoutExpectationsArray_StillLoads()
    {
        // v2.1-era saved flow — no `expectations` field at all. Must
        // still deserialise (foundation requirement: existing flows
        // load + run unchanged).
        const string json = """
        {
          "id": "flow_legacy",
          "name": "Old",
          "nodes": [ { "id":"node_x", "type":"request", "protocol":"grpc", "service":"S", "method":"M" } ]
        }
        """;
        var flow = JsonSerializer.Deserialize<FlowDefinition>(json);
        Assert.NotNull(flow);
        Assert.Single(flow!.Nodes);
        Assert.Empty(flow.Nodes[0].Expectations);
        Assert.Null(flow.Nodes[0].Assertions);
        Assert.Empty(flow.Nodes[0].EffectiveExpectations());
    }
}

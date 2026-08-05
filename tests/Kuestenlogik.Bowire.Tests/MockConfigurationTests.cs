// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for the <see cref="MockConfiguration"/> envelope (#558): JSON
/// round-trip, forward/backward version tolerance, and the parse contract.
/// </summary>
public sealed class MockConfigurationTests
{
    private static readonly string[] AllPermissions = ["all"];

    [Fact]
    public void Parse_Empty_Or_Whitespace_Yields_Default_Config()
    {
        foreach (var input in new[] { null, "", "   ", "\t\n" })
        {
            var config = MockConfiguration.Parse(input);
            Assert.Equal(MockConfiguration.CurrentFormatVersion, config.ConfigFormatVersion);
            Assert.Empty(config.FieldOverrides);
            Assert.Empty(config.ConditionalRules);
            Assert.Null(config.Auth);
        }
    }

    [Fact]
    public void RoundTrip_Preserves_All_Arms()
    {
        var original = new MockConfiguration
        {
            Source = new MockConfigSource("openapi", "./api.yaml"),
            Auth = new MockAuthRequirement { Required = true, Scheme = "bearer", AuthRecordingId = "rec-1" },
        };
        original.FieldOverrides.Add(new MockFieldOverride
        {
            Service = "Orders",
            Method = "list",
            JsonPath = "$.total",
            Value = JsonSerializer.SerializeToElement(42),
        });
        original.ConditionalRules.Add(new MockConditionalRule
        {
            Service = "Orders",
            Method = "get",
            When = new MockRulePredicate { JsonPath = "$.role", EqualTo = "admin" },
            Response = JsonSerializer.SerializeToElement(new { permissions = AllPermissions }),
        });

        var reparsed = MockConfiguration.Parse(original.ToJson());

        Assert.Equal("openapi", reparsed.Source!.Kind);
        Assert.Equal("./api.yaml", reparsed.Source.Path);
        Assert.True(reparsed.Auth!.Required);
        Assert.Equal("bearer", reparsed.Auth.Scheme);
        Assert.Equal("rec-1", reparsed.Auth.AuthRecordingId);

        var ov = Assert.Single(reparsed.FieldOverrides);
        Assert.Equal("Orders", ov.Service);
        Assert.Equal("$.total", ov.JsonPath);
        Assert.Equal(42, ov.Value!.Value.GetInt32());

        var rule = Assert.Single(reparsed.ConditionalRules);
        Assert.Equal("get", rule.Method);
        Assert.Equal("admin", rule.When!.EqualTo);
        Assert.Equal("all", rule.Response!.Value.GetProperty("permissions")[0].GetString());
    }

    [Fact]
    public void Parse_Absent_Version_Defaults_To_Current_Backward_Tolerance()
    {
        // A document written before configFormatVersion existed.
        var config = MockConfiguration.Parse("""{"fieldOverrides":[]}""");
        Assert.Equal(MockConfiguration.CurrentFormatVersion, config.ConfigFormatVersion);
    }

    [Fact]
    public void Parse_NonPositive_Version_Is_Normalised()
    {
        var config = MockConfiguration.Parse("""{"configFormatVersion":0}""");
        Assert.Equal(MockConfiguration.CurrentFormatVersion, config.ConfigFormatVersion);
    }

    [Fact]
    public void Parse_Newer_Version_With_Unknown_Fields_Loads_Best_Effort_Forward_Tolerance()
    {
        // A v99 document from a future build with an unknown "futureArm" —
        // System.Text.Json ignores the unknown field, the known arms load,
        // and the higher version is preserved (not clobbered).
        var config = MockConfiguration.Parse(
            """{"configFormatVersion":99,"futureArm":{"x":1},"fieldOverrides":[{"jsonPath":"$.a","value":1}]}""");

        Assert.Equal(99, config.ConfigFormatVersion);
        var ov = Assert.Single(config.FieldOverrides);
        Assert.Equal("$.a", ov.JsonPath);
    }

    [Fact]
    public void Parse_Explicit_Null_Collection_Throws()
    {
        // System.Text.Json's setter runs for an explicit null token, defeating
        // the constructor initializer. Rather than return a config with a null
        // collection (which would NRE a consumer), Parse rejects it — so a
        // successful Parse always yields non-null collections.
        Assert.ThrowsAny<JsonException>(
            () => MockConfiguration.Parse("""{"fieldOverrides":null}"""));
        Assert.ThrowsAny<JsonException>(
            () => MockConfiguration.Parse("""{"conditionalRules":null}"""));
    }

    [Fact]
    public void Parse_Absent_Collections_Are_Non_Null_Empty()
    {
        var config = MockConfiguration.Parse("""{"configFormatVersion":1}""");
        Assert.Empty(config.FieldOverrides);
        Assert.Empty(config.ConditionalRules);
    }

    [Fact]
    public void Parse_Invalid_Json_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => MockConfiguration.Parse("{not json"));
    }

    [Fact]
    public void Parse_Json_Array_Throws()
    {
        // A JSON array is not a MockConfiguration object.
        Assert.ThrowsAny<JsonException>(() => MockConfiguration.Parse("[1,2,3]"));
    }

    [Fact]
    public void FieldOverride_Absent_And_Json_Null_Value_Both_Deserialise_To_Null()
    {
        // System.Text.Json maps a JSON null to a CLR null for JsonElement?,
        // so an absent value and an explicit "value": null are the same —
        // both null, both a no-op at apply time (see MockFieldOverride.Value).
        var absent = MockConfiguration.Parse("""{"fieldOverrides":[{"jsonPath":"$.a"}]}""");
        Assert.Null(absent.FieldOverrides[0].Value);

        var explicitNull = MockConfiguration.Parse("""{"fieldOverrides":[{"jsonPath":"$.a","value":null}]}""");
        Assert.Null(explicitNull.FieldOverrides[0].Value);
    }
}

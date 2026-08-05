// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for <see cref="MockConfigApplier"/> (#558) — per-field response
/// overrides applied onto a recording's steps by (service, method) scope
/// and JSONPath, using the same segment syntax as the mock body matchers.
/// </summary>
public sealed class MockConfigApplierTests
{
    private static readonly string[] Tags = ["x", "y"];

    private static BowireRecording OneStep(string service, string method, string response)
        => new()
        {
            Steps =
            {
                new BowireRecordingStep
                {
                    Protocol = "rest",
                    Service = service,
                    Method = method,
                    Response = response,
                },
            },
        };

    private static MockConfiguration WithOverride(string? service, string? method, string jsonPath, object? value)
    {
        var config = new MockConfiguration();
        config.FieldOverrides.Add(new MockFieldOverride
        {
            Service = service,
            Method = method,
            JsonPath = jsonPath,
            Value = JsonSerializer.SerializeToElement(value),
        });
        return config;
    }

    private static string Response(BowireRecording r) => r.Steps[0].Response!;

    [Fact]
    public void Applies_TopLevel_Override()
    {
        var rec = OneStep("Svc", "m", """{"status":"pending","n":1}""");
        MockConfigApplier.Apply(rec, WithOverride("Svc", "m", "$.status", "shipped"));

        using var doc = JsonDocument.Parse(Response(rec));
        Assert.Equal("shipped", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("n").GetInt32());
    }

    [Fact]
    public void Applies_Nested_Path()
    {
        var rec = OneStep("Svc", "m", """{"user":{"role":"guest"}}""");
        MockConfigApplier.Apply(rec, WithOverride("Svc", "m", "user.role", "admin"));

        using var doc = JsonDocument.Parse(Response(rec));
        Assert.Equal("admin", doc.RootElement.GetProperty("user").GetProperty("role").GetString());
    }

    [Fact]
    public void Applies_Array_Index_Path()
    {
        var rec = OneStep("Svc", "m", """{"items":[{"sku":"a"},{"sku":"b"}]}""");
        MockConfigApplier.Apply(rec, WithOverride("Svc", "m", "items[0].sku", "OVERRIDDEN"));

        using var doc = JsonDocument.Parse(Response(rec));
        Assert.Equal("OVERRIDDEN", doc.RootElement.GetProperty("items")[0].GetProperty("sku").GetString());
        Assert.Equal("b", doc.RootElement.GetProperty("items")[1].GetProperty("sku").GetString());
    }

    [Fact]
    public void Creates_Missing_Intermediate_Objects()
    {
        var rec = OneStep("Svc", "m", "{}");
        MockConfigApplier.Apply(rec, WithOverride("Svc", "m", "$.a.b.c", 7));

        using var doc = JsonDocument.Parse(Response(rec));
        Assert.Equal(7, doc.RootElement.GetProperty("a").GetProperty("b").GetProperty("c").GetInt32());
    }

    [Fact]
    public void Applies_Object_And_Array_Values()
    {
        var rec = OneStep("Svc", "m", """{"payload":null}""");
        MockConfigApplier.Apply(rec, WithOverride("Svc", "m", "$.payload", new { id = 3, tags = Tags }));

        using var doc = JsonDocument.Parse(Response(rec));
        var payload = doc.RootElement.GetProperty("payload");
        Assert.Equal(3, payload.GetProperty("id").GetInt32());
        Assert.Equal("y", payload.GetProperty("tags")[1].GetString());
    }

    [Fact]
    public void Null_Override_Value_Is_NoOp()
    {
        // A JSON-null value is treated as "no override" (see the applier +
        // MockFieldOverride.Value contract) — the field keeps its value.
        var rec = OneStep("Svc", "m", """{"token":"secret"}""");
        var config = new MockConfiguration();
        config.FieldOverrides.Add(new MockFieldOverride
        {
            Service = "Svc",
            Method = "m",
            JsonPath = "$.token",
            Value = JsonDocument.Parse("null").RootElement,
        });
        MockConfigApplier.Apply(rec, config);

        using var doc = JsonDocument.Parse(Response(rec));
        Assert.Equal("secret", doc.RootElement.GetProperty("token").GetString());
    }

    [Fact]
    public void Wildcard_Service_And_Method_Match_Any_Step()
    {
        var rec = OneStep("AnyService", "anyMethod", """{"v":1}""");
        MockConfigApplier.Apply(rec, WithOverride(null, "*", "$.v", 99));

        using var doc = JsonDocument.Parse(Response(rec));
        Assert.Equal(99, doc.RootElement.GetProperty("v").GetInt32());
    }

    [Fact]
    public void Scoped_Override_Skips_NonMatching_Step()
    {
        var rec = OneStep("Orders", "list", """{"v":1}""");
        MockConfigApplier.Apply(rec, WithOverride("Users", "list", "$.v", 99));

        using var doc = JsonDocument.Parse(Response(rec));
        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32()); // untouched
    }

    [Fact]
    public void Unresolvable_Path_Leaves_Response_Unchanged()
    {
        // Object-segment path against an array root can't resolve → no-op.
        var rec = OneStep("Svc", "m", """[1,2,3]""");
        MockConfigApplier.Apply(rec, WithOverride("Svc", "m", "$.field", 1));

        Assert.Equal("""[1,2,3]""", Response(rec));
    }

    [Fact]
    public void Unparseable_Response_Is_Left_Untouched()
    {
        var rec = OneStep("Svc", "m", "not-json");
        MockConfigApplier.Apply(rec, WithOverride("Svc", "m", "$.a", 1));

        Assert.Equal("not-json", Response(rec));
    }

    [Fact]
    public void Null_Or_Empty_Config_Returns_Recording_Unchanged()
    {
        var rec = OneStep("Svc", "m", """{"v":1}""");
        Assert.Same(rec, MockConfigApplier.Apply(rec, null));
        Assert.Same(rec, MockConfigApplier.Apply(rec, new MockConfiguration()));
        Assert.Equal("""{"v":1}""", Response(rec));
    }

    [Fact]
    public void Absent_Override_Value_Is_Skipped()
    {
        var rec = OneStep("Svc", "m", """{"v":1}""");
        var config = new MockConfiguration();
        config.FieldOverrides.Add(new MockFieldOverride { Service = "Svc", Method = "m", JsonPath = "$.v" });
        MockConfigApplier.Apply(rec, config);

        Assert.Equal("""{"v":1}""", Response(rec)); // no value → no change
    }

    [Fact]
    public void Applies_At_Array_Index_Leaf()
    {
        // The leaf segment is itself an array index — exercises SetAtPath's
        // array-leaf write branch (not just an object-property leaf).
        var rec = OneStep("Svc", "m", """{"tags":["x","y"]}""");
        MockConfigApplier.Apply(rec, WithOverride("Svc", "m", "tags[0]", "Z"));

        using var doc = JsonDocument.Parse(Response(rec));
        Assert.Equal("Z", doc.RootElement.GetProperty("tags")[0].GetString());
        Assert.Equal("y", doc.RootElement.GetProperty("tags")[1].GetString());
    }

    [Fact]
    public void OutOfRange_Intermediate_Index_Is_NoOp_Does_Not_Grow_Array()
    {
        var rec = OneStep("Svc", "m", """{"items":[{"sku":"a"}]}""");
        MockConfigApplier.Apply(rec, WithOverride("Svc", "m", "items[5].sku", "b"));

        using var doc = JsonDocument.Parse(Response(rec));
        Assert.Equal(1, doc.RootElement.GetProperty("items").GetArrayLength()); // not grown
        Assert.Equal("a", doc.RootElement.GetProperty("items")[0].GetProperty("sku").GetString());
    }

    [Fact]
    public void OutOfRange_Leaf_Index_Is_NoOp_Does_Not_Grow_Array()
    {
        var rec = OneStep("Svc", "m", """{"tags":["x"]}""");
        MockConfigApplier.Apply(rec, WithOverride("Svc", "m", "tags[9]", "z"));

        using var doc = JsonDocument.Parse(Response(rec));
        Assert.Equal(1, doc.RootElement.GetProperty("tags").GetArrayLength()); // not grown
        Assert.Equal("x", doc.RootElement.GetProperty("tags")[0].GetString());
    }

    [Fact]
    public void Null_FieldOverrides_List_Does_Not_Throw()
    {
        // Parse rejects an explicit-null collection, but a config constructed
        // outside Parse could still carry a null list — Apply guards
        // defensively (no NullReferenceException, recording untouched).
        var rec = OneStep("Svc", "m", """{"v":1}""");
        var config = new MockConfiguration { FieldOverrides = null! };
        var result = MockConfigApplier.Apply(rec, config);

        Assert.Same(rec, result);
        Assert.Equal("""{"v":1}""", Response(rec));
    }
}

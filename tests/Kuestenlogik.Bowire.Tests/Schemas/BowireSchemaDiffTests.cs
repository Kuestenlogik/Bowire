// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Schemas;

namespace Kuestenlogik.Bowire.Tests.Schemas;

/// <summary>
/// Unit coverage for <see cref="BowireSchemaDiff"/> — the base→head schema
/// delta that backs `bowire diff` and the v2.5 PR bot. Mirrors the classifier
/// discipline of the workbench's #185 schema-watch: additions and pure
/// deprecation/prose edits are NOT breaking, a field reorder is NOT a change,
/// and only a moved callable facet counts as a signature change.
/// </summary>
public sealed class BowireSchemaDiffTests
{
    [Fact]
    public void Identical_snapshots_produce_an_empty_delta()
    {
        var before = new[] { Svc("Users", Method("GetUser")) };
        var after = new[] { Svc("Users", Method("GetUser")) };

        var delta = BowireSchemaDiff.Compute(before, after);

        Assert.True(delta.IsEmpty);
        Assert.False(delta.CallableMoved);
        Assert.False(delta.HasBreakingChanges);
        Assert.Equal("schema identical", delta.Summary());
    }

    [Fact]
    public void Added_service_is_reported_without_listing_its_methods()
    {
        var before = new[] { Svc("Users", Method("GetUser")) };
        var after = new[] { Svc("Users", Method("GetUser")), Svc("Orders", Method("GetOrder")) };

        var delta = BowireSchemaDiff.Compute(before, after);

        Assert.Equal("Orders", Assert.Single(delta.AddedServices));
        Assert.Empty(delta.AddedMethods);
        Assert.True(delta.CallableMoved);
        Assert.False(delta.HasBreakingChanges);
    }

    [Fact]
    public void Removed_service_is_breaking()
    {
        var before = new[] { Svc("Users", Method("GetUser")), Svc("Orders", Method("GetOrder")) };
        var after = new[] { Svc("Users", Method("GetUser")) };

        var delta = BowireSchemaDiff.Compute(before, after);

        Assert.Equal("Orders", Assert.Single(delta.RemovedServices));
        Assert.True(delta.HasBreakingChanges);
    }

    [Fact]
    public void Added_method_is_not_breaking()
    {
        var before = new[] { Svc("Users", Method("GetUser")) };
        var after = new[] { Svc("Users", Method("GetUser"), Method("CreateUser")) };

        var delta = BowireSchemaDiff.Compute(before, after);

        var added = Assert.Single(delta.AddedMethods);
        Assert.Equal("Users", added.Service);
        Assert.Equal("CreateUser", added.Method);
        Assert.False(delta.HasBreakingChanges);
    }

    [Fact]
    public void Removed_method_is_breaking()
    {
        var before = new[] { Svc("Users", Method("GetUser"), Method("DeleteUser")) };
        var after = new[] { Svc("Users", Method("GetUser")) };

        var delta = BowireSchemaDiff.Compute(before, after);

        Assert.Equal("DeleteUser", Assert.Single(delta.RemovedMethods).Method);
        Assert.True(delta.HasBreakingChanges);
    }

    [Fact]
    public void Request_shape_change_is_a_breaking_signature_change()
    {
        var before = new[] { Svc("Users", Method("GetUser", input: Msg("Req", Field("id")))) };
        var after = new[] { Svc("Users", Method("GetUser", input: Msg("Req", Field("id"), Field("tenant")))) };

        var delta = BowireSchemaDiff.Compute(before, after);

        var change = Assert.Single(delta.ChangedMethods);
        Assert.Equal("signature", change.Kind);
        Assert.Contains("request shape changed", change.Detail, StringComparison.Ordinal);
        Assert.True(delta.HasBreakingChanges);
    }

    [Fact]
    public void Route_change_is_reported_with_both_routes()
    {
        var before = new[] { Svc("Users", Method("GetUser", httpMethod: "GET", httpPath: "/users/{id}")) };
        var after = new[] { Svc("Users", Method("GetUser", httpMethod: "POST", httpPath: "/users/{id}")) };

        var delta = BowireSchemaDiff.Compute(before, after);

        var change = Assert.Single(delta.ChangedMethods);
        Assert.Equal("signature", change.Kind);
        Assert.Contains("route GET /users/{id} -> POST /users/{id}", change.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Field_reorder_alone_is_not_a_change()
    {
        var before = new[] { Svc("Users", Method("GetUser", input: Msg("Req", Field("a"), Field("b")))) };
        var after = new[] { Svc("Users", Method("GetUser", input: Msg("Req", Field("b"), Field("a")))) };

        var delta = BowireSchemaDiff.Compute(before, after);

        Assert.True(delta.IsEmpty);
    }

    [Fact]
    public void Deprecation_flip_is_a_deprecation_change_and_not_breaking()
    {
        var before = new[] { Svc("Users", Method("GetUser", deprecated: false)) };
        var after = new[] { Svc("Users", Method("GetUser", deprecated: true)) };

        var delta = BowireSchemaDiff.Compute(before, after);

        var change = Assert.Single(delta.ChangedMethods);
        Assert.Equal("deprecation", change.Kind);
        Assert.Equal("marked deprecated", change.Detail);
        Assert.True(delta.CallableMoved);
        Assert.False(delta.HasBreakingChanges);
    }

    [Fact]
    public void Description_only_edit_is_an_annotation_and_does_not_move_the_callable_surface()
    {
        var before = new[] { Svc("Users", Method("GetUser", summary: "Get a user")) };
        var after = new[] { Svc("Users", Method("GetUser", summary: "Fetch a user by id")) };

        var delta = BowireSchemaDiff.Compute(before, after);

        var note = Assert.Single(delta.AnnotatedMethods);
        Assert.Equal("annotation", note.Kind);
        Assert.Empty(delta.ChangedMethods);
        Assert.False(delta.CallableMoved);
        Assert.False(delta.HasBreakingChanges);
        Assert.False(delta.IsEmpty);
    }

    [Fact]
    public void Summary_names_each_populated_bucket()
    {
        var before = new[] { Svc("Users", Method("GetUser"), Method("DeleteUser")) };
        var after = new[]
        {
            Svc("Users", Method("GetUser", httpMethod: "POST", httpPath: "/u"), Method("CreateUser")),
            Svc("Orders", Method("GetOrder")),
        };

        var delta = BowireSchemaDiff.Compute(before, after);

        // +1 service (Orders), +1 method (CreateUser), -1 method (DeleteUser),
        // ~1 changed (GetUser gained a route).
        Assert.Equal("+1 service, +1 method, -1 method, ~1 changed", delta.Summary());
    }

    // ---- builders -------------------------------------------------------

    private static BowireServiceInfo Svc(string name, params BowireMethodInfo[] methods)
        => new(name, "pkg", [.. methods]);

    private static BowireMethodInfo Method(
        string name,
        string methodType = "unary",
        string? httpMethod = null,
        string? httpPath = null,
        bool deprecated = false,
        string? summary = null,
        BowireMessageInfo? input = null,
        BowireMessageInfo? output = null)
        => new(name, name, ClientStreaming: false, ServerStreaming: false,
               input ?? Msg("In"), output ?? Msg("Out"), methodType)
        {
            HttpMethod = httpMethod,
            HttpPath = httpPath,
            Deprecated = deprecated,
            Summary = summary,
        };

    private static BowireMessageInfo Msg(string name, params BowireFieldInfo[] fields)
        => new(name, name, [.. fields]);

    private static BowireFieldInfo Field(string name, string type = "string", bool required = false)
        => new(name, 0, type, "", IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null)
        {
            Required = required,
        };
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.GraphQL.Tests;

/// <summary>
/// The selection set a generated operation asks for (#710).
/// </summary>
/// <remarks>
/// <para>
/// It used to be <c>__typename</c> and nothing else. Inside the workbench
/// that went unnoticed, because the UI has its own field picker and sends a
/// finished query; the generated operation is what the callers *without* a
/// UI get — the CLI, flows, contract tests. They were told the name of the
/// type and nothing about the data.
/// </para>
/// <para>
/// The fallback is still here and still load-bearing: an empty selection
/// set is a syntax error, so a type with nothing known about it has to
/// select something.
/// </para>
/// </remarks>
public sealed class GraphQLSelectionSetTests
{
    private static BowireFieldInfo Scalar(string name, string type = "string") =>
        new(name, 1, type, "optional", IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null);

    private static BowireFieldInfo Nested(string name, BowireMessageInfo shape) =>
        new(name, 1, "message", "optional", IsMap: false, IsRepeated: false, MessageType: shape, EnumValues: null);

    private static BowireMethodInfo Method(BowireMessageInfo output, params BowireFieldInfo[] args) =>
        new(
            Name: "berth",
            FullName: "Query/berth",
            ClientStreaming: false,
            ServerStreaming: false,
            InputType: new BowireMessageInfo("berthVariables", "berthVariables", [.. args]),
            OutputType: output,
            MethodType: "Unary");

    [Fact]
    public void Scalar_Fields_Are_Selected_By_Name()
    {
        var (operation, _) = GraphQLQueryBuilder.Build("query",
            Method(new BowireMessageInfo("Berth", "Berth", [Scalar("id"), Scalar("name")])), "{}");

        Assert.Contains("id", operation, StringComparison.Ordinal);
        Assert.Contains("name", operation, StringComparison.Ordinal);
        // The point of the change: the name of the type is no longer the
        // only thing the caller gets back.
        Assert.DoesNotContain("__typename", operation, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Object_Field_Is_Recursed_Into()
    {
        var vessel = new BowireMessageInfo("Vessel", "Vessel", [Scalar("imo"), Scalar("flag")]);
        var (operation, _) = GraphQLQueryBuilder.Build("query",
            Method(new BowireMessageInfo("Berth", "Berth", [Scalar("id"), Nested("vessel", vessel)])), "{}");

        Assert.Contains("vessel {", operation, StringComparison.Ordinal);
        Assert.Contains("imo", operation, StringComparison.Ordinal);
        Assert.Contains("flag", operation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Type_With_No_Fields_Still_Selects_Something()
    {
        // An empty selection set is a syntax error, so this is what keeps
        // the generated operation valid at all.
        var (operation, _) = GraphQLQueryBuilder.Build("query",
            Method(new BowireMessageInfo("Result", "Result", [])), "{}");

        Assert.Contains("__typename", operation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Self_Referential_Type_Stops_Rather_Than_Generating_Forever()
    {
        // A Node whose children are Nodes. Discovery hands these back with
        // the cycle already closed, but the depth cap is what makes that
        // safe rather than lucky.
        var node = new BowireMessageInfo("Node", "Node", [Scalar("id")]);
        var level3 = new BowireMessageInfo("Node", "Node", [Scalar("id"), Nested("child", node)]);
        var level2 = new BowireMessageInfo("Node", "Node", [Scalar("id"), Nested("child", level3)]);
        var level1 = new BowireMessageInfo("Node", "Node", [Scalar("id"), Nested("child", level2)]);

        var (operation, _) = GraphQLQueryBuilder.Build("query", Method(level1), "{}");

        // Three levels of `child {`, then the cap turns the fourth into
        // __typename instead of another nesting.
        Assert.Equal(3, operation.Split("child {").Length - 1);
        Assert.Contains("__typename", operation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Generated_Operation_Still_Types_Its_Variables()
    {
        // The selection set is new; the argument half must not have moved.
        var (operation, _) = GraphQLQueryBuilder.Build("query",
            Method(new BowireMessageInfo("Berth", "Berth", [Scalar("id")]),
                   new BowireFieldInfo("id", 1, "string", "required",
                       IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null)
                   { Required = true }),
            """{"id":"b1"}""");

        Assert.Contains("$id: String!", operation, StringComparison.Ordinal);
        Assert.Contains("id: $id", operation, StringComparison.Ordinal);
    }
}

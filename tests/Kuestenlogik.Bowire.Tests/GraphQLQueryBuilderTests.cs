// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.GraphQL;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests that the GraphQL query builder produces operation strings whose
/// shape matches the discovered method's argument list (NON_NULL, repeated,
/// scalar mapping back to GraphQL types).
/// </summary>
public class GraphQLQueryBuilderTests
{
    private static BowireMethodInfo Method(string name, params BowireFieldInfo[] args) =>
        new(
            Name: name,
            FullName: "Query/" + name,
            ClientStreaming: false,
            ServerStreaming: false,
            InputType: new BowireMessageInfo(name + "Variables", name + "Variables", [..args]),
            OutputType: new BowireMessageInfo("Result", "Result", []),
            MethodType: "Unary");

    private static BowireFieldInfo Field(string name, string type, bool required = false, bool repeated = false) =>
        new(
            Name: name,
            Number: 1,
            Type: type,
            Label: required ? "required" : "optional",
            IsMap: false,
            IsRepeated: repeated,
            MessageType: null,
            EnumValues: null)
        {
            Required = required
        };

    [Fact]
    public void Build_NoArguments_EmitsBareOperation()
    {
        var (operation, _) = GraphQLQueryBuilder.Build("query", Method("listBooks"), "{}");

        Assert.Contains("query listBooks {", operation, StringComparison.Ordinal);
        Assert.Contains("listBooks {", operation, StringComparison.Ordinal);
        Assert.Contains("__typename", operation, StringComparison.Ordinal);
        // No parameter list expected when there are no arguments
        Assert.DoesNotContain("$", operation, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RequiredScalar_EmitsTypedParameter()
    {
        var method = Method("getBook", Field("id", "string", required: true));
        var (operation, _) = GraphQLQueryBuilder.Build("query", method, "{\"id\":\"abc\"}");

        Assert.Contains("query getBook($id: String!)", operation, StringComparison.Ordinal);
        Assert.Contains("getBook(id: $id)", operation, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RepeatedRequiredInt_EmitsListWithInnerNonNull()
    {
        var method = Method("page", Field("ids", "int32", required: true, repeated: true));
        var (operation, _) = GraphQLQueryBuilder.Build("query", method, "{\"ids\":[1,2]}");

        // [Int!]! — outer non-null because the field is required, inner
        // non-null because we always tag list items as required.
        Assert.Contains("$ids: [Int!]!", operation, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_MutationKind_EmitsMutationKeyword()
    {
        var method = Method("addBook",
            Field("title", "string", required: true),
            Field("year", "int32"));
        var (operation, _) = GraphQLQueryBuilder.Build("mutation", method, "{\"title\":\"X\",\"year\":2024}");

        Assert.StartsWith("mutation addBook(", operation, StringComparison.Ordinal);
        Assert.Contains("$title: String!", operation, StringComparison.Ordinal);
        Assert.Contains("$year: Int", operation, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_MalformedVariablesJson_FallsBackToEmptyObject()
    {
        // The caller may pass an unparseable variables payload (e.g.
        // when the user is mid-edit). The builder should still emit a
        // valid operation and return an empty {} variables element.
        var method = Method("getBook", Field("id", "string", required: true));

        var (operation, variables) = GraphQLQueryBuilder.Build("query", method, "{ broken");

        Assert.Contains("query getBook($id: String!)", operation, StringComparison.Ordinal);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, variables.ValueKind);
        Assert.False(variables.EnumerateObject().Any());
    }

    [Fact]
    public void Build_WhitespaceVariables_StillEmitsValidOperation()
    {
        // Empty / whitespace variables JSON triggers the IsNullOrWhiteSpace
        // branch which short-circuits to "{}".
        var method = Method("ping");

        var (operation, variables) = GraphQLQueryBuilder.Build("query", method, "");

        Assert.Contains("query ping {", operation, StringComparison.Ordinal);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, variables.ValueKind);
    }

    [Theory]
    [InlineData("int32", "Int")]
    [InlineData("int64", "Int")]
    [InlineData("uint32", "Int")]
    [InlineData("uint64", "Int")]
    [InlineData("float", "Float")]
    [InlineData("double", "Float")]
    [InlineData("bool", "Boolean")]
    [InlineData("string", "String")]
    [InlineData("custom-future", "String")]
    public void Build_ScalarTypeMapping_EmitsExpectedGraphQLTypeName(string bowireType, string expected)
    {
        var method = Method("op", Field("v", bowireType));

        var (operation, _) = GraphQLQueryBuilder.Build("query", method, "{}");

        Assert.Contains("$v: " + expected, operation, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_MessageField_WithMessageType_EmitsTypeName()
    {
        var nested = new BowireMessageInfo("UserInput", "UserInput", []);
        var input = new BowireFieldInfo(
            Name: "input",
            Number: 1,
            Type: "message",
            Label: "required",
            IsMap: false,
            IsRepeated: false,
            MessageType: nested,
            EnumValues: null)
        {
            Required = true
        };
        var method = new BowireMethodInfo(
            Name: "createUser",
            FullName: "Mutation/createUser",
            ClientStreaming: false,
            ServerStreaming: false,
            InputType: new BowireMessageInfo("Variables", "Variables", [input]),
            OutputType: new BowireMessageInfo("Result", "Result", []),
            MethodType: "Unary");

        var (operation, _) = GraphQLQueryBuilder.Build("mutation", method, "{\"input\":{}}");

        Assert.Contains("$input: UserInput!", operation, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_MessageField_WithoutMessageType_FallsBackToJsonScalar()
    {
        // The fallback when MessageType is null — used when discovery
        // couldn't resolve the input object's name.
        var input = new BowireFieldInfo(
            Name: "blob",
            Number: 1,
            Type: "message",
            Label: "optional",
            IsMap: false,
            IsRepeated: false,
            MessageType: null,
            EnumValues: null);
        var method = new BowireMethodInfo(
            Name: "save",
            FullName: "Mutation/save",
            ClientStreaming: false,
            ServerStreaming: false,
            InputType: new BowireMessageInfo("Variables", "Variables", [input]),
            OutputType: new BowireMessageInfo("Result", "Result", []),
            MethodType: "Unary");

        var (operation, _) = GraphQLQueryBuilder.Build("mutation", method, "{\"blob\":{}}");

        Assert.Contains("$blob: JSON", operation, StringComparison.Ordinal);
    }
}

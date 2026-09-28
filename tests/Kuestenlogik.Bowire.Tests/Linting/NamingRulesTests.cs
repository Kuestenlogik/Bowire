// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Linting;
using Kuestenlogik.Bowire.Linting.Rules;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Tests.Linting;

/// <summary>
/// The naming rules (#583) judge consistency, not a style.
/// </summary>
/// <remarks>
/// A fixed convention was deferred in #189 because it is protocol-sensitive:
/// gRPC methods are PascalCase, REST operation ids camelCase, protobuf fields
/// snake_case. What these pin is that each of those passes on its own, that a
/// surface mixing them is found, and that the names which cannot tell — one
/// word, an acronym, a synthesised REST name — neither vote nor get flagged.
/// </remarks>
public sealed class NamingRulesTests
{
    // ---- reading a name ----

    [Theory]
    [InlineData("GetOrder", "PascalCase")]
    [InlineData("getOrderById", "CamelCase")]
    [InlineData("get_order", "SnakeCase")]
    [InlineData("created_at", "SnakeCase")]
    [InlineData("get-order", "KebabCase")]
    [InlineData("MAX_PAGE_SIZE", "ScreamingSnakeCase")]
    [InlineData("HTTPStatus", "PascalCase")]
    [InlineData("getHTTPStatus", "CamelCase")]
    [InlineData("order2", "None")]            // one lower-case word
    public void A_Name_Reads_As_The_Convention_It_Follows(string name, string expected)
        => Assert.Equal(expected, NamingConvention.Classify(name).ToString());

    [Theory]
    [InlineData("status")]                // camel, snake and kebab all at once
    [InlineData("ID")]                    // Pascal and SCREAMING
    [InlineData("GET_/pets/{id}")]        // synthesised for a REST operation without an operationId
    [InlineData("Mixed_Case")]
    [InlineData("half-Kebab")]
    [InlineData("")]
    [InlineData(null)]
    public void A_Name_That_Cannot_Tell_Does_Not_Vote(string? name)
        => Assert.Equal(NamingStyle.None, NamingConvention.Classify(name));

    // ---- methods ----

    [Fact]
    public void A_Grpc_Service_In_PascalCase_Passes()
        => Assert.Empty(new MixedMethodNamingRule().Inspect(Svc(Method("GetOrder"), Method("ListOrders"), Method("CancelOrder"))));

    [Fact]
    public void A_Rest_Service_In_CamelCase_Passes()
        => Assert.Empty(new MixedMethodNamingRule().Inspect(Svc(Method("getOrder"), Method("listOrders"), Method("cancelOrder"))));

    [Fact]
    public void The_Odd_One_Out_Is_Named_Against_The_Majority()
    {
        var finding = Assert.Single(new MixedMethodNamingRule().Inspect(
            Svc(Method("GetOrder"), Method("ListOrders"), Method("cancelOrder"))));

        Assert.Equal("cancelOrder", finding.Method);
        Assert.Contains("camelCase", finding.Message, StringComparison.Ordinal);
        Assert.Contains("PascalCase", finding.Message, StringComparison.Ordinal);
        Assert.Contains("2 PascalCase, 1 camelCase", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_A_Majority_It_Is_Said_Once_For_The_Service_Not_Pinned_On_One_Side()
    {
        // Naming either half the outlier would be a coin toss.
        var finding = Assert.Single(new MixedMethodNamingRule().Inspect(
            Svc(Method("GetOrder"), Method("ListOrders"), Method("cancelOrder"), Method("refundOrder"))));

        Assert.Null(finding.Method);
        Assert.Contains("no majority", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void One_Word_Names_Neither_Vote_Nor_Get_Flagged()
    {
        // "get" and "list" fit every lower-case convention. Counting them would
        // let the single longer name decide what the service "is".
        Assert.Empty(new MixedMethodNamingRule().Inspect(
            Svc(Method("get"), Method("list"), Method("cancelOrder"))));
    }

    // ---- fields ----

    [Fact]
    public void A_Payload_Mixing_Created_At_And_UpdatedAt_Is_Found()
    {
        var output = Msg("Order", Field("order_id"), Field("created_at"), Field("updatedAt"));
        var finding = Assert.Single(new MixedFieldNamingRule().Inspect(Svc(Method("GetOrder", output: output))));

        Assert.Equal("updatedAt", finding.Field);
        Assert.Equal("GetOrder", finding.Method);
    }

    [Fact]
    public void Request_And_Response_Are_Measured_Together()
    {
        // A client writes one mapping for the whole service.
        var input = Msg("Req", Field("orderId"), Field("customerId"));
        var output = Msg("Res", Field("order_status"));
        var finding = Assert.Single(new MixedFieldNamingRule().Inspect(Svc(Method("GetOrder", input, output))));
        Assert.Equal("order_status", finding.Field);
    }

    [Fact]
    public void Headers_Do_Not_Vote_Because_Kebab_Case_Is_The_Transports_Convention()
    {
        var input = Msg("Req",
            Field("orderId"), Field("customerId"),
            Field("x-api-key") with { Source = "header" },
            Field("x-request-id") with { Source = "header" },
            Field("session-token") with { Source = "cookie" });
        Assert.Empty(new MixedFieldNamingRule().Inspect(Svc(Method("getOrder", input))));
    }

    [Fact]
    public void A_Field_Shared_By_Many_Messages_Is_One_Vote_And_One_Finding()
    {
        var shared = Field("createdAt");
        var a = Msg("A", Field("order_id"), Field("line_total"), shared);
        var b = Msg("B", Field("order_id"), Field("line_total"), shared);
        var finding = Assert.Single(new MixedFieldNamingRule().Inspect(
            Svc(Method("one", output: a), Method("two", output: b))));
        Assert.Equal("createdAt", finding.Field);
    }

    [Fact]
    public void Nested_Messages_Count()
    {
        var address = Msg("Address", Field("postalCode"));
        var output = Msg("Customer", Field("first_name"), Field("last_name"), FieldMsg("home_address", address));
        var finding = Assert.Single(new MixedFieldNamingRule().Inspect(Svc(Method("getCustomer", output: output))));
        Assert.Equal("postalCode", finding.Field);
    }

    // ---- wiring ----

    [Fact]
    public void Both_Rules_Ship_Built_In_At_Info()
    {
        var linter = BowireSchemaLinter.CreateDefault();
        Assert.Contains("BWR-LINT-MIXED-METHOD-NAMING", linter.RuleIds);
        Assert.Contains("BWR-LINT-MIXED-FIELD-NAMING", linter.RuleIds);
        Assert.Equal(BowireLintSeverity.Info, new MixedMethodNamingRule().Severity);
        Assert.Equal(BowireLintSeverity.Info, new MixedFieldNamingRule().Severity);
    }

    // ---- builders ----

    private static BowireServiceInfo Svc(params BowireMethodInfo[] methods) => new("svc", "pkg", [.. methods]);

    private static BowireMethodInfo Method(string name, BowireMessageInfo? input = null, BowireMessageInfo? output = null)
        => new(name, name, ClientStreaming: false, ServerStreaming: false,
               input ?? Msg("In"), output ?? Msg("Out"), "unary");

    private static BowireMessageInfo Msg(string name, params BowireFieldInfo[] fields) => new(name, name, [.. fields]);

    private static BowireFieldInfo Field(string name)
        => new(name, 0, "string", "", IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null);

    private static BowireFieldInfo FieldMsg(string name, BowireMessageInfo type)
        => new(name, 0, "message", "", IsMap: false, IsRepeated: false, MessageType: type, EnumValues: null);
}

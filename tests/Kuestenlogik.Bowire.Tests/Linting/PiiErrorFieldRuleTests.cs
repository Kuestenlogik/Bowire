// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Linting;
using Kuestenlogik.Bowire.Linting.Rules;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Tests.Linting;

/// <summary>BWR-LINT-PII-IN-ERROR over hand-built methods (#583).</summary>
public sealed class PiiErrorFieldRuleTests
{
    [Fact]
    public void Pii_In_A_Nested_Error_Field_Is_Found()
    {
        var details = Msg("Details", Field("dateOfBirth"));
        var finding = Assert.Single(new PiiErrorFieldRule().Inspect(Svc(
            Method("register", errors: [Msg("RegisterError422", Field("detail"), FieldMsg("details", details))]))));

        Assert.Equal("dateOfBirth", finding.Field);
        Assert.Equal(BowireLintSeverity.Medium, finding.Severity);
    }

    [Fact]
    public void A_Method_Without_Declared_Errors_Says_Nothing()
    {
        // gRPC and GraphQL never declare error shapes; silence there is a gap in
        // what discovery knows, and the rule must not invent anything.
        Assert.Empty(new PiiErrorFieldRule().Inspect(Svc(Method("get", errors: null))));
    }

    [Fact]
    public void Pii_Only_In_The_Success_Response_Is_Not_This_Rules_Business()
    {
        Assert.Empty(new PiiErrorFieldRule().Inspect(Svc(
            Method("get", output: Msg("User", Field("email")), errors: [Msg("E404", Field("title"))]))));
    }

    [Fact]
    public void Ships_Built_In()
        => Assert.Contains("BWR-LINT-PII-IN-ERROR", BowireSchemaLinter.CreateDefault().RuleIds);

    private static BowireServiceInfo Svc(params BowireMethodInfo[] methods) => new("svc", "pkg", [.. methods]);

    private static BowireMethodInfo Method(string name, BowireMessageInfo? output = null, List<BowireMessageInfo>? errors = null)
        => new(name, name, false, false, Msg("In"), output ?? Msg("Out"), "unary") { ErrorTypes = errors };

    private static BowireMessageInfo Msg(string name, params BowireFieldInfo[] fields) => new(name, name, [.. fields]);

    private static BowireFieldInfo Field(string name) => new(name, 0, "string", "", false, false, null, null);

    private static BowireFieldInfo FieldMsg(string name, BowireMessageInfo type) => new(name, 0, "message", "", false, false, type, null);
}

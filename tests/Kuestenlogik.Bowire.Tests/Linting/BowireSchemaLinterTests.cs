// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Linting;
using Kuestenlogik.Bowire.Linting.Rules;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Tests.Linting;

/// <summary>
/// Coverage for the design-time schema linter (#189): each built-in rule fires
/// on the shape it targets and stays silent on a clean surface, and the runner
/// collects findings in a stable order.
/// </summary>
public sealed class BowireSchemaLinterTests
{
    // ---- SensitiveResponseFieldRule -------------------------------------

    [Fact]
    public void Sensitive_response_field_is_flagged_high()
    {
        var svc = Svc("Users", methods:
            Method("GetUser", output: Msg("User", Field("id"), Field("password"))));

        var finding = Assert.Single(new SensitiveResponseFieldRule().Inspect(svc));

        Assert.Equal("BWR-LINT-SENSITIVE-RESPONSE", finding.RuleId);
        Assert.Equal(BowireLintSeverity.High, finding.Severity);
        Assert.Equal("password", finding.Field);
        Assert.Equal("GetUser", finding.Method);
    }

    [Fact]
    public void Sensitive_field_is_caught_in_a_nested_message()
    {
        var credentials = Msg("Credentials", Field("api_key"));
        var svc = Svc("Auth", methods:
            Method("Login", output: Msg("Session", FieldMsg("creds", credentials))));

        var finding = Assert.Single(new SensitiveResponseFieldRule().Inspect(svc));
        Assert.Equal("api_key", finding.Field);
    }

    [Fact]
    public void Clean_response_produces_no_sensitive_finding()
    {
        var svc = Svc("Users", methods:
            Method("GetUser", output: Msg("User", Field("id"), Field("displayName"))));

        Assert.Empty(new SensitiveResponseFieldRule().Inspect(svc));
    }

    // ---- MissingPaginationRule ------------------------------------------

    [Fact]
    public void List_response_without_pagination_is_flagged_medium()
    {
        var svc = Svc("Users", methods:
            Method("ListUsers",
                input: Msg("Req"),
                output: Msg("Res", Repeated("users"))));

        var finding = Assert.Single(new MissingPaginationRule().Inspect(svc));
        Assert.Equal(BowireLintSeverity.Medium, finding.Severity);
        Assert.Equal("ListUsers", finding.Method);
    }

    [Fact]
    public void List_response_with_a_pagination_parameter_is_clean()
    {
        var svc = Svc("Users", methods:
            Method("ListUsers",
                input: Msg("Req", Field("pageSize"), Field("pageToken")),
                output: Msg("Res", Repeated("users"))));

        Assert.Empty(new MissingPaginationRule().Inspect(svc));
    }

    [Fact]
    public void Server_streaming_list_is_not_flagged_for_pagination()
    {
        var svc = Svc("Users", methods:
            Method("StreamUsers",
                input: Msg("Req"),
                output: Msg("Res", Repeated("users")),
                serverStreaming: true));

        Assert.Empty(new MissingPaginationRule().Inspect(svc));
    }

    [Fact]
    public void Non_list_response_is_not_flagged_for_pagination()
    {
        var svc = Svc("Users", methods:
            Method("GetUser", input: Msg("Req"), output: Msg("Res", Field("id"))));

        Assert.Empty(new MissingPaginationRule().Inspect(svc));
    }

    [Fact]
    public void List_response_with_an_output_cursor_is_clean()
    {
        var svc = Svc("Users", methods:
            Method("ListUsers",
                input: Msg("Req"),
                output: Msg("Res", Repeated("users"), Field("nextPageToken"))));

        Assert.Empty(new MissingPaginationRule().Inspect(svc));
    }

    // ---- PiiResponseFieldRule -------------------------------------------

    [Fact]
    public void Pii_response_field_is_flagged_medium()
    {
        var svc = Svc("Users", methods:
            Method("GetUser", output: Msg("User", Field("id"), Field("email"))));

        var finding = Assert.Single(new PiiResponseFieldRule().Inspect(svc));

        Assert.Equal("BWR-LINT-PII-RESPONSE", finding.RuleId);
        Assert.Equal(BowireLintSeverity.Medium, finding.Severity);
        Assert.Equal("email", finding.Field);
        Assert.Equal("GetUser", finding.Method);
    }

    [Fact]
    public void Clean_response_produces_no_pii_finding()
    {
        var svc = Svc("Users", methods:
            Method("GetUser", output: Msg("User", Field("id"), Field("displayName"))));

        Assert.Empty(new PiiResponseFieldRule().Inspect(svc));
    }

    // ---- StringTimestampFieldRule ---------------------------------------

    [Fact]
    public void String_typed_time_field_is_flagged_low()
    {
        var svc = Svc("Users", methods:
            Method("GetUser", output: Msg("User", Field("created_at", "string"))));

        var finding = Assert.Single(new StringTimestampFieldRule().Inspect(svc));

        Assert.Equal("BWR-LINT-STRING-TIMESTAMP", finding.RuleId);
        Assert.Equal(BowireLintSeverity.Low, finding.Severity);
        Assert.Equal("created_at", finding.Field);
    }

    [Fact]
    public void Typed_timestamp_field_is_not_flagged()
    {
        var svc = Svc("Users", methods:
            Method("GetUser", output: Msg("User", Field("created_at", "timestamp"))));

        Assert.Empty(new StringTimestampFieldRule().Inspect(svc));
    }

    [Fact]
    public void A_word_ending_in_at_is_not_a_false_positive()
    {
        var svc = Svc("Users", methods:
            Method("GetUser", output: Msg("User", Field("format", "string"))));

        Assert.Empty(new StringTimestampFieldRule().Inspect(svc));
    }

    [Theory]
    [InlineData("timezone")]
    [InlineData("timeout")]
    public void A_leading_time_word_is_not_a_false_positive(string fieldName)
    {
        var svc = Svc("Users", methods:
            Method("GetUser", output: Msg("User", Field(fieldName, "string"))));

        Assert.Empty(new StringTimestampFieldRule().Inspect(svc));
    }

    [Fact]
    public void A_suffix_time_string_field_is_flagged()
    {
        var svc = Svc("Bookings", methods:
            Method("GetBooking", output: Msg("Booking", Field("start_time", "string"))));

        var finding = Assert.Single(new StringTimestampFieldRule().Inspect(svc));
        Assert.Equal("start_time", finding.Field);
    }

    // ---- MissingVersioningRule ------------------------------------------

    [Fact]
    public void Service_without_version_or_versioned_route_is_flagged_low()
    {
        var svc = Svc("Users", methods:
            Method("GetUser", httpPath: "/users/{id}"));

        var finding = Assert.Single(new MissingVersioningRule().Inspect(svc));
        Assert.Equal(BowireLintSeverity.Low, finding.Severity);
        Assert.Null(finding.Method);
    }

    [Fact]
    public void Declared_version_clears_the_versioning_rule()
    {
        var svc = Svc("Users", version: "1.2.0", methods:
            Method("GetUser", httpPath: "/users/{id}"));

        Assert.Empty(new MissingVersioningRule().Inspect(svc));
    }

    [Fact]
    public void A_versioned_route_clears_the_versioning_rule()
    {
        var svc = Svc("Users", methods:
            Method("GetUser", httpPath: "/v1/users/{id}"));

        Assert.Empty(new MissingVersioningRule().Inspect(svc));
    }

    // ---- runner ---------------------------------------------------------

    [Fact]
    public void Default_linter_runs_every_built_in_rule()
    {
        var linter = BowireSchemaLinter.CreateDefault();

        Assert.Contains("BWR-LINT-SENSITIVE-RESPONSE", linter.RuleIds);
        Assert.Contains("BWR-LINT-MISSING-PAGINATION", linter.RuleIds);
        Assert.Contains("BWR-LINT-MISSING-VERSIONING", linter.RuleIds);
        Assert.Contains("BWR-LINT-PII-RESPONSE", linter.RuleIds);
        Assert.Contains("BWR-LINT-STRING-TIMESTAMP", linter.RuleIds);
    }

    [Fact]
    public void Linter_collects_findings_across_rules_and_services()
    {
        var services = new[]
        {
            Svc("Users", methods: Method("ListUsers",
                input: Msg("Req"),
                output: Msg("Res", Repeated("users"), Field("secret")))),
        };

        var findings = BowireSchemaLinter.CreateDefault().Lint(services);

        // sensitive (secret) + missing-pagination + missing-versioning.
        Assert.Contains(findings, f => f.RuleId == "BWR-LINT-SENSITIVE-RESPONSE");
        Assert.Contains(findings, f => f.RuleId == "BWR-LINT-MISSING-PAGINATION");
        Assert.Contains(findings, f => f.RuleId == "BWR-LINT-MISSING-VERSIONING");
    }

    // ---- config (.bowire/rules.json) ------------------------------------

    [Fact]
    public void Config_disables_a_rule()
    {
        var config = BowireLintConfig.Parse(
            """{ "rules": { "BWR-LINT-MISSING-VERSIONING": { "enabled": false } } }""");
        var services = new[] { Svc("Users", methods: Method("GetUser", httpPath: "/users")) };

        var findings = BowireSchemaLinter.CreateDefault().Lint(services, config);

        Assert.DoesNotContain(findings, f => f.RuleId == "BWR-LINT-MISSING-VERSIONING");
    }

    [Fact]
    public void Config_overrides_a_rule_severity()
    {
        var config = BowireLintConfig.Parse(
            """{ "rules": { "BWR-LINT-MISSING-VERSIONING": { "severity": "high" } } }""");
        var services = new[] { Svc("Users", methods: Method("GetUser", httpPath: "/users")) };

        var finding = Assert.Single(
            BowireSchemaLinter.CreateDefault().Lint(services, config),
            f => f.RuleId == "BWR-LINT-MISSING-VERSIONING");
        Assert.Equal(BowireLintSeverity.High, finding.Severity);
    }

    [Fact]
    public void Unlisted_rule_keeps_default_severity_and_stays_on()
    {
        var config = BowireLintConfig.Parse("""{ "rules": {} }""");

        Assert.True(config.IsEnabled("BWR-LINT-PII-RESPONSE"));
        Assert.Null(config.SeverityOverride("BWR-LINT-PII-RESPONSE"));
    }

    [Fact]
    public void Config_matches_rule_ids_case_insensitively()
    {
        var config = BowireLintConfig.Parse(
            """{ "rules": { "bwr-lint-pii-response": { "enabled": false } } }""");

        Assert.False(config.IsEnabled("BWR-LINT-PII-RESPONSE"));
    }

    [Fact]
    public void ConfigLoader_discovers_rules_json_by_walking_up()
    {
        var root = Directory.CreateTempSubdirectory("bowire-lint-test");
        try
        {
            var bowireDir = Directory.CreateDirectory(Path.Combine(root.FullName, ".bowire"));
            var rulesPath = Path.Combine(bowireDir.FullName, "rules.json");
            File.WriteAllText(rulesPath, """{ "rules": {} }""");
            var nested = Directory.CreateDirectory(Path.Combine(root.FullName, "a", "b"));

            var found = BowireLintConfigLoader.DiscoverPath(nested.FullName);

            Assert.NotNull(found);
            Assert.Equal(Path.GetFullPath(rulesPath), Path.GetFullPath(found));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // ---- builders -------------------------------------------------------

    private static BowireServiceInfo Svc(string name, string? version = null, params BowireMethodInfo[] methods)
        => new(name, "pkg", [.. methods]) { Version = version };

    private static BowireMethodInfo Method(
        string name,
        string? httpPath = null,
        BowireMessageInfo? input = null,
        BowireMessageInfo? output = null,
        bool serverStreaming = false)
        => new(name, name, ClientStreaming: false, ServerStreaming: serverStreaming,
               input ?? Msg("In"), output ?? Msg("Out"), "unary")
        {
            HttpPath = httpPath,
        };

    private static BowireMessageInfo Msg(string name, params BowireFieldInfo[] fields)
        => new(name, name, [.. fields]);

    private static BowireFieldInfo Field(string name, string type = "string")
        => new(name, 0, type, "", IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null);

    private static BowireFieldInfo Repeated(string name, string type = "string")
        => new(name, 0, type, "", IsMap: false, IsRepeated: true, MessageType: null, EnumValues: null);

    private static BowireFieldInfo FieldMsg(string name, BowireMessageInfo messageType)
        => new(name, 0, "message", "", IsMap: false, IsRepeated: false, MessageType: messageType, EnumValues: null);
}

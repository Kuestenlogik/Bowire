// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0


namespace Kuestenlogik.Bowire.Scim.Tests;

/// <summary>
/// The filter subset (#96). What matters is not breadth — it is that an
/// expression outside the subset is refused rather than half-evaluated.
/// </summary>
public sealed class ScimFilterTests
{
    private static Func<string, string?> User(
        string userName, string? externalId = null, bool active = true)
        => name => name.ToUpperInvariant() switch
        {
            "USERNAME" => userName,
            "EXTERNALID" => externalId,
            "ACTIVE" => active ? "true" : "false",
            _ => null,
        };

    [Fact]
    public void The_One_Filter_Every_Connector_Sends_Works()
    {
        var filter = ScimFilter.Parse("userName eq \"ada@example.com\"");

        Assert.True(filter.Matches(User("ada@example.com")));
        Assert.False(filter.Matches(User("grace@example.com")));
    }

    [Fact]
    public void A_Login_Name_Matches_Regardless_Of_Case()
    {
        // RFC 7643 §2.1: string attributes are caseExact=false unless the
        // schema says otherwise, and userName is one of them. A connector that
        // stores "Ada@Example.com" and filters for the lower-case form must
        // find its own user, or it creates a duplicate.
        var filter = ScimFilter.Parse("userName eq \"ADA@EXAMPLE.COM\"");

        Assert.True(filter.Matches(User("ada@example.com")));
    }

    [Fact]
    public void Entra_Filters_On_The_Immutable_Id_Too()
    {
        var filter = ScimFilter.Parse("externalId eq \"8f14e45f\"");

        Assert.True(filter.Matches(User("ada@example.com", externalId: "8f14e45f")));
        Assert.False(filter.Matches(User("ada@example.com")));
    }

    [Fact]
    public void And_Binds_Tighter_Than_Or()
    {
        // a or (b and c) — not (a or b) and c. Left-to-right evaluation would
        // answer a different question and look like it worked.
        var filter = ScimFilter.Parse(
            "userName eq \"grace@example.com\" or userName eq \"ada@example.com\" and active eq \"true\"");

        Assert.True(filter.Matches(User("grace@example.com", active: false)));
        Assert.True(filter.Matches(User("ada@example.com", active: true)));
        Assert.False(filter.Matches(User("ada@example.com", active: false)));
    }

    [Fact]
    public void Both_Sides_Of_An_And_Have_To_Hold()
    {
        var filter = ScimFilter.Parse("userName eq \"ada@example.com\" and active eq \"false\"");

        Assert.False(filter.Matches(User("ada@example.com", active: true)));
        Assert.True(filter.Matches(User("ada@example.com", active: false)));
    }

    [Fact]
    public void Presence_Asks_Whether_There_Is_A_Value_At_All()
    {
        var filter = ScimFilter.Parse("externalId pr");

        Assert.True(filter.Matches(User("ada@example.com", externalId: "8f14e45f")));
        Assert.False(filter.Matches(User("ada@example.com")));
    }

    [Fact]
    public void A_Quoted_Value_Can_Contain_Spaces_And_Quotes()
    {
        var filter = ScimFilter.Parse("displayName eq \"Ada \\\"the count\\\" Lovelace\"");

        Assert.True(filter.Matches(name =>
            name == "displayName" ? "Ada \"the count\" Lovelace" : null));
    }

    [Theory]
    [InlineData("userName co \"example\"")]
    [InlineData("userName sw \"ada\"")]
    [InlineData("meta.lastModified gt \"2026-01-01T00:00:00Z\"")]
    public void An_Operator_Outside_The_Subset_Is_Refused_Not_Approximated(string expression)
    {
        // Answering a contains-query with an equality result is worse than
        // saying no: the caller has no way to tell it got a different question
        // answered.
        var ex = Assert.Throws<ScimFilterException>(() => ScimFilter.Parse(expression));

        Assert.Contains("not supported", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("(userName eq \"a\") or (userName eq \"b\")")]
    [InlineData("emails[type eq \"work\"].value eq \"a@b.c\"")]
    public void Grouping_Is_Refused_Rather_Than_Dropped(string expression)
    {
        // Dropping the parentheses changes which resources match, silently.
        Assert.Throws<ScimFilterException>(() => ScimFilter.Parse(expression));
    }

    [Theory]
    [InlineData("userName eq")]
    [InlineData("userName")]
    [InlineData("eq \"ada\"")]
    [InlineData("userName eq \"unterminated")]
    public void A_Malformed_Filter_Is_An_Error_Not_A_Match_Everything(string expression)
    {
        // The dangerous failure would be returning every user for a filter the
        // parser could not read — a connector reading that as "these all
        // match" then reconciles the whole directory against one record.
        Assert.Throws<ScimFilterException>(() => ScimFilter.Parse(expression));
    }

    [Fact]
    public void An_Empty_Filter_Is_Refused()
    {
        Assert.Throws<ArgumentException>(() => ScimFilter.Parse("   "));
        Assert.Throws<ArgumentNullException>(() => ScimFilter.Parse(null!));
    }

    [Fact]
    public void An_Attribute_This_Provider_Does_Not_Store_Simply_Matches_Nothing()
    {
        // Not an error: the attribute is legal SCIM, we just have no value for
        // it. Refusing would break a connector that filters on something
        // harmless.
        var filter = ScimFilter.Parse("nickName eq \"ada\"");

        Assert.False(filter.Matches(User("ada@example.com")));
    }
}

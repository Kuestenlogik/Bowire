// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Scim.Tests;

/// <summary>
/// PATCH (#96) — where SCIM implementations break, because the two connectors
/// that matter send different shapes for the same intent.
/// </summary>
public sealed class ScimPatchTests
{
    private static ScimPatchRequest Patch(string json)
        => JsonSerializer.Deserialize<ScimPatchRequest>(json)!;

    private static ScimUser Ada() => new()
    {
        Id = "1",
        UserName = "ada@example.com",
        DisplayName = "Ada Lovelace",
        Active = true,
    };

    // ---- deactivation, both dialects ----

    [Fact]
    public void Okta_Deactivates_With_A_Lower_Case_Op_And_A_Path()
    {
        var user = Ada();

        ScimPatch.Apply(user, Patch("""
            { "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
              "Operations": [ { "op": "replace", "path": "active", "value": false } ] }
            """));

        Assert.False(user.Active);
    }

    [Fact]
    public void Entra_Deactivates_With_A_Capitalised_Op_And_No_Path()
    {
        // The value is the change. An implementation that only understands the
        // path form deactivates nobody provisioned from Entra ID, and reports
        // success while doing it.
        var user = Ada();

        ScimPatch.Apply(user, Patch("""
            { "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
              "Operations": [ { "op": "Replace", "value": { "active": false } } ] }
            """));

        Assert.False(user.Active);
    }

    [Fact]
    public void A_Boolean_Sent_As_A_String_Still_Deactivates()
    {
        // Some connectors do this. Refusing would leave the person active
        // after a deactivation the directory believes succeeded — the failure
        // mode this whole feature exists to prevent.
        var user = Ada();

        ScimPatch.Apply(user, Patch("""
            { "Operations": [ { "op": "replace", "path": "active", "value": "False" } ] }
            """));

        Assert.False(user.Active);
    }

    [Fact]
    public void Reactivation_Works_The_Same_Way()
    {
        var user = Ada();
        user.Active = false;

        ScimPatch.Apply(user, Patch("""
            { "Operations": [ { "op": "replace", "path": "active", "value": true } ] }
            """));

        Assert.True(user.Active);
    }

    // ---- ordinary attributes ----

    [Fact]
    public void Attributes_Are_Replaced_In_The_Order_They_Arrive()
    {
        var user = Ada();

        ScimPatch.Apply(user, Patch("""
            { "Operations": [
                { "op": "replace", "path": "displayName", "value": "A. Lovelace" },
                { "op": "replace", "path": "displayName", "value": "Ada L." } ] }
            """));

        Assert.Equal("Ada L.", user.DisplayName);
    }

    [Fact]
    public void A_Nested_Name_Path_Reaches_Into_The_Complex_Attribute()
    {
        var user = Ada();

        ScimPatch.Apply(user, Patch("""
            { "Operations": [
                { "op": "replace", "path": "name.givenName", "value": "Ada" },
                { "op": "replace", "path": "name.familyName", "value": "Lovelace" } ] }
            """));

        Assert.Equal("Ada", user.Name?.GivenName);
        Assert.Equal("Lovelace", user.Name?.FamilyName);
    }

    [Fact]
    public void Removing_An_Attribute_Clears_It()
    {
        var user = Ada();
        user.ExternalId = "8f14e45f";

        ScimPatch.Apply(user, Patch("""
            { "Operations": [ { "op": "remove", "path": "externalId" } ] }
            """));

        Assert.Null(user.ExternalId);
    }

    [Fact]
    public void An_Attribute_This_Provider_Does_Not_Model_Survives_The_Round_Trip()
    {
        // A connector that reads back a resource missing what it just wrote
        // concludes the write failed, and retries. Forever.
        var user = Ada();

        ScimPatch.Apply(user, Patch("""
            { "Operations": [
                { "op": "replace", "path": "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:department",
                  "value": "Analytical Engines" } ] }
            """));

        const string key = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User:department";
        Assert.True(user.Extensions.ContainsKey(key), "the attribute the connector sent is gone");
        Assert.Equal("Analytical Engines", user.Extensions[key].GetString());
    }

    [Fact]
    public void Removing_An_Unmodelled_Attribute_Takes_It_Back_Out()
    {
        var user = Ada();
        ScimPatch.Apply(user, Patch("""
            { "Operations": [ { "op": "add", "path": "nickName", "value": "Countess" } ] }
            """));

        ScimPatch.Apply(user, Patch("""
            { "Operations": [ { "op": "remove", "path": "nickName" } ] }
            """));

        Assert.False(user.Extensions.ContainsKey("nickName"));
    }

    // ---- groups ----

    [Fact]
    public void Adding_A_Member_Is_The_Whole_Of_Group_Sync()
    {
        var group = new ScimGroup { Id = "g1", DisplayName = "bowire-admins" };

        ScimPatch.Apply(group, Patch("""
            { "Operations": [
                { "op": "add", "path": "members",
                  "value": [ { "value": "1", "display": "ada@example.com" } ] } ] }
            """));

        Assert.Equal("1", Assert.Single(group.Members).Value);
    }

    [Fact]
    public void Adding_The_Same_Member_Twice_Does_Not_Duplicate_It()
    {
        // Connectors re-send the full membership on every sync.
        var group = new ScimGroup { Id = "g1", DisplayName = "bowire-admins" };
        var add = """
            { "Operations": [ { "op": "add", "path": "members", "value": [ { "value": "1" } ] } ] }
            """;

        ScimPatch.Apply(group, Patch(add));
        ScimPatch.Apply(group, Patch(add));

        Assert.Single(group.Members);
    }

    [Fact]
    public void Removing_A_Named_Member_Leaves_The_Others()
    {
        var group = new ScimGroup
        {
            Id = "g1",
            DisplayName = "bowire-admins",
            Members = [new ScimValue { Value = "1" }, new ScimValue { Value = "2" }],
        };

        ScimPatch.Apply(group, Patch("""
            { "Operations": [ { "op": "remove", "path": "members", "value": [ { "value": "1" } ] } ] }
            """));

        Assert.Equal("2", Assert.Single(group.Members).Value);
    }

    [Fact]
    public void Removing_Members_With_No_Value_Empties_The_Group()
    {
        // RFC 7644 §3.5.2.2 — a remove without a value clears the attribute.
        var group = new ScimGroup
        {
            Id = "g1",
            DisplayName = "bowire-admins",
            Members = [new ScimValue { Value = "1" }, new ScimValue { Value = "2" }],
        };

        ScimPatch.Apply(group, Patch("""
            { "Operations": [ { "op": "remove", "path": "members" } ] }
            """));

        Assert.Empty(group.Members);
    }

    [Fact]
    public void An_Attribute_A_Group_Does_Not_Store_Is_Refused()
    {
        // Unlike a user, a group has nowhere to keep it — and reporting
        // success for a change that will never be stored is the worse answer.
        var group = new ScimGroup { Id = "g1", DisplayName = "bowire-admins" };

        Assert.Throws<ScimPatchException>(() => ScimPatch.Apply(group, Patch("""
            { "Operations": [ { "op": "replace", "path": "description", "value": "x" } ] }
            """)));
    }

    // ---- refusals ----

    [Fact]
    public void An_Unknown_Operation_Is_Refused()
    {
        var user = Ada();

        var ex = Assert.Throws<ScimPatchException>(() => ScimPatch.Apply(user, Patch("""
            { "Operations": [ { "op": "increment", "path": "active", "value": true } ] }
            """)));

        Assert.Contains("add, replace or remove", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Pathless_Operation_Without_An_Object_Value_Is_Refused()
    {
        // There is nothing to apply and no attribute named. Guessing would be
        // the only alternative.
        var user = Ada();

        Assert.Throws<ScimPatchException>(() => ScimPatch.Apply(user, Patch("""
            { "Operations": [ { "op": "replace", "value": "active" } ] }
            """)));
    }

    [Fact]
    public void A_Non_Boolean_Active_Is_Refused_Rather_Than_Coerced()
    {
        var user = Ada();

        Assert.Throws<ScimPatchException>(() => ScimPatch.Apply(user, Patch("""
            { "Operations": [ { "op": "replace", "path": "active", "value": 7 } ] }
            """)));
    }
}

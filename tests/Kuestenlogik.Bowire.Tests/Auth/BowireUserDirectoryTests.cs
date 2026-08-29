// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Claims;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Endpoints;

namespace Kuestenlogik.Bowire.Tests.Auth;

/// <summary>
/// What the workbench can say about the person it is serving, from the token
/// alone (#98).
/// </summary>
public sealed class BowireUserDirectoryTests
{
    private static ClaimsPrincipal Token(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    private static readonly ClaimsUserDirectory FromToken = new();

    // ---- reading the token ----

    [Fact]
    public void The_Name_Claim_Is_What_Gets_Shown()
    {
        var profile = FromToken.Describe(Token(("name", "Ada Lovelace")), "8f14e45f");

        Assert.Equal("Ada Lovelace", profile.DisplayName);
        Assert.Equal("8f14e45f", profile.Subject);
    }

    [Theory]
    [InlineData("name")]
    [InlineData(ClaimTypes.Name)]
    [InlineData("preferred_username")]
    [InlineData("given_name")]
    public void Whichever_Claim_The_Provider_Chose_To_Put_It_In(string claim)
    {
        // Providers disagree about this, and a chip showing a raw subject
        // where a name was expected reads as a bug rather than as a missing
        // claim.
        var profile = FromToken.Describe(Token((claim, "Ada Lovelace")), "8f14e45f");

        Assert.Equal("Ada Lovelace", profile.DisplayName);
    }

    [Fact]
    public void The_Earlier_Claim_Wins_When_Several_Are_Present()
    {
        var profile = FromToken.Describe(
            Token(("preferred_username", "ada@corp"), ("name", "Ada Lovelace")), "8f14e45f");

        Assert.Equal("Ada Lovelace", profile.DisplayName);
    }

    [Theory]
    [InlineData("email")]
    [InlineData(ClaimTypes.Email)]
    [InlineData("upn")]
    public void An_Address_Is_Read_From_Any_Of_The_Usual_Claims(string claim)
        => Assert.Equal("ada@example.com",
            FromToken.Describe(Token((claim, "ada@example.com")), "8f14e45f").Email);

    [Fact]
    public void A_Token_With_Nothing_In_It_Yields_A_Profile_Anyway()
    {
        // The subject is always known — it is what the storage slot is keyed
        // on — so there is always something to return.
        var profile = FromToken.Describe(Token(), "8f14e45f");

        Assert.Equal("8f14e45f", profile.Subject);
        Assert.Null(profile.DisplayName);
        Assert.Null(profile.Email);
    }

    [Fact]
    public void Nobody_Is_An_Administrator_Without_A_Directory_That_Says_So()
    {
        // The safe direction: an install with no source of truth for who
        // administers it has no administrators, rather than everybody.
        Assert.False(FromToken.Describe(
            Token(("role", "admin"), ("groups", "bowire-admins")), "8f14e45f").IsAdmin);
    }

    [Fact]
    public void There_Is_Nobody_To_List_Either()
        => Assert.Empty(FromToken.Search("ada", 10));

    [Fact]
    public void A_Profile_Without_A_Subject_Is_Refused()
        => Assert.Throws<ArgumentException>(() => FromToken.Describe(Token(), "  "));

    // ---- the avatar fallback ----

    [Fact]
    public void Initials_Come_From_The_Name_When_There_Is_One()
        => Assert.Equal("AL", Initials(name: "Ada Lovelace", email: "grace@example.com"));

    [Fact]
    public void A_Single_Word_Name_Gives_Two_Letters_Of_It()
        => Assert.Equal("AD", Initials(name: "Ada"));

    [Fact]
    public void A_Middle_Name_Does_Not_Push_Out_The_Surname()
        => Assert.Equal("AK", Initials(name: "Ada Byron King"));

    [Fact]
    public void Without_A_Name_The_Local_Part_Of_The_Address_Is_Used()
        => Assert.Equal("AL", Initials(email: "ada.lovelace@example.com"));

    [Theory]
    [InlineData("ada_lovelace@example.com", "AL")]
    [InlineData("ada-lovelace@example.com", "AL")]
    [InlineData("ada+work@example.com", "AW")]
    public void Address_Separators_Are_Read_As_Word_Breaks(string email, string expected)
        => Assert.Equal(expected, Initials(email: email));

    [Fact]
    public void With_Neither_Two_Characters_Of_The_Subject_Are_Better_Than_An_Empty_Circle()
    {
        // They identify nobody — most subjects are GUIDs — but a blank avatar
        // reads as a rendering failure.
        Assert.Equal("8F", Initials(subject: "8f14e45f"));
    }

    [Fact]
    public void A_Subject_With_No_Letters_Or_Digits_Still_Renders_Something()
        => Assert.Equal("?", Initials(subject: "|||"));

    private static string Initials(string? name = null, string? email = null, string subject = "sub")
        => BowireIdentityEndpoints.Initials(new BowireUserProfile
        {
            Subject = subject,
            DisplayName = name,
            Email = email,
        });
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Claims;

namespace Kuestenlogik.Bowire.Scim.Tests;

/// <summary>
/// Who somebody is according to the provisioned directory, and whether they
/// administer the install (#98, on top of #96).
/// </summary>
public sealed class ScimUserDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-scim-dir-" + Guid.NewGuid().ToString("N"));
    private readonly BowireScimOptions _options = new() { AdminGroup = "bowire-admins" };
    private readonly BowireScimStore _store;
    private readonly ScimUserDirectory _directory;

    public ScimUserDirectoryTests()
    {
        Directory.CreateDirectory(_root);
        _store = new BowireScimStore(_root);
        _directory = new ScimUserDirectory(_store, _options);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static ClaimsPrincipal Token(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    private ScimUserRecord Provision(
        string userName, string? externalId = null, string? displayName = null, bool active = true)
        => _store.CreateUser(new ScimUser
        {
            UserName = userName,
            ExternalId = externalId,
            DisplayName = displayName,
            Active = active,
            Emails = [new ScimValue { Value = userName, Primary = true }],
        });

    // ---- the role ----

    [Fact]
    public void Membership_In_The_Configured_Group_Is_What_Makes_An_Administrator()
    {
        var ada = Provision("ada@example.com", externalId: "8f14e45f");
        _store.CreateGroup(new ScimGroup
        {
            DisplayName = "bowire-admins",
            Members = [new ScimValue { Value = ada.Resource.Id }],
        });

        Assert.True(_directory.Describe(Token(), "8f14e45f").IsAdmin);
    }

    [Fact]
    public void A_Role_Claim_In_The_Token_Confers_Nothing()
    {
        // The token's claim is only as good as the mapping that produced it,
        // and Bowire cannot check that mapping. Group membership in the
        // provisioned directory is something the operator configured
        // deliberately and can audit.
        Provision("ada@example.com", externalId: "8f14e45f");

        var profile = _directory.Describe(
            Token(("role", "admin"), ("groups", "bowire-admins")), "8f14e45f");

        Assert.False(profile.IsAdmin);
    }

    [Fact]
    public void The_Group_Name_Is_The_Configured_One_Not_A_Constant()
    {
        // An IdP's group for this is called whatever the operator's directory
        // calls it.
        var ada = Provision("ada@example.com", externalId: "8f14e45f");
        _store.CreateGroup(new ScimGroup
        {
            DisplayName = "Platform Owners",
            Members = [new ScimValue { Value = ada.Resource.Id }],
        });

        Assert.False(_directory.Describe(Token(), "8f14e45f").IsAdmin);

        var renamed = new ScimUserDirectory(
            _store, new BowireScimOptions { AdminGroup = "Platform Owners" });

        Assert.True(renamed.Describe(Token(), "8f14e45f").IsAdmin);
    }

    // ---- the name ----

    [Fact]
    public void The_Token_Wins_For_A_Name_Because_It_Is_Fresher()
    {
        // A record synced overnight can be behind what the person just
        // authenticated with.
        Provision("ada@example.com", externalId: "8f14e45f", displayName: "A. Lovelace");

        var profile = _directory.Describe(Token(("name", "Ada Lovelace")), "8f14e45f");

        Assert.Equal("Ada Lovelace", profile.DisplayName);
    }

    [Fact]
    public void The_Record_Fills_In_What_The_Token_Left_Out()
    {
        Provision("ada@example.com", externalId: "8f14e45f", displayName: "Ada Lovelace");

        var profile = _directory.Describe(Token(), "8f14e45f");

        Assert.Equal("Ada Lovelace", profile.DisplayName);
        Assert.Equal("ada@example.com", profile.Email);
    }

    [Fact]
    public void A_Structured_Name_Is_Assembled_When_There_Is_No_Display_Name()
    {
        _store.CreateUser(new ScimUser
        {
            UserName = "ada@example.com",
            ExternalId = "8f14e45f",
            Name = new ScimName { GivenName = "Ada", FamilyName = "Lovelace" },
        });

        Assert.Equal("Ada Lovelace", _directory.Describe(Token(), "8f14e45f").DisplayName);
    }

    [Fact]
    public void Somebody_Signed_In_But_Not_Yet_Provisioned_Gets_The_Tokens_Answer()
    {
        // Legitimate while a first sync is running, and locking them out or
        // inventing a record would both be worse than saying what the token
        // says.
        var profile = _directory.Describe(Token(("name", "Grace Hopper")), "unknown-subject");

        Assert.Equal("Grace Hopper", profile.DisplayName);
        Assert.False(profile.IsAdmin);
    }

    // ---- the picker ----

    [Fact]
    public void Searching_Matches_The_Login_Name_And_The_Display_Name()
    {
        Provision("ada@example.com", displayName: "Ada Lovelace");
        Provision("grace@example.com", displayName: "Grace Hopper");

        Assert.Equal("ada@example.com", Assert.Single(_directory.Search("lovelace", 10)).Email);
        Assert.Equal("grace@example.com", Assert.Single(_directory.Search("grace@", 10)).Email);
    }

    [Fact]
    public void Searching_Ignores_Case()
        => Assert.Single(WithAda().Search("ADA", 10));

    [Fact]
    public void An_Empty_Term_Lists_Everyone()
    {
        Provision("ada@example.com");
        Provision("grace@example.com");

        Assert.Equal(2, _directory.Search(null, 10).Count);
    }

    [Fact]
    public void A_Deprovisioned_Identity_Is_Not_Somebody_To_Pick()
    {
        // Their slot is archived, so acting as them would show an empty
        // workbench and look like data loss.
        var ada = Provision("ada@example.com");
        _store.DeleteUser(ada.Resource.Id);

        Assert.Empty(_directory.Search("ada", 10));
    }

    [Fact]
    public void The_Limit_Is_Honoured()
    {
        Provision("ada@example.com");
        Provision("grace@example.com");
        Provision("margaret@example.com");

        Assert.Single(_directory.Search(null, 1));
        Assert.Empty(_directory.Search(null, 0));
    }

    [Fact]
    public void Results_Carry_The_Subject_A_Sign_In_Would_Match()
    {
        var ada = Provision("ada@example.com", externalId: "8f14e45f");
        _store.BindSubject(ada.Resource.Id, "bound-subject");
        Provision("grace@example.com", externalId: "abc123");

        var found = _directory.Search(null, 10);

        Assert.Contains(found, p => p.Subject == "bound-subject");
        Assert.Contains(found, p => p.Subject == "abc123");
    }

    private ScimUserDirectory WithAda()
    {
        Provision("ada@example.com", displayName: "Ada Lovelace");
        return _directory;
    }
}

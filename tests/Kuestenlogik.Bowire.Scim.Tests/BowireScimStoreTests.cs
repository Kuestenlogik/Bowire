// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Scim.Tests;

/// <summary>
/// The provisioned user list on disk (#96) — and what deprovisioning does to
/// the state the person left behind.
/// </summary>
public sealed class BowireScimStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-scim-" + Guid.NewGuid().ToString("N"));
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero));

    public BowireScimStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private BowireScimStore Store() => new(_root, _clock);

    private static ScimUser Ada() => new() { UserName = "ada@example.com", ExternalId = "8f14e45f" };

    // ---- provisioning ----

    [Fact]
    public void A_Provisioned_User_Gets_A_Server_Assigned_Id()
    {
        // The IdP does not choose it: RFC 7643 §3.1 makes id immutable and the
        // service provider's to assign, and a connector that could set it
        // could collide with one that already exists.
        var record = Store().CreateUser(Ada());

        Assert.NotEmpty(record.Resource.Id);
        Assert.Equal("User", record.Resource.Meta.ResourceType);
        Assert.Equal(_clock.Now, record.Resource.Meta.Created);
    }

    [Fact]
    public void A_Login_Name_Can_Only_Belong_To_One_Person()
    {
        var store = Store();
        store.CreateUser(Ada());

        var ex = Assert.Throws<ScimConflictException>(() => store.CreateUser(Ada()));

        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Case_Alone_Does_Not_Make_A_Second_Person()
    {
        // userName is caseExact=false. Two records differing only in case
        // would each be found by half the connector's lookups.
        var store = Store();
        store.CreateUser(Ada());

        Assert.Throws<ScimConflictException>(
            () => store.CreateUser(new ScimUser { UserName = "ADA@EXAMPLE.COM" }));
    }

    [Fact]
    public void A_User_Without_A_Login_Name_Is_Refused()
        => Assert.Throws<ScimConflictException>(() => Store().CreateUser(new ScimUser()));

    [Fact]
    public void Replacing_Keeps_The_Id_And_The_Creation_Time()
    {
        var store = Store();
        var created = store.CreateUser(Ada());
        _clock.Now = _clock.Now.AddHours(1);

        var replaced = store.ReplaceUser(created.Resource.Id,
            new ScimUser { UserName = "ada@example.com", DisplayName = "Ada L." });

        Assert.Equal(created.Resource.Id, replaced!.Resource.Id);
        Assert.Equal(created.Resource.Meta.Created, replaced.Resource.Meta.Created);
        Assert.True(replaced.Resource.Meta.LastModified > replaced.Resource.Meta.Created);
    }

    [Fact]
    public void Taking_Somebody_Elses_Login_Name_Is_Refused()
    {
        var store = Store();
        var ada = store.CreateUser(Ada());
        store.CreateUser(new ScimUser { UserName = "grace@example.com" });

        Assert.Throws<ScimConflictException>(() => store.ReplaceUser(ada.Resource.Id,
            new ScimUser { UserName = "grace@example.com" }));
    }

    [Fact]
    public void Replacing_Something_That_Is_Not_There_Says_So()
        => Assert.Null(Store().ReplaceUser("nope", Ada()));

    // ---- finding ----

    [Fact]
    public void A_Token_Subject_Finds_Its_Record_By_The_Immutable_Id_First()
    {
        // externalId is what survives someone changing their e-mail address;
        // userName is not. Matching on it first is what keeps a rename from
        // orphaning the record.
        var store = Store();
        store.CreateUser(Ada());

        Assert.Equal("ada@example.com", store.FindBySubject("8f14e45f")?.Resource.UserName);
    }

    [Fact]
    public void A_Token_Subject_Falls_Back_To_The_Login_Name()
    {
        var store = Store();
        store.CreateUser(new ScimUser { UserName = "ada@example.com" });

        Assert.NotNull(store.FindBySubject("ada@example.com"));
    }

    [Fact]
    public void Once_Bound_The_Subject_Wins_Over_Everything_Else()
    {
        var store = Store();
        var record = store.CreateUser(Ada());
        store.BindSubject(record.Resource.Id, "sub-from-the-token");

        Assert.Equal(record.Resource.Id, store.FindBySubject("sub-from-the-token")?.Resource.Id);
    }

    [Fact]
    public void An_Identity_Nobody_Provisioned_Is_Simply_Not_Found()
        => Assert.Null(Store().FindBySubject("stranger@example.com"));

    // ---- deprovisioning ----

    [Fact]
    public void Deleting_Deactivates_Rather_Than_Destroys()
    {
        // Deprovisioning is routinely undone — a team change, a misfiring
        // sync. A hard delete makes those recoverable only from a backup.
        var store = Store();
        var record = store.CreateUser(Ada());

        Assert.True(store.DeleteUser(record.Resource.Id));

        var after = store.GetUser(record.Resource.Id);
        Assert.NotNull(after);
        Assert.False(after.Resource.Active);
        Assert.Equal(_clock.Now, after.DeactivatedUtc);
    }

    [Fact]
    public void Provisioning_Does_Not_Create_An_Empty_Slot()
    {
        // #96 — the slot appears when there is something to put in it, not
        // when the person is provisioned. An organisation that syncs 10 000
        // people would otherwise get 10 000 empty directories, most of them
        // for accounts that never sign in.
        //
        // Nothing security-relevant rests on this either way: the
        // deactivation gate reads the record, not the directory, so an
        // identity with no slot yet is refused exactly like one with a slot
        // that was archived. The neighbouring tests are where that is pinned.
        var store = Store();
        var record = store.CreateUser(Ada());
        store.BindSubject(record.Resource.Id, "8f14e45f");

        var slot = new ScopedBowireUserStore(_root, "8f14e45f").Slot;
        Assert.False(Directory.Exists(slot));

        // And it appears the moment the person's state does.
        Directory.CreateDirectory(slot);
        File.WriteAllText(Path.Combine(slot, "environments.json"), "{}");
        Assert.True(Directory.Exists(slot));

        // Deactivating an identity whose slot was never written is not an
        // error — there is simply nothing to archive.
        var untouched = store.CreateUser(new ScimUser { UserName = "grace@example.com" });
        Assert.True(store.DeleteUser(untouched.Resource.Id));
        Assert.Null(store.GetUser(untouched.Resource.Id)!.ArchivedSlot);
    }

    [Fact]
    public void Deactivating_Moves_The_Persons_State_Out_Of_Reach()
    {
        var store = Store();
        var record = store.CreateUser(Ada());
        store.BindSubject(record.Resource.Id, "8f14e45f");

        var slot = new ScopedBowireUserStore(_root, "8f14e45f").Slot;
        Directory.CreateDirectory(slot);
        File.WriteAllText(Path.Combine(slot, "environments.json"), "{}");

        store.DeleteUser(record.Resource.Id);

        Assert.False(Directory.Exists(slot));
        Assert.NotNull(store.GetUser(record.Resource.Id)!.ArchivedSlot);
    }

    [Fact]
    public void Reactivating_Puts_The_State_Back_Where_It_Was()
    {
        // What makes "deactivate" a reversible operation rather than a polite
        // word for delete.
        var store = Store();
        var record = store.CreateUser(Ada());
        store.BindSubject(record.Resource.Id, "8f14e45f");

        var slot = new ScopedBowireUserStore(_root, "8f14e45f").Slot;
        Directory.CreateDirectory(slot);
        File.WriteAllText(Path.Combine(slot, "environments.json"), """{"mine":true}""");

        store.DeleteUser(record.Resource.Id);
        store.UpdateUser(record.Resource.Id, u => u.Active = true);

        Assert.Equal("""{"mine":true}""", File.ReadAllText(Path.Combine(slot, "environments.json")));
        Assert.Null(store.GetUser(record.Resource.Id)!.DeactivatedUtc);
    }

    [Fact]
    public void An_Identity_Nobody_Signed_In_As_Has_No_State_To_Move()
    {
        // Provisioning does not create a slot: the slot name is a function of
        // the token subject, which provisioning does not know. Deactivating
        // before a first sign-in must therefore be a no-op, not an error.
        var store = Store();
        var record = store.CreateUser(Ada());

        Assert.True(store.DeleteUser(record.Resource.Id));
        Assert.Null(store.GetUser(record.Resource.Id)!.ArchivedSlot);
    }

    // ---- the purge window ----

    [Fact]
    public void Nothing_Is_Purged_Before_The_Window_Closes()
    {
        var store = Store();
        var record = store.CreateUser(Ada());
        store.DeleteUser(record.Resource.Id);

        _clock.Now = _clock.Now.AddDays(29);

        Assert.Equal(0, store.Purge(TimeSpan.FromDays(30)));
        Assert.NotNull(store.GetUser(record.Resource.Id));
    }

    [Fact]
    public void Once_It_Closes_The_Record_And_The_State_Are_Gone()
    {
        var store = Store();
        var record = store.CreateUser(Ada());
        store.BindSubject(record.Resource.Id, "8f14e45f");

        var slot = new ScopedBowireUserStore(_root, "8f14e45f").Slot;
        Directory.CreateDirectory(slot);

        store.DeleteUser(record.Resource.Id);
        var kept = store.GetUser(record.Resource.Id)!.ArchivedSlot;
        _clock.Now = _clock.Now.AddDays(31);

        Assert.Equal(1, store.Purge(TimeSpan.FromDays(30)));
        Assert.Null(store.GetUser(record.Resource.Id));
        Assert.False(Directory.Exists(kept!));
    }

    [Fact]
    public void An_Active_Identity_Is_Never_Purged()
    {
        var store = Store();
        store.CreateUser(Ada());
        _clock.Now = _clock.Now.AddDays(365);

        Assert.Equal(0, store.Purge(TimeSpan.FromDays(30)));
        Assert.Single(store.Users());
    }

    // ---- groups ----

    [Fact]
    public void A_Group_Name_Can_Only_Belong_To_One_Group()
    {
        var store = Store();
        store.CreateGroup(new ScimGroup { DisplayName = "bowire-admins" });

        Assert.Throws<ScimConflictException>(
            () => store.CreateGroup(new ScimGroup { DisplayName = "Bowire-Admins" }));
    }

    [Fact]
    public void Membership_Is_What_Makes_Somebody_An_Administrator()
    {
        var store = Store();
        var ada = store.CreateUser(Ada());
        var grace = store.CreateUser(new ScimUser { UserName = "grace@example.com" });
        store.CreateGroup(new ScimGroup
        {
            DisplayName = "bowire-admins",
            Members = [new ScimValue { Value = ada.Resource.Id }],
        });

        Assert.True(store.IsMemberOf(ada.Resource.Id, "bowire-admins"));
        Assert.False(store.IsMemberOf(grace.Resource.Id, "bowire-admins"));
    }

    [Fact]
    public void A_Group_Nobody_Created_Confers_Nothing()
    {
        var store = Store();
        var ada = store.CreateUser(Ada());

        Assert.False(store.IsMemberOf(ada.Resource.Id, "bowire-admins"));
    }

    [Fact]
    public void Deleting_A_Group_Removes_It_Outright()
    {
        // Unlike a user, a group carries no state of its own, so there is
        // nothing a window would protect.
        var store = Store();
        var group = store.CreateGroup(new ScimGroup { DisplayName = "bowire-admins" });

        Assert.True(store.DeleteGroup(group.Id));
        Assert.Null(store.GetGroup(group.Id));
    }

    // ---- persistence ----

    [Fact]
    public void The_User_List_Survives_A_Restart()
    {
        var record = Store().CreateUser(Ada());

        var reopened = Store();

        Assert.Equal("ada@example.com", reopened.GetUser(record.Resource.Id)?.Resource.UserName);
    }

    [Fact]
    public void The_Purge_Window_Survives_A_Restart()
    {
        // The regression this guards: keeping deactivation time only in memory
        // restarts the window on every restart, so a nightly-restarted install
        // never purges anybody.
        var store = Store();
        var record = store.CreateUser(Ada());
        store.DeleteUser(record.Resource.Id);

        var reopened = Store();
        _clock.Now = _clock.Now.AddDays(31);

        Assert.Equal(1, reopened.Purge(TimeSpan.FromDays(30)));
    }

    [Fact]
    public void One_Unreadable_Record_Does_Not_Take_The_Directory_Down()
    {
        // An install that will not start because of a single corrupt file
        // locks everybody out — worse than one identity missing.
        var store = Store();
        store.CreateUser(Ada());
        File.WriteAllText(Path.Combine(_root, "scim", "users", "broken.json"), "not json");

        Assert.Single(Store().Users());
    }

    [Fact]
    public void Every_Decision_Lands_In_The_Audit_Log()
    {
        // "Who removed this person, and when" gets asked months later, and the
        // record files only ever show the current answer.
        var store = Store();
        var record = store.CreateUser(Ada());
        store.DeleteUser(record.Resource.Id);

        var log = File.ReadAllLines(store.EventLog);

        Assert.Equal(2, log.Length);
        Assert.Contains("create", log[0], StringComparison.Ordinal);
        Assert.Contains("delete", log[1], StringComparison.Ordinal);
        Assert.Contains("ada@example.com", log[1], StringComparison.Ordinal);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

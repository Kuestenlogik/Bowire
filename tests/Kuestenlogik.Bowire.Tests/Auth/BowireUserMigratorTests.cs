// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Tests.Auth;

/// <summary>
/// What happens to a single-user install's data when the install becomes
/// multi-tenant (#97, #28 Phase E).
/// </summary>
public sealed class BowireUserMigratorTests : IDisposable
{
    private const string Subject = "ada@example.com";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-migrate-" + Guid.NewGuid().ToString("N"));

    public BowireUserMigratorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Legacy(string relativePath, string content = "{}")
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private BowireUserMigrationPlan Plan(
        BowireUserMigrationMode mode = BowireUserMigrationMode.Prompt)
        => BowireUserMigrator.Plan(_root, Subject, mode);

    private string Slot => new ScopedBowireUserStore(_root, Subject).Slot;

    // ---- what is on offer ----

    [Fact]
    public void An_Install_With_Nothing_In_It_Has_Nothing_To_Offer()
    {
        // A fresh multi-tenant install must not greet its first user with a
        // prompt about data that does not exist.
        Assert.Equal(BowireUserMigrationState.NothingToMigrate, Plan().State);
    }

    [Fact]
    public void The_Legacy_State_Is_Offered_To_The_First_Identity()
    {
        Legacy("environments.json");
        Legacy("collections.json");

        var plan = Plan();

        Assert.Equal(BowireUserMigrationState.Available, plan.State);
        Assert.Equal(2, plan.Entries.Count);
        Assert.Contains(plan.Entries, e => e.RelativePath == "environments.json");
    }

    [Fact]
    public void A_Whole_Subtree_Is_Offered_Not_Just_The_Top_Level()
    {
        // Workspaces are the bulk of what people would lose, and they are
        // nested several levels deep.
        Legacy("workspaces/team-api/environments/staging.json");

        var plan = Plan();

        Assert.Equal(
            "workspaces/team-api/environments/staging.json",
            Assert.Single(plan.Entries).RelativePath);
    }

    [Fact]
    public void A_Store_Nobody_Has_Written_Yet_Is_Still_Migrated()
    {
        // The exclusion list is the whole rule: anything not named in it comes
        // along. An inclusion list would have to grow with every new store,
        // and forgetting would be silent data loss.
        Legacy("some-future-store.json");

        Assert.Equal(BowireUserMigrationState.Available, Plan().State);
    }

    [Theory]
    [InlineData("plugins/Acme.Protocol/plugin.dll")]
    [InlineData("certs/localhost.pfx")]
    [InlineData("logs/bowire.log")]
    [InlineData("cache/schema.bin")]
    [InlineData("state/update-check.json")]
    [InlineData("project.json")]
    public void What_Belongs_To_The_Machine_Stays_With_The_Machine(string path)
    {
        Legacy(path);
        Legacy("environments.json");

        var plan = Plan();

        Assert.Equal("environments.json", Assert.Single(plan.Entries).RelativePath);
    }

    [Fact]
    public void The_Slots_Themselves_Are_Never_Part_Of_The_Payload()
    {
        // Copying users/ into a slot would nest the entire tenancy inside one
        // identity, and do it again on every migration after that.
        Legacy($"{BowireUserSlot.DirectoryName}/somebody-else-1a2b3c4d/environments.json");
        Legacy("environments.json");

        var plan = Plan();

        Assert.Equal("environments.json", Assert.Single(plan.Entries).RelativePath);
    }

    [Fact]
    public void An_Install_That_Wants_To_Start_Clean_Is_Never_Asked()
    {
        Legacy("environments.json");

        Assert.Equal(BowireUserMigrationState.Disabled, Plan(BowireUserMigrationMode.Skip).State);
    }

    [Fact]
    public void A_Slot_That_Already_Holds_Work_Is_Left_Alone()
    {
        // Merging two sets of environments produces one set nobody can take
        // apart again.
        Legacy("environments.json");
        Directory.CreateDirectory(Slot);
        File.WriteAllText(Path.Combine(Slot, "collections.json"), "{}");

        Assert.Equal(BowireUserMigrationState.SlotNotEmpty, Plan().State);
    }

    // ---- accepting ----

    [Fact]
    public void Accepting_Puts_The_Files_In_The_Slot()
    {
        Legacy("environments.json", """{"envs":[]}""");
        Legacy("workspaces/team-api/collections.json");

        BowireUserMigrator.Apply(Plan());

        Assert.Equal("""{"envs":[]}""", File.ReadAllText(Path.Combine(Slot, "environments.json")));
        Assert.True(File.Exists(Path.Combine(Slot, "workspaces", "team-api", "collections.json")));
    }

    [Fact]
    public void The_Originals_Are_Still_There_Afterwards()
    {
        // Copy, never move: the install can be switched back to single-user
        // without a second migration, and a migration into the wrong slot is
        // recoverable. The operator deletes the originals when they choose.
        Legacy("environments.json");

        BowireUserMigrator.Apply(Plan());

        Assert.True(File.Exists(Path.Combine(_root, "environments.json")));
    }

    [Fact]
    public void Accepting_Is_Recorded_So_It_Is_Not_Offered_Twice()
    {
        Legacy("environments.json");

        var receipt = BowireUserMigrator.Apply(Plan());
        var after = Plan();

        Assert.Equal(BowireUserMigrationOutcome.Migrated, receipt.Outcome);
        Assert.Equal(1, receipt.Files);
        Assert.Equal(BowireUserMigrationState.AlreadyDecided, after.State);
        Assert.Equal(BowireUserMigrationOutcome.Migrated, after.Receipt?.Outcome);
        Assert.Equal(Subject, after.Receipt?.Subject);
    }

    [Fact]
    public void The_Receipt_Says_Where_The_Data_Came_From()
    {
        // What makes it an audit trail rather than a flag: an operator reading
        // it a year later can tell which install this state was lifted out of.
        Legacy("environments.json", new string('x', 40));

        var receipt = BowireUserMigrator.Apply(Plan());

        Assert.Equal(Path.GetFullPath(_root), receipt.Source);
        Assert.Equal(40, receipt.Bytes);
        Assert.True(receipt.DecidedUtc > DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void Nothing_Is_Left_Staged_Beside_The_Slot()
    {
        Legacy("environments.json");

        BowireUserMigrator.Apply(Plan());

        var usersRoot = Path.Combine(_root, BowireUserSlot.DirectoryName);
        Assert.Equal(
            new[] { Path.GetFileName(Slot) },
            Directory.EnumerateDirectories(usersRoot).Select(p => Path.GetFileName(p)!).ToArray());
    }

    [Fact]
    public void Applying_Something_That_Is_Not_On_Offer_Is_Refused()
    {
        // The plan carries the verdict; applying one that says otherwise would
        // copy over a slot the verdict already protected.
        Legacy("environments.json");
        var plan = Plan(BowireUserMigrationMode.Skip);

        var ex = Assert.Throws<InvalidOperationException>(() => BowireUserMigrator.Apply(plan));

        Assert.Contains("Disabled", ex.Message, StringComparison.Ordinal);
    }

    // ---- refusing ----

    [Fact]
    public void Declining_Is_Recorded_So_The_Prompt_Does_Not_Come_Back()
    {
        Legacy("environments.json");

        var receipt = BowireUserMigrator.Decline(Plan());
        var after = Plan();

        Assert.Equal(BowireUserMigrationOutcome.Declined, receipt.Outcome);
        Assert.Equal(BowireUserMigrationState.AlreadyDecided, after.State);
        Assert.Equal(BowireUserMigrationOutcome.Declined, after.Receipt?.Outcome);
    }

    [Fact]
    public void Declining_Copies_Nothing()
    {
        Legacy("environments.json");

        BowireUserMigrator.Decline(Plan());

        Assert.False(File.Exists(Path.Combine(Slot, "environments.json")));
        Assert.Equal(
            new[] { BowireUserMigrator.ReceiptFileName },
            Directory.EnumerateFileSystemEntries(Slot).Select(p => Path.GetFileName(p)!).ToArray());
    }

    [Fact]
    public void An_Unreadable_Receipt_Reads_As_No_Decision()
    {
        // The alternative is an identity that can never be offered a migration
        // and never told why. Re-offering is recoverable; that is not.
        //
        // It also pins the other half: the receipt lives in the slot, so the
        // "does this slot hold work" check has to look past it. Counting it
        // would report SlotNotEmpty here instead of Available.
        Legacy("environments.json");
        Directory.CreateDirectory(Slot);
        File.WriteAllText(Path.Combine(Slot, BowireUserMigrator.ReceiptFileName), "not json");

        Assert.Equal(BowireUserMigrationState.Available, Plan().State);
    }

    // ---- taking it back ----

    [Fact]
    public void Undoing_An_Accepted_Migration_Moves_The_Slot_Aside_Rather_Than_Deleting_It()
    {
        // The case: the operator's admin identity signs in first and takes the
        // data. Undo has to be safe enough that they will actually use it.
        Legacy("environments.json", """{"envs":[1]}""");
        BowireUserMigrator.Apply(Plan());

        var aside = BowireUserMigrator.Undo(Plan());

        Assert.NotNull(aside);
        Assert.False(Directory.Exists(Slot));
        Assert.Equal("""{"envs":[1]}""", File.ReadAllText(Path.Combine(aside!, "environments.json")));
    }

    [Fact]
    public void Undoing_Puts_The_Migration_Back_On_Offer()
    {
        Legacy("environments.json");
        BowireUserMigrator.Apply(Plan());
        BowireUserMigrator.Undo(Plan());

        Assert.Equal(BowireUserMigrationState.Available, Plan().State);
    }

    [Fact]
    public void What_Was_Set_Aside_Is_Not_A_Slot()
    {
        // It sits under users/ and must not be mistaken for an identity — by
        // a listing, or by the next migration looking for a free name.
        Legacy("environments.json");
        BowireUserMigrator.Apply(Plan());

        var aside = BowireUserMigrator.Undo(Plan());

        Assert.StartsWith(".", Path.GetFileName(aside)!, StringComparison.Ordinal);
    }

    [Fact]
    public void Undoing_A_Decline_Only_Removes_The_Record()
    {
        Legacy("environments.json");
        BowireUserMigrator.Decline(Plan());

        var aside = BowireUserMigrator.Undo(Plan());

        Assert.Null(aside);
        Assert.Equal(BowireUserMigrationState.Available, Plan().State);
    }

    [Fact]
    public void Undoing_A_Decline_Leaves_The_Work_Done_Since_Where_It_Is()
    {
        // Somebody declined, then built a workspace. Moving that aside would
        // hide the very thing they are looking at — so a decline's undo
        // touches only its own record, and the slot's contents then correctly
        // stop the offer coming back.
        Legacy("environments.json");
        BowireUserMigrator.Decline(Plan());
        File.WriteAllText(Path.Combine(Slot, "collections.json"), """{"mine":true}""");

        BowireUserMigrator.Undo(Plan());

        Assert.Equal("""{"mine":true}""", File.ReadAllText(Path.Combine(Slot, "collections.json")));
        Assert.Equal(BowireUserMigrationState.SlotNotEmpty, Plan().State);
    }

    [Fact]
    public void There_Is_Nothing_To_Undo_Before_Anything_Was_Decided()
    {
        Legacy("environments.json");

        var ex = Assert.Throws<InvalidOperationException>(() => BowireUserMigrator.Undo(Plan()));

        Assert.Contains("Available", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_Identity_Decides_For_Itself()
    {
        // One person declining must not answer for the next one to sign in.
        Legacy("environments.json");
        BowireUserMigrator.Decline(Plan());

        Assert.Equal(
            BowireUserMigrationState.Available,
            BowireUserMigrator.Plan(_root, "grace@example.com").State);
    }
}

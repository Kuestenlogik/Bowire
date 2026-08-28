// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Tests.Auth;

/// <summary>
/// The identity-scoped store and the dispatcher that decides whose turn it is
/// (#97).
/// </summary>
public sealed class BowireTenancyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-tenancy-" + Guid.NewGuid().ToString("N"));

    public BowireTenancyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private BowireTenancy Tenancy(out DefaultBowireUserStore shared)
    {
        shared = new DefaultBowireUserStore(_root);
        return new BowireTenancy(_root, shared);
    }

    // ---- the slot ----

    [Fact]
    public void A_Subject_Gets_A_Directory_Under_The_Slots_Root()
    {
        var store = new ScopedBowireUserStore(_root, "ada@example.com");

        Assert.Equal(
            Path.Combine(_root, BowireUserSlot.DirectoryName, store.Slug),
            store.Slot);
    }

    [Fact]
    public void State_Resolves_Inside_The_Subjects_Own_Slot()
    {
        var store = new ScopedBowireUserStore(_root, "ada@example.com");

        var path = store.GetUserPath("environments.json");

        Assert.Equal(Path.Combine(store.Slot, "environments.json"), path);
    }

    [Fact]
    public void A_Path_Climbing_Out_Of_The_Slot_Is_Refused()
    {
        // The slot next door belongs to someone else, so this is not a
        // traversal into a harmless directory — it is cross-tenant access.
        var store = new ScopedBowireUserStore(_root, "ada@example.com");

        Assert.Throws<ArgumentException>(
            () => store.GetUserPath(Path.Combine("..", "..", "somebody-else", "environments.json")));
    }

    [Fact]
    public void The_Storage_Root_Stays_The_Root_Even_Though_The_Slot_Moved()
    {
        // The regression this guards: BowirePathResolver derives the Data root
        // from the active store. If a scoped store reported its slot as the
        // root, the next store built from it would sit at users/a/users/a —
        // one level deeper on every swap.
        var first = new ScopedBowireUserStore(_root, "ada@example.com");
        var second = new ScopedBowireUserStore(first.StorageRoot, "ada@example.com");

        Assert.Equal(first.Slot, second.Slot);
        Assert.Equal(Path.GetFullPath(_root), first.StorageRoot);
    }

    [Fact]
    public void A_Store_Without_A_Subject_Is_Refused()
    {
        Assert.Throws<ArgumentException>(() => new ScopedBowireUserStore(_root, "  "));
        Assert.Throws<ArgumentException>(() => new ScopedBowireUserStore("  ", "ada"));
    }

    // ---- whose turn it is ----

    [Fact]
    public void Outside_A_Request_Calls_Go_To_The_Shared_Store()
    {
        // Background work — the plugin update check, a warm-up — has no
        // identity. Filing its state under whoever was served last would make
        // process-wide state follow a person around.
        var tenancy = Tenancy(out var shared);

        Assert.Equal(
            shared.GetUserPath("state"),
            tenancy.GetUserPath("state"));
    }

    [Fact]
    public void Inside_A_Request_Calls_Go_To_That_Identitys_Slot()
    {
        var tenancy = Tenancy(out _);

        using (BowireTenancy.Enter("ada@example.com"))
        {
            Assert.Equal(
                tenancy.For("ada@example.com").GetUserPath("collections.json"),
                tenancy.GetUserPath("collections.json"));
        }
    }

    [Fact]
    public void Two_Identities_Never_See_The_Same_File()
    {
        var tenancy = Tenancy(out _);

        string ada, grace;
        using (BowireTenancy.Enter("ada@example.com")) ada = tenancy.GetUserPath("collections.json");
        using (BowireTenancy.Enter("grace@example.com")) grace = tenancy.GetUserPath("collections.json");

        Assert.NotEqual(ada, grace);
    }

    [Fact]
    public void Leaving_A_Scope_Restores_The_One_Around_It()
    {
        // Nested scopes are legitimate — an admin acting on someone's behalf
        // (#98) is exactly that — so leaving the inner one must not drop the
        // outer identity for the rest of the request.
        var tenancy = Tenancy(out _);

        using (BowireTenancy.Enter("ada@example.com"))
        {
            using (BowireTenancy.Enter("grace@example.com"))
            {
                Assert.Equal("grace@example.com", BowireTenancy.CurrentSubject);
            }

            Assert.Equal("ada@example.com", BowireTenancy.CurrentSubject);
        }

        Assert.Null(BowireTenancy.CurrentSubject);
    }

    [Fact]
    public void An_Empty_Subject_Is_Not_An_Identity()
    {
        Assert.Throws<ArgumentException>(() => BowireTenancy.Enter("   "));
        Assert.Throws<ArgumentNullException>(() => BowireTenancy.Enter(null!));
    }

    [Fact]
    public void Asking_Twice_For_One_Subject_Returns_The_Same_Store()
    {
        // GetUserPath sits on read paths that run per request, and building a
        // slot hashes the subject.
        var tenancy = Tenancy(out _);

        Assert.Same(tenancy.For("ada@example.com"), tenancy.For("ada@example.com"));
    }

    [Fact]
    public void The_Slots_On_Disk_Can_Be_Listed()
    {
        var tenancy = Tenancy(out _);
        Directory.CreateDirectory(tenancy.For("ada@example.com").Slot);
        Directory.CreateDirectory(tenancy.For("grace@example.com").Slot);

        var slots = tenancy.EnumerateSlots().ToList();

        Assert.Equal(2, slots.Count);
        Assert.Contains(tenancy.For("ada@example.com").Slug, slots);
    }

    [Fact]
    public void Listing_Slots_Before_Anyone_Signed_In_Is_Empty_Not_An_Error()
        => Assert.Empty(Tenancy(out _).EnumerateSlots());
}

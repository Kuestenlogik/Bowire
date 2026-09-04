// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// The workspace list is the identity's, not the browser's (#646).
/// </summary>
/// <remarks>
/// <para>
/// Multi-tenancy separated everything inside a workspace and nothing about
/// which workspaces exist. The interesting assertions are therefore not "does
/// a round-trip work" but the two that the browser-only version could not
/// satisfy: two identities on one process must not see one list, and "never
/// saved" must stay distinguishable from "saved empty" — the distinction #612
/// cost the collections, where an empty disk answer was written over a
/// freshly seeded template.
/// </para>
/// </remarks>
[Collection("BowireUserContext")]
public sealed class WorkspaceInventoryStoreTests : IDisposable
{
    private const string TwoWorkspaces =
        """{"workspaces":[{"id":"personal","name":"Personal"},{"id":"team","name":"Team"}]}""";

    private const string NoWorkspaces = """{"workspaces":[]}""";

    private readonly IBowireUserStore _previousUsers = BowireUserContext.Current;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-inventory-" + Guid.NewGuid().ToString("N"));

    public WorkspaceInventoryStoreTests()
    {
        Directory.CreateDirectory(_root);
        BowireUserContext.Current = new DefaultBowireUserStore(_root);
    }

    public void Dispose()
    {
        WorkspaceInventoryStore.TestPathOverride = null;
        BowireUserContext.Current = _previousUsers;
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void NeverSavedIsNotSavedEmpty()
    {
        // The whole reason the envelope carries `everSaved` separately from
        // the array's length. Collapsing the two either loses a list the
        // browser still holds or resurrects one somebody deleted on purpose.
        Assert.Null(WorkspaceInventoryStore.Load());

        Assert.True(WorkspaceInventoryStore.Save(NoWorkspaces));

        Assert.Equal(NoWorkspaces, WorkspaceInventoryStore.Load());
    }

    [Fact]
    public void TheListRoundTripsVerbatim()
    {
        // Verbatim on purpose: the server has no opinion about a workspace
        // record's fields, so adding one in the workbench must not need a
        // server release.
        WorkspaceInventoryStore.Save(TwoWorkspaces);

        Assert.Equal(TwoWorkspaces, WorkspaceInventoryStore.Load());
    }

    [Fact]
    public void TwoIdentitiesKeepTwoLists()
    {
        // The defect this exists to remove. Before, both identities read one
        // localStorage key in one browser profile, so deleting an entry
        // removed it for the other person.
        BowireUserContext.Current = new ScopedBowireUserStore(_root, "alice@example.com");
        WorkspaceInventoryStore.Save(TwoWorkspaces);

        BowireUserContext.Current = new ScopedBowireUserStore(_root, "bob@example.com");
        Assert.Null(WorkspaceInventoryStore.Load());

        WorkspaceInventoryStore.Save(NoWorkspaces);

        BowireUserContext.Current = new ScopedBowireUserStore(_root, "alice@example.com");
        Assert.Equal(TwoWorkspaces, WorkspaceInventoryStore.Load());
    }

    [Fact]
    public void TheFileLandsInTheIdentitysSlot()
    {
        // Not decoration: deprovisioning archives the slot directory whole
        // and `bowire users migrate` walks everything under the root that is
        // not on its not-personal list. Both reach this file only because of
        // where it is, which is why neither needed a line of its own.
        var store = new ScopedBowireUserStore(_root, "alice@example.com");
        BowireUserContext.Current = store;

        WorkspaceInventoryStore.Save(TwoWorkspaces);

        var expected = Path.Combine(store.Slot, "workspaces.json");
        Assert.True(File.Exists(expected), expected + " should exist");
    }

    [Fact]
    public void ACorruptFileReadsAsNeverSaved()
    {
        // So the workbench opens on the browser's copy rather than on an
        // error page in front of everything the person has.
        var path = Path.Combine(_root, "workspaces.json");
        File.WriteAllText(path, "{ this is not json");

        Assert.Null(WorkspaceInventoryStore.Load());
    }

    [Fact]
    public void AMalformedSaveLeavesNothingBehind()
    {
        // Validated before the path is touched: a bad PUT must not be able to
        // leave a file the next load has to recover from.
        // ThrowsAny, not Throws: the contract is "the JSON is rejected", and
        // JsonDocument.Parse raises JsonReaderException, a JsonException
        // subtype. Assert.Throws demands an exact match and would pin this
        // test to an implementation detail of System.Text.Json.
        Assert.ThrowsAny<JsonException>(() => WorkspaceInventoryStore.Save("{ nope"));

        Assert.False(File.Exists(Path.Combine(_root, "workspaces.json")));
        Assert.Null(WorkspaceInventoryStore.Load());
    }

    [Fact]
    public void AnEmptyPayloadIsRejected()
    {
        Assert.Throws<ArgumentException>(() => WorkspaceInventoryStore.Save("   "));
    }
}

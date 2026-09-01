// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;

namespace Kuestenlogik.Bowire.Scim.Tests;

/// <summary>
/// A directory the size of a real one (#96).
/// </summary>
/// <remarks>
/// <para>
/// The acceptance criterion asks for 10 000 users listed in under half a
/// second with a warm index. The interesting part turned out not to be the
/// listing — that was always in memory — but everything around it: filling
/// the directory cost a scan of everyone already in it, so provisioning was
/// quadratic, and listing stamped an absolute URL onto all 10 000 records in
/// order to return fifty.
/// </para>
/// <para>
/// The timing assertions are deliberately loose against what the code now
/// does: an in-memory sort and filter of 10 000 is single-digit milliseconds,
/// so half a second is roughly two orders of magnitude of headroom. That is
/// the point — a threshold tight enough to fail on a busy runner would be
/// measuring the runner, and this suite has paid for that lesson already
/// (#637). What these catch is a return to walking the directory.
/// </para>
/// </remarks>
public sealed class ScimScaleTests : IDisposable
{
    private const int Directory10K = 10_000;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-scim-scale-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void TenThousandUsersListAndFilterWellInsideTheBudget()
    {
        var store = Seeded(Directory10K);

        // Warm: the first call is the load, which reads the directory off
        // disk. The criterion says "with a warm index" and means it — a cold
        // start reading 10 000 files is a different measurement.
        _ = store.Users();

        var listed = Measure(() => store.Users());
        Assert.Equal(Directory10K, listed.Result.Count);
        Assert.True(listed.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Listing {Directory10K} users took {listed.Elapsed.TotalMilliseconds:F0} ms.");

        // The filter an identity provider actually sends: "do you already
        // have this person?", once per user it is about to sync.
        var filter = ScimFilter.Parse("userName eq \"user-7777@example.com\"");
        var filtered = Measure(() => store.Users()
            .Where(r => filter.Matches(name => string.Equals(name, "userName", StringComparison.OrdinalIgnoreCase)
                ? r.Resource.UserName
                : null))
            .ToList());

        Assert.Single(filtered.Result);
        Assert.True(filtered.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Filtering {Directory10K} users took {filtered.Elapsed.TotalMilliseconds:F0} ms.");
    }

    [Fact]
    public void LookingSomebodyUpByNameDoesNotWalkTheDirectory()
    {
        // The lookup on the sign-in path, and the one every create has to do
        // before it can accept a userName. Ten thousand of them: if this is
        // still a scan, it is a hundred million comparisons and the test
        // stops looking instant.
        var store = Seeded(Directory10K);
        _ = store.Users();

        var lookups = Measure(() =>
        {
            var found = 0;
            for (var i = 0; i < Directory10K; i++)
            {
                if (store.FindByUserName(UserName(i)) is not null) found++;
            }
            return found;
        });

        Assert.Equal(Directory10K, lookups.Result);
        Assert.True(lookups.Elapsed < TimeSpan.FromSeconds(2),
            $"{Directory10K} lookups took {lookups.Elapsed.TotalMilliseconds:F0} ms.");
    }

    // ---- the invariant the index introduced ----

    [Fact]
    public void RenamingThroughReplaceFreesTheOldName()
    {
        var store = new BowireScimStore(_root);
        var created = store.CreateUser(User("ada@example.com"));

        store.ReplaceUser(created.Resource.Id, User("ada.lovelace@example.com"));

        Assert.Null(store.FindByUserName("ada@example.com"));
        Assert.NotNull(store.FindByUserName("ada.lovelace@example.com"));

        // And the freed name is available again, which a stale index would
        // refuse with a conflict.
        var reused = store.CreateUser(User("ada@example.com"));
        Assert.NotEqual(created.Resource.Id, reused.Resource.Id);
    }

    [Fact]
    public void RenamingThroughPatchFreesTheOldName()
    {
        // The path where the rename happens inside a callback, after the
        // index has already been told the old name.
        var store = new BowireScimStore(_root);
        var created = store.CreateUser(User("grace@example.com"));

        store.UpdateUser(created.Resource.Id, u => u.UserName = "grace.hopper@example.com");

        Assert.Null(store.FindByUserName("grace@example.com"));
        Assert.NotNull(store.FindByUserName("grace.hopper@example.com"));
    }

    [Fact]
    public void TheNameIsStillTakenWhileSomebodyHoldsIt()
    {
        var store = new BowireScimStore(_root);
        store.CreateUser(User("ada@example.com"));

        // Case-insensitive, because an identity provider will not agree with
        // you about capitalisation.
        Assert.Throws<ScimConflictException>(() => store.CreateUser(User("ADA@example.com")));
    }

    [Fact]
    public void TheIndexSurvivesAReload()
    {
        var store = new BowireScimStore(_root);
        store.CreateUser(User("ada@example.com"));

        // A second store over the same directory is what a restart looks
        // like: the index has to come back from the files, not from memory.
        var reopened = new BowireScimStore(_root);

        Assert.NotNull(reopened.FindByUserName("ada@example.com"));
        Assert.Throws<ScimConflictException>(() => reopened.CreateUser(User("ada@example.com")));
    }

    // ---- plumbing ----

    private BowireScimStore Seeded(int count)
    {
        var store = new BowireScimStore(_root);
        for (var i = 0; i < count; i++) store.CreateUser(User(UserName(i)));
        return store;
    }

    private static string UserName(int i)
        => string.Create(CultureInfo.InvariantCulture, $"user-{i}@example.com");

    private static ScimUser User(string userName) => new() { UserName = userName, Active = true };

    private static (T Result, TimeSpan Elapsed) Measure<T>(Func<T> work)
    {
        var sw = Stopwatch.StartNew();
        var result = work();
        sw.Stop();
        return (result, sw.Elapsed);
    }
}

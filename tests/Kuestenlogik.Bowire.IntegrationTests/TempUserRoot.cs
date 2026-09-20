// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// A user-storage root of this test class's own, for as long as it runs — and
/// a check that it actually took (#734).
/// </summary>
/// <remarks>
/// <para>
/// For any suite that touches a disk-backed store: without a root of its own
/// such a test writes into whatever <c>~/.bowire</c> the machine running the
/// suite has, and leaves its fixtures behind there.
/// </para>
/// <para>
/// This scopes rather than assigning <see cref="BowireUserContext.Current"/>,
/// which several suites here do: that replaces the <em>process</em> default
/// and is why those suites had to be serialised into one collection. A scope
/// reaches only what flows from it, so a class holding one stays parallel with
/// the rest.
/// </para>
/// <para>
/// The catch, and the reason <see cref="Dispose"/> does more than delete a
/// directory: a scope is an <see cref="AsyncLocal{T}"/>, and there are at
/// least three ways to open one that does not reach the test.
/// </para>
/// <list type="bullet">
/// <item><description>Opened inside an <c>async</c> method — including
/// <c>IAsyncLifetime.InitializeAsync</c>. The value is discarded when that
/// method returns and the caller gets the disposable and none of the
/// effect.</description></item>
/// <item><description>Opened in a collection fixture, whose constructor runs
/// in a context the tests do not descend from.</description></item>
/// <item><description>Opened correctly, but the store is touched inside a
/// <c>TestServer</c> request handler, and <c>TestServer</c> drops the
/// caller's execution context unless <c>PreserveExecutionContext</c> is
/// set.</description></item>
/// </list>
/// <para>
/// All three fail the same way: silently, by writing into the developer's own
/// storage. A check at construction time cannot see any of them — the scope
/// <em>is</em> in force at that moment. So the check happens at disposal and
/// looks at the filesystem instead of at the scope: whatever this class did,
/// the real <c>~/.bowire</c> has to come out of it unchanged.
/// </para>
/// </remarks>
internal sealed class TempUserRoot : IDisposable
{
    private readonly string _root;
    private readonly IDisposable _scope;
    private readonly IReadOnlyDictionary<string, (long Length, DateTime WrittenUtc)> _before;

    public TempUserRoot(string label)
    {
        _root = Path.Combine(Path.GetTempPath(), $"bowire-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _before = RealStorageSnapshot.Take();
        _scope = BowireUserContext.Enter(new DefaultBowireUserStore(_root));
    }

    /// <summary>Where this class's stores resolve to.</summary>
    public string Root => _root;

    public void Dispose()
    {
        _scope.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }

        RealStorageSnapshot.AssertUnchanged(_before);
    }
}

/// <summary>
/// The developer's own <c>~/.bowire</c>, before and after — so a test that
/// failed to isolate itself says so instead of quietly editing it (#734).
/// </summary>
internal static class RealStorageSnapshot
{
    /// <summary>
    /// Every file under the real storage root with its length and write time.
    /// An absent root is an empty snapshot, which is the right baseline: a
    /// file appearing under it is then a failure like any other.
    /// </summary>
    public static IReadOnlyDictionary<string, (long Length, DateTime WrittenUtc)> Take()
    {
        var root = RealRoot();
        var found = new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);
        if (root is null || !Directory.Exists(root)) return found;

        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(path);
                found[path] = (info.Length, info.LastWriteTimeUtc);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable profile cannot be compared, so it is not asserted
            // on. Refusing to run the suite over it would be worse.
        }
        return found;
    }

    /// <summary>
    /// Fail when anything under the real storage root was added, removed or
    /// rewritten since <paramref name="before"/> was taken.
    /// </summary>
    public static void AssertUnchanged(
        IReadOnlyDictionary<string, (long Length, DateTime WrittenUtc)> before)
    {
        var after = Take();

        var added = after.Keys.Where(k => !before.ContainsKey(k)).ToArray();
        var removed = before.Keys.Where(k => !after.ContainsKey(k)).ToArray();
        var changed = after
            .Where(kv => before.TryGetValue(kv.Key, out var was) && was != kv.Value)
            .Select(kv => kv.Key)
            .ToArray();

        if (added.Length == 0 && removed.Length == 0 && changed.Length == 0) return;

        var what = new List<string>();
        if (added.Length > 0) what.Add("added: " + string.Join(", ", added.Take(5)));
        if (changed.Length > 0) what.Add("changed: " + string.Join(", ", changed.Take(5)));
        if (removed.Length > 0) what.Add("removed: " + string.Join(", ", removed.Take(5)));

        throw new InvalidOperationException(
            "This test wrote into the real user storage instead of its own temp root. "
            + "The scope did not reach the code under test — the usual causes are opening it "
            + "inside an async method (IAsyncLifetime.InitializeAsync included), opening it in a "
            + "collection fixture, or driving a TestServer without PreserveExecutionContext. "
            + string.Join("; ", what));
    }

    private static string? RealRoot()
    {
        try
        {
            // The same resolver the product uses, so BOWIRE_DATA_DIR moves the
            // check with it — a CI run that redirects storage is isolated by
            // construction and has nothing to guard.
            return BowirePathResolver.DataDirOverride()
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bowire");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return null;
        }
    }
}

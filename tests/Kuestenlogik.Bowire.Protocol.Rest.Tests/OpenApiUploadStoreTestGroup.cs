// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Protocol.Rest.Tests;

/// <summary>
/// A user-storage root of this test class's own, for as long as it runs.
/// </summary>
/// <remarks>
/// <para>
/// Since #654 an uploaded OpenAPI document is a file in the identity's slot
/// rather than an entry in a process-wide list, so <c>Clear()</c> alone no
/// longer isolates a test: without a scope it would reach into whatever
/// <c>~/.bowire</c> the machine running the suite happens to have, and leave
/// test documents behind in it.
/// </para>
/// <para>
/// Held per class rather than once for the collection, although the classes
/// here are already serialised. <see cref="BowireUserContext.Enter"/> rides an
/// <see cref="AsyncLocal{T}"/>, which reaches what flows from where it was
/// set; a collection fixture is constructed somewhere else and would be
/// relying on xunit's internal ordering for that to hold. The alternative,
/// assigning <see cref="BowireUserContext.Current"/>, replaces the
/// <em>process</em> default and would leak into the other collections of this
/// assembly running beside it. A field in the constructor is the one that is
/// true by construction.
/// </para>
/// </remarks>
internal sealed class TempUserRoot : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-openapi-" + Guid.NewGuid().ToString("N"));

    private readonly IDisposable _userScope;
    private readonly IReadOnlyDictionary<string, (long Length, DateTime WrittenUtc)> _before;

    public TempUserRoot()
    {
        Directory.CreateDirectory(_root);
        _before = RealStorageSnapshot.Take();
        _userScope = BowireUserContext.Enter(new DefaultBowireUserStore(_root));
    }

    public void Dispose()
    {
        _userScope.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }

        // #734 — the scope is an AsyncLocal and there are several ways to open
        // one that never reaches the test, all of them silent: the damage shows
        // up as files in the developer's own ~/.bowire. Checking the filesystem
        // at disposal catches every form, including forgetting to isolate at
        // all, which no check on the scope itself can see.
        RealStorageSnapshot.AssertUnchanged(_before);
    }
}

/// <summary>
/// The developer's own storage root, before and after — so a test that failed
/// to isolate itself says so instead of quietly editing it (#734).
/// </summary>
internal static class RealStorageSnapshot
{
    /// <summary>
    /// Every file under the real storage root with its length and write time.
    /// An absent root is an empty snapshot: a file appearing under it is then
    /// a failure like any other.
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
            // An unreadable profile cannot be compared, so it is not asserted on.
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
            return Kuestenlogik.Bowire.Projects.BowirePathResolver.DataDirOverride()
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bowire");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// xunit.v3 collection marker that serialises every test class touching
/// <see cref="OpenApiUploadStore"/>. Both <c>BowireRestProtocolTests</c>
/// and <c>OpenApiUploadStoreTests</c> add to and clear the same store,
/// so without serialisation one class's <c>Clear</c> can race
/// another class's <c>GetAll</c>/<c>Single</c> assertion.
/// </summary>
// xunit1027: collection definition classes must be public so xunit's
// reflection-based discovery can see them. CA1515 prefers internal for
// types not consumed across assemblies — disable it here, the xunit
// analyser wins.
#pragma warning disable CA1515
[CollectionDefinition(nameof(OpenApiUploadStoreTestGroup))]
public sealed class OpenApiUploadStoreTestGroup;
#pragma warning restore CA1515

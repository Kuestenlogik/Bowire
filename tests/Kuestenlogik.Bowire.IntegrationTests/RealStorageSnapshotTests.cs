// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// The guard that catches a test which failed to isolate its storage (#734).
/// </summary>
/// <remarks>
/// <para>
/// A guard nobody has seen fire is a guard nobody should trust, and this one
/// exists precisely because the failure it watches for is silent. So these
/// make it fire, against a storage root pointed somewhere harmless.
/// </para>
/// <para>
/// <c>BOWIRE_DATA_DIR</c> is what moves the real root, and it is read on every
/// call rather than captured, so setting it here aims both the snapshot and
/// the assertion at a temp directory. That is the same mechanism a CI run uses
/// to isolate itself wholesale.
/// </para>
/// </remarks>
[Collection("BowireUserContext")]
public sealed class RealStorageSnapshotTests : IDisposable
{
    private readonly string _fakeRealRoot = Path.Combine(
        Path.GetTempPath(), "bowire-guard-" + Guid.NewGuid().ToString("N"));

    private readonly string? _previousDataDir =
        Environment.GetEnvironmentVariable(BowirePathResolver.DataDirVariable);

    public RealStorageSnapshotTests()
    {
        Directory.CreateDirectory(_fakeRealRoot);
        Environment.SetEnvironmentVariable(BowirePathResolver.DataDirVariable, _fakeRealRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(BowirePathResolver.DataDirVariable, _previousDataDir);
        try { Directory.Delete(_fakeRealRoot, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void An_Untouched_Storage_Root_Passes()
    {
        File.WriteAllText(Path.Combine(_fakeRealRoot, "environments.json"), "{}");

        var before = RealStorageSnapshot.Take();

        RealStorageSnapshot.AssertUnchanged(before);
    }

    [Fact]
    public void A_File_That_Appeared_Is_Reported()
    {
        // The commonest shape: a test uploads something and it lands in the
        // developer's profile because the scope never reached the endpoint.
        var before = RealStorageSnapshot.Take();

        Directory.CreateDirectory(Path.Combine(_fakeRealRoot, "schemas"));
        File.WriteAllText(Path.Combine(_fakeRealRoot, "schemas", "beacon.proto"), "syntax = \"proto3\";");

        var ex = Assert.Throws<InvalidOperationException>(() => RealStorageSnapshot.AssertUnchanged(before));
        Assert.Contains("beacon.proto", ex.Message, StringComparison.Ordinal);
        // The message has to name the causes, because the person reading it is
        // looking at a test that seemed to pass a moment ago.
        Assert.Contains("async method", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_File_That_Changed_Is_Reported()
    {
        // What plugin-visibility.spec.ts did: it edited an existing file
        // rather than creating one, and hid a protocol in the real workbench.
        var path = Path.Combine(_fakeRealRoot, "hidden-protocols.json");
        File.WriteAllText(path, """{"hidden":[]}""");

        var before = RealStorageSnapshot.Take();

        File.WriteAllText(path, """{"hidden":["amqp"]}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));

        var ex = Assert.Throws<InvalidOperationException>(() => RealStorageSnapshot.AssertUnchanged(before));
        Assert.Contains("hidden-protocols.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_File_That_Vanished_Is_Reported()
    {
        // A test that clears a store it does not own. Same damage, opposite
        // direction, and a snapshot that only looked for new files would miss it.
        var path = Path.Combine(_fakeRealRoot, "workspaces.json");
        File.WriteAllText(path, """{"workspaces":[]}""");

        var before = RealStorageSnapshot.Take();
        File.Delete(path);

        var ex = Assert.Throws<InvalidOperationException>(() => RealStorageSnapshot.AssertUnchanged(before));
        Assert.Contains("workspaces.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Storage_Root_That_Does_Not_Exist_Is_An_Empty_Baseline()
    {
        // CI, or a machine nobody has run Bowire on. A file appearing under it
        // is then still a failure, which is the behaviour worth having.
        Directory.Delete(_fakeRealRoot, recursive: true);

        var before = RealStorageSnapshot.Take();
        Assert.Empty(before);

        Directory.CreateDirectory(_fakeRealRoot);
        File.WriteAllText(Path.Combine(_fakeRealRoot, "stray.json"), "{}");

        Assert.Throws<InvalidOperationException>(() => RealStorageSnapshot.AssertUnchanged(before));
    }

    [Fact]
    public void A_Properly_Scoped_Helper_Comes_And_Goes_Without_A_Trace()
    {
        // The whole point, end to end: the helper used correctly leaves the
        // storage root exactly as it found it, so Dispose stays quiet.
        File.WriteAllText(Path.Combine(_fakeRealRoot, "environments.json"), "{}");

        using (var storage = new TempUserRoot("guard-selftest"))
        {
            File.WriteAllText(Path.Combine(storage.Root, "collections.json"), """{"collections":[]}""");
        }

        // No throw from Dispose, and the root still holds only what it had.
        Assert.Single(Directory.GetFiles(_fakeRealRoot, "*", SearchOption.AllDirectories));
    }
}

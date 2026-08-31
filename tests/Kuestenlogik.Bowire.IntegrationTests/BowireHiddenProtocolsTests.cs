// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// "I don't use MQTT; stop showing it to me" (#638).
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="BowireDisabledPluginsStore"/>, and the pair
/// only makes sense together: disabling unloads a plugin from the process, so
/// it is one decision for everybody and its file sits in the storage root
/// (#284 Phase D). Hiding is a preference, so it sits in the identity's own
/// slot and nobody else can tell.
/// </para>
/// <para>
/// The test that matters most is <see cref="TwoIdentitiesKeepTwoSets"/>: a
/// single process-wide cache is precisely the defect this store was written
/// to avoid repeating.
/// </para>
/// </remarks>
[Collection("BowireUserContext")]
public sealed class BowireHiddenProtocolsTests : IDisposable
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-hidden-" + Guid.NewGuid().ToString("N"));

    private readonly IBowireUserStore _previousUsers = BowireUserContext.Current;

    public BowireHiddenProtocolsTests()
    {
        Directory.CreateDirectory(_root);
        BowireHiddenProtocolsStore.ResetForTests();
    }

    public void Dispose()
    {
        BowireUserContext.Current = _previousUsers;
        BowireHiddenProtocolsStore.ResetForTests();
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void TwoIdentitiesKeepTwoSets()
    {
        // One process, two slots. Ada tidying her sidebar must not tidy
        // Grace's — the whole reason this is not the disabled list.
        BowireUserContext.Current = new ScopedBowireUserStore(_root, Ada);
        BowireHiddenProtocolsStore.SetHidden("mqtt", hidden: true);

        BowireUserContext.Current = new ScopedBowireUserStore(_root, Grace);

        Assert.False(BowireHiddenProtocolsStore.IsHidden("mqtt"));
        Assert.Empty(BowireHiddenProtocolsStore.Snapshot());

        BowireUserContext.Current = new ScopedBowireUserStore(_root, Ada);
        Assert.True(BowireHiddenProtocolsStore.IsHidden("mqtt"));
    }

    [Fact]
    public void NothingIsHiddenUntilSomebodyHidesSomething()
    {
        // No file, no preference, no surprises. A workbench that starts by
        // showing everything is the only safe default.
        BowireUserContext.Current = new ScopedBowireUserStore(_root, Ada);

        Assert.Empty(BowireHiddenProtocolsStore.Snapshot());
        Assert.False(BowireHiddenProtocolsStore.IsHidden("mqtt"));
        Assert.False(File.Exists(HiddenFileFor(Ada)));
    }

    [Fact]
    public void ItSurvivesTheProcessForgetting()
    {
        BowireUserContext.Current = new ScopedBowireUserStore(_root, Ada);
        BowireHiddenProtocolsStore.SetHidden("mqtt", hidden: true);

        // Stands in for a restart: the cache goes, the file stays.
        BowireHiddenProtocolsStore.ResetForTests();

        Assert.True(BowireHiddenProtocolsStore.IsHidden("mqtt"));
    }

    [Fact]
    public void ShowingAgainIsReachable()
    {
        // A preference nobody can undo is a bug with a nice name.
        BowireUserContext.Current = new ScopedBowireUserStore(_root, Ada);
        BowireHiddenProtocolsStore.SetHidden("mqtt", hidden: true);

        Assert.True(BowireHiddenProtocolsStore.SetHidden("mqtt", hidden: false));
        BowireHiddenProtocolsStore.ResetForTests();

        Assert.False(BowireHiddenProtocolsStore.IsHidden("mqtt"));
    }

    [Fact]
    public void SettingWhatIsAlreadySetChangesNothing()
    {
        BowireUserContext.Current = new ScopedBowireUserStore(_root, Ada);

        Assert.True(BowireHiddenProtocolsStore.SetHidden("mqtt", hidden: true));
        Assert.False(BowireHiddenProtocolsStore.SetHidden("mqtt", hidden: true));
    }

    [Fact]
    public void AnUnreadableFileMeansNothingIsHidden()
    {
        // Fail towards showing. A protocol that reappears is a visible
        // annoyance; one that vanishes because a file got corrupted is a
        // support case nobody can reproduce.
        BowireUserContext.Current = new ScopedBowireUserStore(_root, Ada);
        var path = HiddenFileFor(Ada);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");

        Assert.Empty(BowireHiddenProtocolsStore.Snapshot());
    }

    [Fact]
    public void TheFileIsTheHandEditableShapeItLooksLike()
    {
        // Documented as { "hidden": [...] }, and an operator diffing a slot
        // should find exactly that.
        BowireUserContext.Current = new ScopedBowireUserStore(_root, Ada);
        BowireHiddenProtocolsStore.SetHidden("mqtt", hidden: true);
        BowireHiddenProtocolsStore.SetHidden("grpc", hidden: true);

        var json = File.ReadAllText(HiddenFileFor(Ada));

        Assert.Contains("\"hidden\"", json, StringComparison.Ordinal);
        Assert.Contains("mqtt", json, StringComparison.Ordinal);
        Assert.Contains("grpc", json, StringComparison.Ordinal);
    }

    private string HiddenFileFor(string subject)
        => new ScopedBowireUserStore(_root, subject).GetUserPath("hidden-protocols.json");
}

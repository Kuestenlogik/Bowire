// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.Tests.Projects;

/// <summary>
/// Where Bowire decides to put things (#616).
/// </summary>
/// <remarks>
/// Every rule here fails silently if it is wrong, which is why they are worth
/// pinning: a machine scope that quietly resolves to a user profile looks like
/// it worked until an admin and a service account disagree about which plugins
/// are installed, and an instance segment that quietly does nothing looks like
/// it worked until two instances overwrite each other's state.
/// </remarks>
public sealed class BowirePathResolverTests
{
    private const string UserRoot = "/home/dev/.bowire";

    private static BowirePathResolver Resolver(
        string? dataDir = null, string? instance = null, string dataRoot = UserRoot)
        => new(
            name => name switch
            {
                BowirePathResolver.DataDirVariable => dataDir,
                BowirePathResolver.InstanceVariable => instance,
                _ => null,
            },
            () => dataRoot);

    private static string[] Segments(string path) => path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    // ---- data scope ----

    [Fact]
    public void The_Data_Scope_Follows_Whatever_The_Storage_Root_Decided()
    {
        // Which is the point of taking it as a delegate: a project that opted
        // into .bowire/ storage moves everything, not just the stores that
        // remembered to ask.
        Assert.Equal(UserRoot, Resolver().Root(BowireStorageScope.Data));
        Assert.Equal("/repo/.bowire", Resolver(dataRoot: "/repo/.bowire").Root(BowireStorageScope.Data));
    }

    [Fact]
    public void Segments_Are_Appended_Under_The_Root()
    {
        var path = Resolver().Resolve(BowireStorageScope.Data, "plugins", "Kuestenlogik.Bowire.Protocol.Rest");

        // The tail, not the whole path: SafePath normalises to an absolute
        // path, so a POSIX root under test on Windows picks up a drive letter.
        // What this is about is that the segments land under the root in
        // order, which the tail states without asserting the host's idea of
        // what "/home" means.
        Assert.Equal(
            [".bowire", "plugins", "Kuestenlogik.Bowire.Protocol.Rest"],
            Segments(path).TakeLast(3));
    }

    [Fact]
    public void No_Segments_Yields_The_Root_Itself()
        => Assert.Equal(Resolver().Root(BowireStorageScope.Data), Resolver().Resolve(BowireStorageScope.Data));

    [Fact]
    public void A_Rooted_Segment_Is_Refused_Rather_Than_Replacing_The_Root()
    {
        // Path.Combine would silently discard everything before it and write
        // wherever the caller's string pointed.
        var resolver = Resolver();

        Assert.ThrowsAny<ArgumentException>(
            () => resolver.Resolve(BowireStorageScope.Data, OperatingSystem.IsWindows() ? @"C:\elsewhere" : "/elsewhere"));
    }

    [Fact]
    public void A_Segment_Climbing_Out_Of_The_Root_Is_Refused()
        => Assert.ThrowsAny<ArgumentException>(
            () => Resolver().Resolve(BowireStorageScope.Data, "..", "..", "etc", "passwd"));

    // ---- machine scope ----

    [Fact]
    public void The_Machine_Scope_Does_Not_Depend_On_Which_Account_Is_Running()
    {
        // The whole reason it exists: a service instance and the admin who
        // configured it must resolve to the same directory.
        var root = Resolver().Root(BowireStorageScope.Machine);

        Assert.DoesNotContain("/home/dev", root, StringComparison.Ordinal);

        if (OperatingSystem.IsWindows())
        {
            // ProgramData, not the user profile.
            Assert.Contains("Bowire", root, StringComparison.Ordinal);
            Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), root,
                StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // /var/lib rather than SpecialFolder.CommonApplicationData, which
            // on .NET for Unix is /usr/share — static package data, not state
            // a service writes.
            Assert.Equal("/var/lib/bowire", root);
        }
    }

    [Fact]
    public void The_Machine_And_Data_Scopes_Are_Different_Places()
        => Assert.NotEqual(
            Resolver().Root(BowireStorageScope.Data),
            Resolver().Root(BowireStorageScope.Machine));

    // ---- the test override ----

    [Fact]
    public void BOWIRE_DATA_DIR_Redirects_Every_Scope_To_One_Tree()
    {
        // What makes it usable as a fixture: one directory to create and one
        // to delete, instead of hunting for state that a single scope missed.
        var resolver = Resolver(dataDir: "/tmp/fixture");

        Assert.Equal("/tmp/fixture", resolver.Root(BowireStorageScope.Data));
        Assert.Equal("/tmp/fixture", resolver.Root(BowireStorageScope.Machine));
    }

    [Fact]
    public void An_Empty_BOWIRE_DATA_DIR_Is_Treated_As_Unset()
    {
        // An exported-but-empty variable is a shell accident, not a request to
        // store everything at the filesystem root.
        Assert.Equal(UserRoot, Resolver(dataDir: "").Root(BowireStorageScope.Data));
        Assert.Equal(UserRoot, Resolver(dataDir: "   ").Root(BowireStorageScope.Data));
    }

    // ---- the instance segment ----

    [Fact]
    public void No_Instance_Means_The_Root_Itself_So_Nothing_Moves()
    {
        // The single-instance case has to stay exactly where it was, or this
        // feature relocates everybody's data on upgrade.
        Assert.Equal(UserRoot, Resolver(instance: null).Root(BowireStorageScope.Data));
        Assert.Equal(UserRoot, Resolver(instance: "").Root(BowireStorageScope.Data));
    }

    [Fact]
    public void An_Instance_Adds_One_Segment_Under_Every_Root()
    {
        Assert.Equal([".bowire", "staging"],
            Segments(Resolver(instance: "staging").Root(BowireStorageScope.Data)).TakeLast(2));

        // Machine scope too — separating instances is not much use if their
        // service-visible state still collides.
        Assert.EndsWith("staging", Resolver(instance: "staging").Root(BowireStorageScope.Machine),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Two_Instances_Do_Not_Share_A_Directory()
        => Assert.NotEqual(
            Resolver(instance: "staging").Resolve(BowireStorageScope.Data, "collections.json"),
            Resolver(instance: "prod").Resolve(BowireStorageScope.Data, "collections.json"));

    [Fact]
    public void An_Instance_Name_Is_Trimmed_Before_Use()
        => Assert.EndsWith("staging", Resolver(instance: "  staging  ").Root(BowireStorageScope.Data),
            StringComparison.Ordinal);

    [Theory]
    [InlineData("team/staging")]
    [InlineData(@"team\staging")]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    public void An_Instance_That_Is_Not_A_Single_Segment_Is_Refused(string instance)
    {
        // Silent otherwise: it would write outside the root and look fine.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Resolver(instance: instance).Root(BowireStorageScope.Data));
        Assert.Contains(BowirePathResolver.InstanceVariable, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plugins")]
    [InlineData("workspaces")]
    [InlineData("Recordings")]   // the check is case-insensitive: on Windows
    public void An_Instance_Named_After_A_Directory_The_Root_Owns_Is_Refused(string instance)
    {
        // With no instance set the root IS the scope, so this instance would
        // quietly share state with an unnamed one — the exact opposite of
        // what setting it was for.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Resolver(instance: instance).Root(BowireStorageScope.Data));

        Assert.Contains("share state with an unnamed one", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Reserved:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateInstance_Accepts_An_Ordinary_Name()
    {
        BowirePathResolver.ValidateInstance("staging");
        BowirePathResolver.ValidateInstance("team-b");
        BowirePathResolver.ValidateInstance("instance_2");
    }

    [Fact]
    public void ValidateInstance_Rejects_Nothing_At_All()
    {
        Assert.Throws<ArgumentNullException>(() => BowirePathResolver.ValidateInstance(null!));
        Assert.Throws<ArgumentException>(() => BowirePathResolver.ValidateInstance("   "));
    }

    // ---- construction ----

    [Fact]
    public void The_Parameterless_Resolver_Works_Without_A_Container()
    {
        // Several call sites resolve paths from static initialisers that run
        // before any host is built. A resolver they cannot reach would just
        // mean they keep their own copy of this logic.
        var resolver = new BowirePathResolver();

        Assert.False(string.IsNullOrWhiteSpace(resolver.Root(BowireStorageScope.Data)));
        Assert.False(string.IsNullOrWhiteSpace(resolver.Root(BowireStorageScope.Machine)));
    }

    [Fact]
    public void The_Resolver_Needs_Both_Of_Its_Inputs()
    {
        Assert.Throws<ArgumentNullException>(() => new BowirePathResolver(null!, () => UserRoot));
        Assert.Throws<ArgumentNullException>(() => new BowirePathResolver(_ => null, null!));
    }
}

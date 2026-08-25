// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Endpoints;

namespace Kuestenlogik.Bowire.Tests.Endpoints;

/// <summary>
/// The guard on <c>?workspaceId=</c> and <c>?storageRoot=</c> — the pair that
/// reaches a file path from a query string (CodeQL <c>cs/path-injection</c>).
/// </summary>
/// <remarks>
/// <para>
/// Six endpoint files read this pair. Before this guard none of them checked
/// it, so a request could name any directory on the machine and have Bowire
/// read or write there — with the response reporting success.
/// </para>
/// <para>
/// "It only listens on loopback" bounds who can reach it, not what a request
/// may ask for once it does: any page in any tab can POST to localhost, and
/// the workbench itself renders content from the APIs it is pointed at.
/// </para>
/// </remarks>
public sealed class WorkspaceScopeQueryTests
{
    // ---- workspace id ----

    [Theory]
    [InlineData("ws-3f2a9c")]
    [InlineData("Personal")]
    [InlineData("team_b.2")]
    [InlineData("a")]
    public void An_Ordinary_Workspace_Id_Is_Accepted(string id)
    {
        var scope = WorkspaceScopeQuery.Validate(id, null);

        Assert.False(scope.IsInvalid);
        Assert.Equal(id, scope.WorkspaceId);
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("C:/Windows")]
    [InlineData("ws\u0000null")]
    [InlineData("ws%2f..%2f")]      // percent-encoding is not a way in either
    public void A_Workspace_Id_That_Is_Not_One_Segment_Is_Refused(string id)
    {
        // It becomes a path segment under "workspaces/", so anything that can
        // traverse or re-root has to be refused before it gets there.
        var scope = WorkspaceScopeQuery.Validate(id, null);

        Assert.True(scope.IsInvalid);
        Assert.Contains("workspaceId", scope.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Absurdly_Long_Workspace_Id_Is_Refused()
        => Assert.True(WorkspaceScopeQuery.Validate(new string('a', 129), null).IsInvalid);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_Workspace_Id_Means_The_Legacy_Scope_Not_An_Error(string? id)
    {
        // Callers predating per-workspace storage send nothing, and they must
        // keep working against the single-file layout.
        var scope = WorkspaceScopeQuery.Validate(id, null);

        Assert.False(scope.IsInvalid);
        Assert.Null(scope.WorkspaceId);
    }

    [Fact]
    public void A_Workspace_Id_Is_Trimmed()
        => Assert.Equal("ws-1", WorkspaceScopeQuery.Validate("  ws-1  ", null).WorkspaceId);

    // ---- storage root ----

    [Fact]
    public void An_Existing_Absolute_Directory_Is_Accepted()
    {
        var dir = Directory.CreateTempSubdirectory("bowire-scope-");
        try
        {
            var scope = WorkspaceScopeQuery.Validate(null, dir.FullName);

            Assert.False(scope.IsInvalid);
            Assert.Equal(dir.FullName, scope.StorageRoot);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_Relative_Storage_Root_Is_Refused()
    {
        // It would resolve against the server's working directory — somewhere
        // the caller cannot see and did not mean.
        var scope = WorkspaceScopeQuery.Validate(null, "relative/path");

        Assert.True(scope.IsInvalid);
        Assert.Contains("absolute", scope.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Storage_Root_Containing_DotDot_Is_Refused()
    {
        var root = Path.Combine(Path.GetTempPath(), "..", "elsewhere");

        var scope = WorkspaceScopeQuery.Validate(null, root);

        Assert.True(scope.IsInvalid);
        Assert.Contains("..", scope.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Storage_Root_That_Does_Not_Exist_Is_Refused()
    {
        // The difference between writing into a directory the operator chose
        // and writing into one the request invented.
        var missing = Path.Combine(Path.GetTempPath(), "bowire-not-here-" + Guid.NewGuid().ToString("N"));

        var scope = WorkspaceScopeQuery.Validate(null, missing);

        Assert.True(scope.IsInvalid);
        Assert.Contains("existing directory", scope.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_Storage_Root_Means_The_Default_Layout(string? root)
    {
        var scope = WorkspaceScopeQuery.Validate(null, root);

        Assert.False(scope.IsInvalid);
        Assert.Null(scope.StorageRoot);
    }

    // ---- the shape the attack takes ----

    [Fact]
    public void A_System_Directory_Cannot_Be_Named_As_A_Storage_Root_Via_Traversal()
    {
        // The literal request this guard exists for: point the workspace at
        // somewhere outside anything Bowire owns and have the server write
        // collections.json there.
        var traversal = Path.Combine(Path.GetTempPath(), "..", "..", "Windows", "System32");

        Assert.True(WorkspaceScopeQuery.Validate("ws-1", traversal).IsInvalid);
    }

    [Fact]
    public void Both_Values_Are_Validated_Not_Just_The_First()
    {
        // A valid id must not carry an invalid root past the check.
        var scope = WorkspaceScopeQuery.Validate("ws-1", "relative/path");

        Assert.True(scope.IsInvalid);
        Assert.Null(scope.WorkspaceId);
        Assert.Null(scope.StorageRoot);
    }
}

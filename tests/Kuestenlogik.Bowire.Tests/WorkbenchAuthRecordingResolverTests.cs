// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// #563: the production <see cref="WorkbenchAuthRecordingResolver"/> — the
/// concrete <c>IAuthRecordingResolver</c> registered by the standalone tool.
/// Redirects <see cref="BowireUserContext"/> to a per-test temp root so the
/// per-workspace <c>AuthRecordingStore</c> scan runs against fixtures, not the
/// developer's real <c>~/.bowire/</c>.
/// </summary>
[Collection("BowireUserContext")]
public sealed class WorkbenchAuthRecordingResolverTests : IDisposable
{
    private readonly IBowireUserStore _original;
    private readonly string _tempRoot;

    public WorkbenchAuthRecordingResolverTests()
    {
        _original = BowireUserContext.Current;
        _tempRoot = Path.Combine(Path.GetTempPath(), $"bowire-authrec-resolver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        BowireUserContext.Current = new TempUserStore(_tempRoot);
    }

    public void Dispose()
    {
        BowireUserContext.Current = _original;
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private static void Save(string workspaceId, string id, string credential, string? scheme = "bearer") =>
        AuthRecordingStore.Save(workspaceId, null, new AuthRecording { Id = id, Credential = credential, Scheme = scheme });

    [Fact]
    public void Scan_Finds_A_Recording_When_No_Workspace_Given()
    {
        Save("ws-a", "login", "tok-a");

        var resolved = new WorkbenchAuthRecordingResolver().TryResolve("login", workspaceId: null);

        Assert.NotNull(resolved);
        Assert.Equal("tok-a", resolved!.Credential);
        Assert.Equal("bearer", resolved.Scheme);
    }

    [Fact]
    public void Unknown_Id_Resolves_To_Null()
    {
        Save("ws-a", "login", "tok-a");
        Assert.Null(new WorkbenchAuthRecordingResolver().TryResolve("nope", workspaceId: null));
    }

    [Fact]
    public void Empty_Credential_Recording_Is_Skipped()
    {
        // The store rejects a credential-less Save, so write the file directly
        // to simulate a hand-edited / corrupt recording — the resolver must
        // still skip it rather than hand back an empty (presence-only) credential.
        var path = AuthRecordingStore.GetStorePath("ws-a", null, "login");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new AuthRecording { Id = "login", Credential = "" }.ToJson());

        Assert.Null(new WorkbenchAuthRecordingResolver().TryResolve("login", workspaceId: null));
    }

    [Fact]
    public void Scoped_Resolution_Picks_The_Named_Workspace_On_A_Colliding_Id()
    {
        Save("ws-a", "login", "tok-a");
        Save("ws-b", "login", "tok-b");   // same id, different workspace + credential

        var resolver = new WorkbenchAuthRecordingResolver();
        Assert.Equal("tok-b", resolver.TryResolve("login", workspaceId: "ws-b")!.Credential);
        Assert.Equal("tok-a", resolver.TryResolve("login", workspaceId: "ws-a")!.Credential);
    }

    private sealed class TempUserStore(string root) : IBowireUserStore
    {
        public string GetUserPath(string filename) => Path.Combine(root, filename);
    }
}

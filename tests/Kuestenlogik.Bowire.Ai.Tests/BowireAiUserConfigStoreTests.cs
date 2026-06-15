// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Ai.Tests;

/// <summary>
/// Behaviour tests for <see cref="BowireAiUserConfigStore"/>. Covers
/// the round-trip persistence shape, the #116 Phase 3 per-workspace
/// override layer (Save / TryLoad / HasOverride / RemoveOverride),
/// the workspaceId sanitisation guard, the default-fallback semantics
/// (null fields in the on-disk DTO resolve back to
/// <see cref="BowireAiOptions"/> defaults), and the error paths
/// (missing file → null, corrupted file → null without throwing).
/// </summary>
[Collection("BowireUserContext")]
public sealed class BowireAiUserConfigStoreTests : IDisposable
{
    private readonly IBowireUserStore _originalStore;
    private readonly string _tempRoot;

    public BowireAiUserConfigStoreTests()
    {
        _originalStore = BowireUserContext.Current;
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"bowire-ai-store-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        BowireUserContext.Current = new TempUserStore(_tempRoot);
    }

    public void Dispose()
    {
        BowireUserContext.Current = _originalStore;
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TryLoad_NoFile_ReturnsNull()
    {
        // Distinguishes "user hasn't picked anything yet" from "user
        // saved the default values" — important because AddBowireAi's
        // overlay only applies the file when it actually exists.
        Assert.Null(BowireAiUserConfigStore.TryLoad());
    }

    [Fact]
    public void Save_Then_TryLoad_RoundTrips_All_Fields()
    {
        var saved = new BowireAiOptions
        {
            ProviderId = "lmstudio",
            Endpoint = "http://localhost:1234",
            Model = "mistral-7b-instruct",
            AutoDetectLocal = false,
        };

        BowireAiUserConfigStore.Save(saved);
        var loaded = BowireAiUserConfigStore.TryLoad();

        Assert.NotNull(loaded);
        Assert.Equal("lmstudio", loaded!.ProviderId);
        Assert.Equal("http://localhost:1234", loaded.Endpoint);
        Assert.Equal("mistral-7b-instruct", loaded.Model);
        Assert.False(loaded.AutoDetectLocal);
    }

    [Fact]
    public void Save_WritesCamelCase_PrettyPrinted_Json()
    {
        // The persisted DTO uses camelCase keys; pretty-printing is
        // deliberate so a curious user inspecting ~/.bowire/ai-config.json
        // can read + hand-edit it. Pinning the on-disk shape because
        // it's part of the user-facing contract — renaming a field
        // would silently invalidate every existing user's saved pick.
        BowireAiUserConfigStore.Save(new BowireAiOptions
        {
            ProviderId = "ollama",
            Endpoint = "http://localhost:11434",
            Model = "qwen2.5:7b",
            AutoDetectLocal = true,
        });

        var path = Path.Combine(_tempRoot, "ai-config.json");
        var json = File.ReadAllText(path);

        Assert.Contains("\"providerId\":", json, StringComparison.Ordinal);
        Assert.Contains("\"endpoint\":", json, StringComparison.Ordinal);
        Assert.Contains("\"model\":", json, StringComparison.Ordinal);
        Assert.Contains("\"autoDetectLocal\":", json, StringComparison.Ordinal);
        // Pretty-printed: at least one newline between the opening
        // brace and the first field.
        Assert.Contains("\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TryLoad_CorruptedFile_ReturnsNull_WithoutThrowing()
    {
        // A corrupted user-config file must NOT take the workbench down
        // on startup. The store swallows the parse error and the
        // overlay falls back to the IConfiguration layer; the next save
        // rewrites the file with valid JSON.
        var path = Path.Combine(_tempRoot, "ai-config.json");
        File.WriteAllText(path, "{not valid json");

        Assert.Null(BowireAiUserConfigStore.TryLoad());
    }

    [Fact]
    public void TryLoad_PartialFile_FallsBackToDefaults_PerField()
    {
        // The persisted DTO uses nullable fields so an older on-disk
        // file (written before a property was added) still loads.
        // Each unset field falls back to its BowireAiOptions default.
        var path = Path.Combine(_tempRoot, "ai-config.json");
        File.WriteAllText(path, """{"providerId":"ollama"}""");

        var loaded = BowireAiUserConfigStore.TryLoad();

        Assert.NotNull(loaded);
        Assert.Equal("ollama", loaded!.ProviderId);
        Assert.Equal("http://localhost:11434", loaded.Endpoint);
        Assert.Equal("llama3.2:3b", loaded.Model);
        Assert.True(loaded.AutoDetectLocal);
    }

    [Fact]
    public void Save_CreatesParentDirectory_WhenMissing()
    {
        // The store doesn't assume ~/.bowire/ exists — first save on a
        // fresh install needs to mkdir -p the parent. Re-target the
        // resolver at a path one level deeper so we can verify.
        var deepRoot = Path.Combine(_tempRoot, "nested", "deeper");
        BowireUserContext.Current = new TempUserStore(deepRoot);

        BowireAiUserConfigStore.Save(new BowireAiOptions { Model = "x" });

        Assert.True(Directory.Exists(deepRoot));
        Assert.True(File.Exists(Path.Combine(deepRoot, "ai-config.json")));
    }

    // ----- #116 Phase 3 per-workspace overrides --------------------

    [Fact]
    public void Save_WithWorkspaceId_WritesOverrideFile_NotGlobal()
    {
        BowireAiUserConfigStore.Save(
            new BowireAiOptions { Model = "ws-pick:1b" },
            workspaceId: "personal");

        // Per-workspace override sits next to the global file under the
        // documented naming scheme: ai-config.<workspaceId>.json.
        Assert.True(File.Exists(Path.Combine(_tempRoot, "ai-config.personal.json")));
        Assert.False(File.Exists(Path.Combine(_tempRoot, "ai-config.json")));
    }

    [Fact]
    public void TryLoad_WithWorkspaceId_PrefersOverride_OverGlobal()
    {
        BowireAiUserConfigStore.Save(new BowireAiOptions { Model = "global-pick:1b" });
        BowireAiUserConfigStore.Save(
            new BowireAiOptions { Model = "workspace-pick:7b" },
            workspaceId: "personal");

        var loaded = BowireAiUserConfigStore.TryLoad(workspaceId: "personal");

        Assert.NotNull(loaded);
        Assert.Equal("workspace-pick:7b", loaded!.Model);
    }

    [Fact]
    public void TryLoad_WithWorkspaceId_FallsBackToGlobal_WhenOverrideMissing()
    {
        BowireAiUserConfigStore.Save(new BowireAiOptions { Model = "global-pick:1b" });

        // No per-workspace file written — load with a workspaceId
        // should still resolve via the global file.
        var loaded = BowireAiUserConfigStore.TryLoad(workspaceId: "personal");

        Assert.NotNull(loaded);
        Assert.Equal("global-pick:1b", loaded!.Model);
    }

    [Fact]
    public void TryLoad_WithWorkspaceId_ReturnsNull_WhenNeitherFileExists()
    {
        Assert.Null(BowireAiUserConfigStore.TryLoad(workspaceId: "personal"));
    }

    [Fact]
    public void HasOverride_TrueOnlyAfterPerWorkspaceSave()
    {
        Assert.False(BowireAiUserConfigStore.HasOverride("personal"));

        BowireAiUserConfigStore.Save(new BowireAiOptions(), workspaceId: "personal");
        Assert.True(BowireAiUserConfigStore.HasOverride("personal"));

        // The global save shouldn't flip HasOverride for a different
        // workspaceId — each workspace owns its own override flag.
        BowireAiUserConfigStore.Save(new BowireAiOptions());
        Assert.False(BowireAiUserConfigStore.HasOverride("other-ws"));
    }

    [Fact]
    public void HasOverride_NullOrWhitespace_ReturnsFalse()
    {
        // Defensive: the UI may pass an empty workspaceId; we treat
        // that as "no workspace selected" rather than synthesising a
        // global override flag.
        Assert.False(BowireAiUserConfigStore.HasOverride(null));
        Assert.False(BowireAiUserConfigStore.HasOverride(""));
        Assert.False(BowireAiUserConfigStore.HasOverride("   "));
    }

    [Fact]
    public void RemoveOverride_DeletesFile_WhenItExists()
    {
        BowireAiUserConfigStore.Save(new BowireAiOptions(), workspaceId: "personal");
        Assert.True(BowireAiUserConfigStore.HasOverride("personal"));

        BowireAiUserConfigStore.RemoveOverride("personal");

        Assert.False(BowireAiUserConfigStore.HasOverride("personal"));
        Assert.False(File.Exists(Path.Combine(_tempRoot, "ai-config.personal.json")));
    }

    [Fact]
    public void RemoveOverride_NoOp_WhenFileMissing()
    {
        // RemoveOverride is best-effort cleanup; calling it when no
        // override exists shouldn't throw or surface an error to the
        // 200-OK DELETE handler in the endpoint.
        BowireAiUserConfigStore.RemoveOverride("never-existed");
        Assert.False(BowireAiUserConfigStore.HasOverride("never-existed"));
    }

    [Fact]
    public void RemoveOverride_NullOrWhitespace_IsNoOp()
    {
        BowireAiUserConfigStore.RemoveOverride("");
        BowireAiUserConfigStore.RemoveOverride("   ");
        // Nothing on disk to assert against; the contract is just
        // "doesn't throw + doesn't touch the global file".
        Assert.False(File.Exists(Path.Combine(_tempRoot, "ai-config.json")));
    }

    [Fact]
    public void WorkspaceId_PathTraversal_IsSanitisedAway()
    {
        // Defence in depth: a hostile workspaceId from the URL query
        // shouldn't be allowed to escape the per-user directory and
        // touch ../../etc/passwd or similar. The sanitiser strips
        // path separators + dots, so the bogus segments collapse into
        // a benign filename.
        BowireAiUserConfigStore.Save(
            new BowireAiOptions { Model = "evil-pick:1b" },
            workspaceId: "../../etc/passwd");

        // No file with ".." or "/" in its name was written inside our
        // sandbox root. The sanitised name only keeps the allowlisted
        // characters (letters, digits, underscore, hyphen).
        var written = Directory.GetFiles(_tempRoot, "ai-config.*.json");
        Assert.Single(written);
        var name = Path.GetFileName(written[0]);
        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
        Assert.DoesNotContain("/", name, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", name, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceId_AllUnsafeChars_CollapsesToDefault()
    {
        // When every char gets stripped, the sanitiser falls back to
        // the literal "default" segment so we still have a valid
        // file name — verified by both Save and HasOverride agreeing
        // on the resulting file location.
        BowireAiUserConfigStore.Save(
            new BowireAiOptions { Model = "default-collapse:1b" },
            workspaceId: "/././/");

        Assert.True(File.Exists(Path.Combine(_tempRoot, "ai-config.default.json")));
        Assert.True(BowireAiUserConfigStore.HasOverride("/././/"));
    }

    [Fact]
    public void WorkspaceId_KeepsAllowlistedCharacters()
    {
        // ws_personal-1 keeps every char (letter, digit, underscore,
        // hyphen) so the on-disk filename should be the verbatim id.
        BowireAiUserConfigStore.Save(
            new BowireAiOptions { Model = "ws-pick:1b" },
            workspaceId: "ws_personal-1");

        Assert.True(File.Exists(Path.Combine(_tempRoot, "ai-config.ws_personal-1.json")));
    }

    [Fact]
    public void Save_GlobalAfterOverride_DoesNotTouchOverrideFile()
    {
        // Per-workspace overrides survive a global save — they're a
        // separate file, and the global write only addresses ai-config.json.
        BowireAiUserConfigStore.Save(
            new BowireAiOptions { Model = "ws-pick:7b" },
            workspaceId: "personal");
        var beforeBytes = File.ReadAllBytes(Path.Combine(_tempRoot, "ai-config.personal.json"));

        BowireAiUserConfigStore.Save(new BowireAiOptions { Model = "global-pick:1b" });

        var afterBytes = File.ReadAllBytes(Path.Combine(_tempRoot, "ai-config.personal.json"));
        Assert.Equal(beforeBytes, afterBytes);

        // And the freshly-written global file is independent — picking
        // it up by workspace lookup still prefers the override.
        var loaded = BowireAiUserConfigStore.TryLoad(workspaceId: "personal");
        Assert.Equal("ws-pick:7b", loaded!.Model);
    }

    [Fact]
    public void Save_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => BowireAiUserConfigStore.Save(null!));
    }

    [Fact]
    public void PersistedJson_IsValidStructured_Json_Document()
    {
        // The store can't be a behavioural promise on its own — a
        // future round trip must work. We parse what was written and
        // confirm the keys exist with the expected types.
        BowireAiUserConfigStore.Save(new BowireAiOptions
        {
            ProviderId = "ollama",
            Endpoint = "http://localhost:11434",
            Model = "qwen2.5:7b",
            AutoDetectLocal = false,
        });

        var json = File.ReadAllText(Path.Combine(_tempRoot, "ai-config.json"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("ollama", root.GetProperty("providerId").GetString());
        Assert.Equal("http://localhost:11434", root.GetProperty("endpoint").GetString());
        Assert.Equal("qwen2.5:7b", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("autoDetectLocal").GetBoolean());
    }

    private sealed class TempUserStore(string root) : IBowireUserStore
    {
        public string GetUserPath(string filename) => Path.Combine(root, filename);
    }
}

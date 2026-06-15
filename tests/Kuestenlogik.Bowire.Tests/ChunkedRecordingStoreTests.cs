// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for <see cref="ChunkedRecordingStore"/> — the on-disk chunked
/// layout under <c>recordings/&lt;id&gt;/{recording.json, steps/, bodies/}</c>.
/// Each test points the store at a fresh temp directory so the harness
/// isolation from the developer's real <c>~/.bowire/recordings/</c>
/// is rigid; the disposable cleans up afterwards.
/// </summary>
public sealed class ChunkedRecordingStoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _originalOverride;
    private readonly long _originalMaxBytes;

    public ChunkedRecordingStoreTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("bowire-chunked-").FullName;
        // Stash the existing override (null in the normal case) so we
        // don't smear a value across tests that legitimately want the
        // BowireUserContext path. ChunkedRecordingStore.RootPath is the
        // documented seam for this.
        _originalOverride = SafeGetRootOverride();
        _originalMaxBytes = ChunkedRecordingStore.MaxBytesPerRecording;
        ChunkedRecordingStore.RootPath = _tempRoot;
    }

    public void Dispose()
    {
        ChunkedRecordingStore.MaxBytesPerRecording = _originalMaxBytes;
        // Restore whatever override (or null) was in place.
        if (_originalOverride is null)
        {
            // The setter only accepts non-null; null out via reflection
            // — there's no public "clear" API. The override is internal
            // state so we treat it as best-effort.
            try
            {
                typeof(ChunkedRecordingStore)
                    .GetField("_testRootOverride", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?
                    .SetValue(null, null);
            }
            catch
            {
                // Best-effort; the next test sets its own override.
            }
        }
        else
        {
            ChunkedRecordingStore.RootPath = _originalOverride;
        }

        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private static string? SafeGetRootOverride()
    {
        try
        {
            return typeof(ChunkedRecordingStore)
                .GetField("_testRootOverride", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?
                .GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    // ---------- LoadAll ----------

    [Fact]
    public void LoadAll_With_No_Recordings_Returns_Empty_Wrapper()
    {
        var json = ChunkedRecordingStore.LoadAll();

        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.GetProperty("recordings");
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(0, arr.GetArrayLength());
    }

    [Fact]
    public void SaveAll_Then_LoadAll_Round_Trips_Single_Recording_Without_Steps()
    {
        const string Input = """{"recordings":[{"id":"r1","name":"Demo"}]}""";

        ChunkedRecordingStore.SaveAll(Input);
        var loaded = ChunkedRecordingStore.LoadAll();

        using var doc = JsonDocument.Parse(loaded);
        var arr = doc.RootElement.GetProperty("recordings");
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("r1", arr[0].GetProperty("id").GetString());
        Assert.Equal("Demo", arr[0].GetProperty("name").GetString());
        // SaveAll always emits a stepCount and sizeBytes even when no
        // steps were supplied; LoadAll re-projects them as 0.
        Assert.Equal(0, arr[0].GetProperty("stepCount").GetInt32());
        Assert.Equal(0, arr[0].GetProperty("sizeBytes").GetInt32());
    }

    [Fact]
    public void SaveAll_Splits_Steps_Into_Numbered_Files_And_LoadAll_Reassembles_Them()
    {
        var input = """
            {
              "recordings": [
                {
                  "id": "r1",
                  "name": "two-step",
                  "steps": [
                    { "id": "a", "service": "users", "method": "list", "status": "200", "response": "alpha" },
                    { "id": "b", "service": "users", "method": "get",  "status": "404", "response": "beta" }
                  ]
                }
              ]
            }
            """;

        ChunkedRecordingStore.SaveAll(input);

        // On-disk layout: numbered step files under steps/.
        var stepsDir = SafePath.Combine(_tempRoot, "r1", "steps");
        Assert.True(Directory.Exists(stepsDir));
        Assert.True(File.Exists(SafePath.Combine(stepsDir, "0000.json")));
        Assert.True(File.Exists(SafePath.Combine(stepsDir, "0001.json")));

        // The metadata file carries the manifest, not inlined steps.
        var metaPath = SafePath.Combine(_tempRoot, "r1", "recording.json");
        Assert.True(File.Exists(metaPath));
        var meta = JsonDocument.Parse(File.ReadAllText(metaPath)).RootElement;
        var manifest = meta.GetProperty("stepsManifest");
        Assert.Equal(2, manifest.GetArrayLength());
        Assert.Equal("a", manifest[0].GetProperty("id").GetString());
        Assert.Equal("0000.json", manifest[0].GetProperty("file").GetString());
        Assert.Equal(2, meta.GetProperty("stepCount").GetInt32());
        // Inline-small bodies are counted in sizeBytes — "alpha" (5) + "beta" (4).
        Assert.Equal(9, meta.GetProperty("sizeBytes").GetInt32());

        // LoadAll should reassemble back to the legacy {recordings:[{steps:[…]}]} shape.
        var loaded = ChunkedRecordingStore.LoadAll();
        using var doc = JsonDocument.Parse(loaded);
        var rec0 = doc.RootElement.GetProperty("recordings")[0];
        Assert.False(rec0.TryGetProperty("stepsManifest", out _),
            "stepsManifest should be projected away during LoadAll");
        var steps = rec0.GetProperty("steps");
        Assert.Equal(2, steps.GetArrayLength());
        Assert.Equal("alpha", steps[0].GetProperty("response").GetString());
        Assert.Equal("beta", steps[1].GetProperty("response").GetString());
    }

    [Fact]
    public void SaveAll_Content_Addresses_Bodies_Above_One_Megabyte_Threshold()
    {
        // The threshold is 1 MB (1024*1024). Use just over that so the
        // string is long enough to be hashed into bodies/ but short
        // enough to keep the test fast.
        var bigBody = new string('x', (1024 * 1024) + 64);
        var input = $$"""
            {
              "recordings": [
                {
                  "id": "big",
                  "name": "huge",
                  "steps": [
                    { "id": "a", "response": "{{bigBody}}" }
                  ]
                }
              ]
            }
            """;

        ChunkedRecordingStore.SaveAll(input);

        // bodies/<sha256-uppercase> should hold the payload.
        var bodiesDir = SafePath.Combine(_tempRoot, "big", "bodies");
        Assert.True(Directory.Exists(bodiesDir));
        var bodyFiles = Directory.GetFiles(bodiesDir);
        Assert.Single(bodyFiles);

        var expectedHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(bigBody)));
        Assert.Equal(expectedHash, Path.GetFileName(bodyFiles[0]));
        Assert.Equal(bigBody, File.ReadAllText(bodyFiles[0]));

        // The step file references the body by hash, not inline.
        var stepText = File.ReadAllText(SafePath.Combine(_tempRoot, Path.Combine("big", "steps", "0000.json")));
        var stepObj = JsonNode.Parse(stepText)!.AsObject();
        Assert.Equal(expectedHash, (string?)stepObj["responseRef"]);
        Assert.False(stepObj.ContainsKey("response"));

        // LoadAll resolves the responseRef back into an inline response.
        var loaded = ChunkedRecordingStore.LoadAll();
        using var doc = JsonDocument.Parse(loaded);
        var step = doc.RootElement.GetProperty("recordings")[0].GetProperty("steps")[0];
        Assert.Equal(bigBody, step.GetProperty("response").GetString());
        Assert.False(step.TryGetProperty("responseRef", out _));
    }

    [Fact]
    public void SaveAll_Deduplicates_Identical_Large_Bodies_Across_Steps()
    {
        var bigBody = new string('y', (1024 * 1024) + 8);
        var input = $$"""
            {
              "recordings": [
                {
                  "id": "dup",
                  "steps": [
                    { "id": "a", "response": "{{bigBody}}" },
                    { "id": "b", "response": "{{bigBody}}" }
                  ]
                }
              ]
            }
            """;

        ChunkedRecordingStore.SaveAll(input);

        var bodiesDir = SafePath.Combine(_tempRoot, "dup", "bodies");
        Assert.Single(Directory.GetFiles(bodiesDir));
    }

    [Fact]
    public void SaveAll_Sweeps_Recordings_Missing_From_New_Document()
    {
        // Seed two recordings, then re-save with only one — the dropped
        // one's directory should be removed (operator-delete propagation).
        ChunkedRecordingStore.SaveAll("""{"recordings":[{"id":"keep","name":"K"},{"id":"drop","name":"D"}]}""");
        Assert.True(Directory.Exists(SafePath.Combine(_tempRoot, "keep")));
        Assert.True(Directory.Exists(SafePath.Combine(_tempRoot, "drop")));

        ChunkedRecordingStore.SaveAll("""{"recordings":[{"id":"keep","name":"K"}]}""");

        Assert.True(Directory.Exists(SafePath.Combine(_tempRoot, "keep")));
        Assert.False(Directory.Exists(SafePath.Combine(_tempRoot, "drop")));
    }

    [Fact]
    public void SaveAll_Throws_When_Top_Level_Recordings_Missing_Or_Wrong_Shape()
    {
        Assert.Throws<JsonException>(
            () => ChunkedRecordingStore.SaveAll("""{"other":[]}"""));
        Assert.Throws<JsonException>(
            () => ChunkedRecordingStore.SaveAll("""{"recordings":"oops"}"""));
    }

    [Fact]
    public void SaveAll_Skips_Recordings_Without_String_Id_But_Keeps_Others()
    {
        var input = """
            {"recordings":[
                {"name":"no-id"},
                {"id":"good","name":"Good"}
            ]}
            """;

        ChunkedRecordingStore.SaveAll(input);

        // Only the recording with an id gets a directory.
        var dirs = Directory.GetDirectories(_tempRoot);
        Assert.Single(dirs);
        Assert.Equal("good", Path.GetFileName(dirs[0]));
    }

    [Fact]
    public void SaveAll_Sanitises_Path_Traversal_Attempts_In_Recording_Id()
    {
        // SanitiseId strips '/', '\', '..' — a malicious id like
        // "../escape" reduces to "escape", which lands as a child of
        // the temp root. The directory should NOT appear above _tempRoot.
        ChunkedRecordingStore.SaveAll("""{"recordings":[{"id":"../escape","name":"Bad"}]}""");

        var parent = Directory.GetParent(_tempRoot)!.FullName;
        Assert.False(Directory.Exists(SafePath.Combine(parent, "escape")),
            "Sanitiser must not let an id traverse out of the recordings root");
        Assert.True(Directory.Exists(SafePath.Combine(_tempRoot, "escape")));
    }

    [Fact]
    public void SaveAll_Sanitises_All_Punctuation_To_Anon_Folder()
    {
        // An id of "///" has all chars filtered → empty → SanitiseId
        // substitutes "anon" so the recording still lands on disk.
        ChunkedRecordingStore.SaveAll("""{"recordings":[{"id":"///","name":"Bad"}]}""");

        var dirs = Directory.GetDirectories(_tempRoot);
        var dir = Assert.Single(dirs);
        Assert.Equal("anon", Path.GetFileName(dir));
    }

    // ---------- AppendStep ----------

    [Fact]
    public void AppendStep_Creates_Metadata_And_Returns_Sequential_Indices()
    {
        var seed = new JsonObject
        {
            ["name"] = "live-capture",
            ["startedAt"] = "2026-01-01T00:00:00Z",
        };
        var step0 = new JsonObject
        {
            ["id"] = "s0",
            ["service"] = "svc",
            ["method"] = "do",
            ["status"] = "200",
            ["response"] = "first",
        };
        var step1 = new JsonObject
        {
            ["id"] = "s1",
            ["service"] = "svc",
            ["method"] = "do",
            ["status"] = "201",
            ["response"] = "second",
        };

        var idx0 = ChunkedRecordingStore.AppendStep("live", step0, seed);
        var idx1 = ChunkedRecordingStore.AppendStep("live", step1, recordingMetadata: null);

        Assert.Equal(0, idx0);
        Assert.Equal(1, idx1);

        // Step files were written under steps/ with the manifest naming.
        var stepsDir = SafePath.Combine(_tempRoot, "live", "steps");
        Assert.True(File.Exists(SafePath.Combine(stepsDir, "0000.json")));
        Assert.True(File.Exists(SafePath.Combine(stepsDir, "0001.json")));

        // Metadata seed survived; id was forced to the sanitised value.
        var metaPath = SafePath.Combine(_tempRoot, "live", "recording.json");
        var meta = JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
        Assert.Equal("live", (string?)meta["id"]);
        Assert.Equal("live-capture", (string?)meta["name"]);
        Assert.Equal("2026-01-01T00:00:00Z", (string?)meta["startedAt"]);
        Assert.Equal(2, (int?)meta["stepCount"]);
        // sizeBytes is "first".Length + "second".Length = 5 + 6 = 11.
        Assert.Equal(11L, (long?)meta["sizeBytes"]);

        // Manifest tracks the step file by name.
        var manifest = meta["stepsManifest"]!.AsArray();
        Assert.Equal(2, manifest.Count);
        Assert.Equal("0000.json", (string?)manifest[0]!["file"]);
        Assert.Equal("0001.json", (string?)manifest[1]!["file"]);
    }

    [Fact]
    public void AppendStep_Content_Addresses_Large_Bodies()
    {
        var bigBody = new string('z', (1024 * 1024) + 32);
        var step = new JsonObject
        {
            ["id"] = "big",
            ["response"] = bigBody,
        };

        ChunkedRecordingStore.AppendStep("rec", step, recordingMetadata: null);

        var bodiesDir = SafePath.Combine(_tempRoot, "rec", "bodies");
        Assert.True(Directory.Exists(bodiesDir));
        Assert.Single(Directory.GetFiles(bodiesDir));

        // Step file references the hash, not inlines the body.
        var stepObj = JsonNode.Parse(File.ReadAllText(
            SafePath.Combine(_tempRoot, Path.Combine("rec", "steps", "0000.json"))))!.AsObject();
        Assert.NotNull(stepObj["responseRef"]);
        Assert.Null(stepObj["response"]);
    }

    [Fact]
    public void AppendStep_Rejects_Empty_RecordingId()
    {
        Assert.Throws<ArgumentException>(
            () => ChunkedRecordingStore.AppendStep("", new JsonObject(), null));
    }

    [Fact]
    public void AppendStep_Rejects_Null_Step()
    {
        Assert.Throws<ArgumentNullException>(
            () => ChunkedRecordingStore.AppendStep("rec", null!, null));
    }

    [Fact]
    public void AppendStep_Falls_Back_To_Anon_Folder_When_Id_Is_All_Punctuation()
    {
        // "///" sanitises to "" → SanitiseId substitutes "anon" so the
        // append still lands somewhere on disk rather than throwing.
        // Pin the fallback so callers can rely on it.
        var idx = ChunkedRecordingStore.AppendStep(
            "///", new JsonObject { ["response"] = "hi" }, null);

        Assert.Equal(0, idx);
        Assert.True(Directory.Exists(SafePath.Combine(_tempRoot, "anon")));
    }

    [Fact]
    public void AppendStep_Throws_When_Size_Cap_Would_Be_Exceeded()
    {
        ChunkedRecordingStore.MaxBytesPerRecording = 10;

        // 8 bytes — under the cap; succeeds.
        ChunkedRecordingStore.AppendStep("rec",
            new JsonObject { ["response"] = "12345678" }, null);

        // 5 more bytes would push total to 13 > 10 → reject.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ChunkedRecordingStore.AppendStep("rec",
                new JsonObject { ["response"] = "abcde" }, null));
        Assert.Contains("exceed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- LoadManifest ----------

    [Fact]
    public void LoadManifest_Returns_Raw_Metadata_Without_Step_Bodies()
    {
        ChunkedRecordingStore.SaveAll("""
            {"recordings":[
              {"id":"m1","name":"M","steps":[{"id":"s0","response":"hello"}]}
            ]}
            """);

        var raw = ChunkedRecordingStore.LoadManifest("m1");

        Assert.NotNull(raw);
        var node = JsonNode.Parse(raw!)!.AsObject();
        Assert.Equal("M", (string?)node["name"]);
        // stepsManifest is the on-disk shape — never reassembled steps[].
        Assert.IsType<JsonArray>(node["stepsManifest"]);
        Assert.Null(node["steps"]);
        Assert.Equal(1, (int?)node["stepCount"]);
    }

    [Fact]
    public void LoadManifest_Missing_Recording_Returns_Null()
    {
        Assert.Null(ChunkedRecordingStore.LoadManifest("never-saved"));
    }

    [Fact]
    public void LoadManifest_Returns_Null_For_Id_That_Has_Never_Been_Saved()
    {
        // "..." sanitises to "anon"; the recording doesn't exist on
        // disk so the load returns null rather than throwing.
        Assert.Null(ChunkedRecordingStore.LoadManifest("..."));
    }

    [Fact]
    public void LoadManifest_Rejects_Empty_String_Id()
    {
        Assert.Throws<ArgumentException>(
            () => ChunkedRecordingStore.LoadManifest(""));
    }

    // ---------- LoadStep ----------

    [Fact]
    public void LoadStep_Returns_Inline_Step_When_Body_Is_Small()
    {
        ChunkedRecordingStore.SaveAll("""
            {"recordings":[
              {"id":"s1","steps":[
                {"id":"a","response":"first"},
                {"id":"b","response":"second"}
              ]}
            ]}
            """);

        var step1 = ChunkedRecordingStore.LoadStep("s1", 1);

        Assert.NotNull(step1);
        var obj = JsonNode.Parse(step1!)!.AsObject();
        Assert.Equal("b", (string?)obj["id"]);
        Assert.Equal("second", (string?)obj["response"]);
    }

    [Fact]
    public void LoadStep_Resolves_ResponseRef_Into_Inline_Response()
    {
        var bigBody = new string('q', (1024 * 1024) + 16);
        var input = $$"""
            {"recordings":[
              {"id":"big","steps":[
                {"id":"a","response":"{{bigBody}}"}
              ]}
            ]}
            """;
        ChunkedRecordingStore.SaveAll(input);

        var stepJson = ChunkedRecordingStore.LoadStep("big", 0);

        Assert.NotNull(stepJson);
        var obj = JsonNode.Parse(stepJson!)!.AsObject();
        Assert.Equal(bigBody, (string?)obj["response"]);
        Assert.Null(obj["responseRef"]);
    }

    [Fact]
    public void LoadStep_Missing_Or_Negative_Index_Returns_Null()
    {
        ChunkedRecordingStore.SaveAll("""
            {"recordings":[{"id":"r","steps":[{"id":"s0","response":"x"}]}]}
            """);

        Assert.Null(ChunkedRecordingStore.LoadStep("r", -1));
        Assert.Null(ChunkedRecordingStore.LoadStep("r", 99));
        Assert.Null(ChunkedRecordingStore.LoadStep("never-saved", 0));
    }

    [Fact]
    public void LoadStep_Rejects_Empty_RecordingId()
    {
        Assert.Throws<ArgumentException>(
            () => ChunkedRecordingStore.LoadStep("", 0));
    }

    [Fact]
    public void LoadStep_Returns_Null_For_Id_That_Has_Never_Been_Saved()
    {
        Assert.Null(ChunkedRecordingStore.LoadStep("...", 0));
    }

    // ---------- DeleteAll ----------

    [Fact]
    public void DeleteAll_Wipes_Every_Recording_Directory()
    {
        ChunkedRecordingStore.SaveAll("""
            {"recordings":[{"id":"a"},{"id":"b"},{"id":"c"}]}
            """);
        Assert.Equal(3, Directory.GetDirectories(_tempRoot).Length);

        ChunkedRecordingStore.DeleteAll();

        Assert.Empty(Directory.GetDirectories(_tempRoot));
        // LoadAll on the now-empty store returns the empty wrapper.
        using var doc = JsonDocument.Parse(ChunkedRecordingStore.LoadAll());
        Assert.Equal(0, doc.RootElement.GetProperty("recordings").GetArrayLength());
    }

    [Fact]
    public void DeleteAll_Missing_Root_Is_NoOp()
    {
        // Point at a path that doesn't exist yet — the cap-call must
        // not throw, just early-return.
        var ghost = SafePath.Combine(_tempRoot, "does-not-exist");
        ChunkedRecordingStore.RootPath = ghost;
        ChunkedRecordingStore.DeleteAll();
        Assert.False(Directory.Exists(ghost));
    }

    // ---------- Workspace scoping ----------

    [Fact]
    public void Workspace_Scoped_Recordings_Land_Under_Workspaces_Subtree()
    {
        ChunkedRecordingStore.SaveAll(
            """{"recordings":[{"id":"r1","name":"ws-bound"}]}""",
            workspaceId: "alpha");

        var wsDir = SafePath.Combine(_tempRoot, Path.Combine("workspaces", "alpha", "recordings", "r1"));
        Assert.True(Directory.Exists(wsDir));
        Assert.True(File.Exists(SafePath.Combine(wsDir, "recording.json")));

        // The legacy un-scoped root stays empty — workspaces are
        // isolated on disk.
        Assert.False(Directory.Exists(SafePath.Combine(_tempRoot, "r1")));
    }

    [Fact]
    public void Workspace_Scoped_LoadAll_Only_Returns_Matching_Workspace_Recordings()
    {
        ChunkedRecordingStore.SaveAll(
            """{"recordings":[{"id":"r1","name":"A"}]}""",
            workspaceId: "alpha");
        ChunkedRecordingStore.SaveAll(
            """{"recordings":[{"id":"r2","name":"B"}]}""",
            workspaceId: "beta");

        var alpha = ChunkedRecordingStore.LoadAll(workspaceId: "alpha");
        var beta = ChunkedRecordingStore.LoadAll(workspaceId: "beta");

        using var alphaDoc = JsonDocument.Parse(alpha);
        using var betaDoc = JsonDocument.Parse(beta);

        var alphaRecs = alphaDoc.RootElement.GetProperty("recordings");
        var betaRecs = betaDoc.RootElement.GetProperty("recordings");
        Assert.Equal(1, alphaRecs.GetArrayLength());
        Assert.Equal(1, betaRecs.GetArrayLength());
        Assert.Equal("r1", alphaRecs[0].GetProperty("id").GetString());
        Assert.Equal("r2", betaRecs[0].GetProperty("id").GetString());
    }

    [Fact]
    public void Workspace_Empty_Or_Whitespace_Falls_Back_To_Root_Path()
    {
        ChunkedRecordingStore.SaveAll(
            """{"recordings":[{"id":"r1","name":"root"}]}""",
            workspaceId: "   ");

        // Whitespace-only workspace id → root path; the recording lives
        // directly under the test root, not under workspaces/.
        Assert.True(Directory.Exists(SafePath.Combine(_tempRoot, "r1")));
        Assert.False(Directory.Exists(SafePath.Combine(_tempRoot, "workspaces")));
    }

    [Fact]
    public void Workspace_AppendStep_Plus_LoadManifest_Plus_LoadStep_Share_Workspace_Scope()
    {
        ChunkedRecordingStore.AppendStep(
            recordingId: "rec",
            step: new JsonObject { ["id"] = "s0", ["response"] = "wsbody" },
            recordingMetadata: new JsonObject { ["name"] = "ws-cap" },
            workspaceId: "team");

        var manifest = ChunkedRecordingStore.LoadManifest("rec", workspaceId: "team");
        Assert.NotNull(manifest);
        var meta = JsonNode.Parse(manifest!)!.AsObject();
        Assert.Equal("ws-cap", (string?)meta["name"]);

        var step = ChunkedRecordingStore.LoadStep("rec", 0, workspaceId: "team");
        Assert.NotNull(step);
        var stepObj = JsonNode.Parse(step!)!.AsObject();
        Assert.Equal("wsbody", (string?)stepObj["response"]);

        // The same recording is invisible from the un-scoped path.
        Assert.Null(ChunkedRecordingStore.LoadManifest("rec"));

        // DeleteAll respects the workspace too.
        ChunkedRecordingStore.DeleteAll(workspaceId: "team");
        Assert.Null(ChunkedRecordingStore.LoadManifest("rec", workspaceId: "team"));
    }

    // ---------- Robustness ----------

    [Fact]
    public void LoadAll_Skips_Recording_Directories_With_Missing_Metadata_File()
    {
        // Drop an empty directory directly into the root — it has no
        // recording.json. LoadAll should silently skip it rather than
        // surfacing a fake entry.
        Directory.CreateDirectory(SafePath.Combine(_tempRoot, "orphan"));
        ChunkedRecordingStore.SaveAll("""{"recordings":[{"id":"good","name":"G"}]}""");

        using var doc = JsonDocument.Parse(ChunkedRecordingStore.LoadAll());
        var arr = doc.RootElement.GetProperty("recordings");
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("good", arr[0].GetProperty("id").GetString());
    }

    [Fact]
    public void LoadAll_Skips_Corrupt_Metadata_File()
    {
        Directory.CreateDirectory(SafePath.Combine(_tempRoot, "corrupt"));
        File.WriteAllText(SafePath.Combine(_tempRoot, "corrupt", "recording.json"),
            "{ not valid json");
        ChunkedRecordingStore.SaveAll("""{"recordings":[{"id":"good","name":"G"}]}""");

        using var doc = JsonDocument.Parse(ChunkedRecordingStore.LoadAll());
        var arr = doc.RootElement.GetProperty("recordings");
        // Only the valid entry survives.
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("good", arr[0].GetProperty("id").GetString());
    }

    [Fact]
    public void LoadAll_ManifestOnly_Skips_Step_Body_Assembly()
    {
        ChunkedRecordingStore.SaveAll("""
            {"recordings":[
              {"id":"m","name":"M","steps":[
                {"id":"s0","response":"hi"},
                {"id":"s1","response":"there"}
              ]}
            ]}
            """);

        var manifestOnly = ChunkedRecordingStore.LoadAll(manifestOnly: true);

        using var doc = JsonDocument.Parse(manifestOnly);
        var rec = doc.RootElement.GetProperty("recordings")[0];
        // ManifestOnly returns the raw on-disk metadata: stepsManifest
        // is preserved, the reassembled steps[] is not.
        Assert.True(rec.TryGetProperty("stepsManifest", out var manifest));
        Assert.Equal(2, manifest.GetArrayLength());
        Assert.False(rec.TryGetProperty("steps", out _));
    }

    [Fact]
    public void SaveAll_Throws_When_Steps_Push_Total_Past_Size_Cap()
    {
        ChunkedRecordingStore.MaxBytesPerRecording = 16;

        // Two 10-byte responses = 20 bytes > 16-byte cap.
        var input = """
            {"recordings":[
              {"id":"r","steps":[
                {"id":"a","response":"0123456789"},
                {"id":"b","response":"0123456789"}
              ]}
            ]}
            """;

        Assert.Throws<InvalidOperationException>(
            () => ChunkedRecordingStore.SaveAll(input));
    }

    [Fact]
    public void SaveAll_Skips_NonObject_Steps_Without_Throwing()
    {
        // A null entry in steps[] is structurally illegal but the
        // store should harden against it by skipping rather than
        // bombing the entire save.
        var input = """
            {"recordings":[
              {"id":"r","steps":[
                {"id":"a","response":"good"},
                null,
                {"id":"c","response":"also-good"}
              ]}
            ]}
            """;

        ChunkedRecordingStore.SaveAll(input);

        var stepsDir = SafePath.Combine(_tempRoot, "r", "steps");
        // Only the two object entries become files (indices 0 and 2 —
        // null entry is skipped but the counter advances so the
        // surviving entries land at 0000 + 0002).
        var files = Directory.GetFiles(stepsDir);
        Assert.Equal(2, files.Length);
    }
}

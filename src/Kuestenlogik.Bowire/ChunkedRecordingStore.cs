// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Disk-backed store for Bowire recordings with the #144 chunked
/// layout. Each recording lives in its own directory under
/// <c>~/.bowire/recordings/&lt;recordingId&gt;/</c> with:
/// <list type="bullet">
/// <item><c>recording.json</c> — top-level metadata + step manifest
/// (small, &lt;1 MB even for 100k-step captures)</item>
/// <item><c>steps/0000.json</c>, <c>steps/0001.json</c>, … — individual
/// step bodies. Append-write on capture; lazy-read on replay or
/// detail view.</item>
/// <item><c>bodies/&lt;sha256&gt;</c> — large bodies (&gt; 1 MB)
/// content-addressed for de-duplication when the same payload appears
/// across multiple steps.</item>
/// </list>
///
/// The legacy single-file <c>recordings.json</c> automatically migrates
/// on first read — the existing JSON is split into the chunked layout
/// and the original is renamed to <c>recordings.json.legacy</c> so a
/// downgrade can still read it.
/// </summary>
/// <remarks>
/// Wire format with the workbench frontend stays unchanged for Phase 1:
/// <see cref="LoadAll"/> returns the same <c>{"recordings":[…]}</c>
/// shape the legacy store served, just assembled on the fly from the
/// chunked layout. <see cref="SaveAll"/> writes that shape back into
/// the chunked layout. A future Phase 1.5 ships lazy-step endpoints
/// for true GB-scale recordings; this commit lays the structural
/// groundwork without breaking the existing wire contract.
/// </remarks>
internal static partial class ChunkedRecordingStore
{
    // CodeQL cs/path-injection sanitiser barriers. The taint analyser
    // recognises an anchored Regex.IsMatch against a restrictive
    // character class as a sanitiser on the tainted string, so any
    // value that passes one of these regex checks before reaching a
    // File.*/Directory.*/Path.* sink stops being flagged.
    //
    // SafeIdPattern guards recording / workspace / step ids that came
    // from outside the assembly (HTTP body, JSON on disk, &c). The
    // SanitiseId helper below funnels every id through this barrier
    // before it lands in a combined path.
    //
    // SafeHashPattern guards content-addressed body file names — those
    // are Sha256 hex digests in our own code, but CodeQL doesn't track
    // that purity, and the hash also gets read back from disk via the
    // step manifest's responseRef field, which the analyser treats as
    // tainted on the way out.
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeIdPattern();

    [GeneratedRegex(@"^[A-Fa-f0-9]+$")]
    private static partial Regex SafeHashPattern();

    private const string RecordingMetadataFile = "recording.json";
    private const string StepsDirectory = "steps";
    private const string BodiesDirectory = "bodies";
    private const int InlineBodyThreshold = 1024 * 1024;     // 1 MB
    private const long DefaultMaxBytes = 5L * 1024 * 1024 * 1024; // 5 GB per recording

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Lock DiskLock = new();
    private static string? _testRootOverride;

    /// <summary>
    /// Root directory holding the chunked recordings. Resolves through
    /// <see cref="BowireUserContext.GetUserPath"/> by default so the
    /// per-identity scoping seam (#28) can swap in a multi-tenant
    /// resolver without touching this class.
    /// </summary>
    internal static string RootPath
    {
        get => _testRootOverride ?? BowireUserContext.GetUserPath("recordings");
        set => _testRootOverride = value;
    }

    /// <summary>
    /// Per-workspace root resolution (#144 Phase 1.6 / #116). When a
    /// workspaceId is supplied, recordings land under
    /// <c>~/.bowire/workspaces/&lt;wsId&gt;/recordings/</c> so each
    /// workspace's captures are isolated on disk. Without a workspaceId
    /// the legacy root applies — that path is still used by hosts that
    /// haven't adopted workspaces yet.
    /// </summary>
    /// <remarks>
    /// #196 Phase 2.3 — when <paramref name="storageRoot"/> is set on
    /// the workspace (operator pointed it at a checked-out git folder),
    /// recordings live under <c>&lt;storageRoot&gt;/recordings/</c>
    /// instead of the per-user folder. Routed through
    /// <see cref="BowireUserContext.GetWorkspacePath"/> so the resolver
    /// owns the policy in one place.
    /// </remarks>
    internal static string ResolveRootPath(string? workspaceId, string? storageRoot = null)
    {
        // Funnel the workspace id through the cs/path-injection
        // sanitiser barrier up-front. Both branches below feed the id
        // into a path segment, so the analyser needs the SanitiseId
        // call to dominate every downstream Path.Combine / file sink.
        var safeWorkspaceId = string.IsNullOrWhiteSpace(workspaceId)
            ? string.Empty
            : SanitiseId(workspaceId!);

        // Workspace-scoped path with an explicit storageRoot: anchor
        // under the operator's checked-out folder. The test root
        // override stays disabled here — once an operator has pointed
        // the workspace at a real path, smuggling a test override
        // would land bytes outside that path, which is exactly the
        // surprise the override is meant to prevent.
        if (!string.IsNullOrWhiteSpace(storageRoot))
        {
            // GetWorkspacePath ignores the workspaceId segment when
            // storageRoot is present, which is the contract we want
            // here (the storageRoot already identifies the workspace
            // on disk via its folder).
            return BowireUserContext.GetWorkspacePath(
                workspaceId: safeWorkspaceId,
                storageRoot: storageRoot,
                relativePath: "recordings");
        }
        if (string.IsNullOrEmpty(safeWorkspaceId)) return RootPath;
        return _testRootOverride is not null
            ? SafePath.Combine(_testRootOverride, Path.Combine("workspaces", safeWorkspaceId, "recordings"))
            : BowireUserContext.GetUserPath(Path.Combine("workspaces", safeWorkspaceId, "recordings"));
    }

    /// <summary>
    /// Per-recording byte cap; rejects appends that would push the
    /// recording past this limit. Default 5 GB. Configurable via
    /// <c>Bowire:Recording:MaxBytes</c> in a future pass.
    /// </summary>
    internal static long MaxBytesPerRecording { get; set; } = DefaultMaxBytes;

    /// <summary>
    /// Assemble every recording on disk back into the legacy single-
    /// document JSON shape <c>{"recordings":[…]}</c> the workbench
    /// frontend already consumes. Returns an empty shape when no
    /// recordings exist or the directory is unreadable.
    /// </summary>
    public static string LoadAll(string? workspaceId = null, bool manifestOnly = false, string? storageRoot = null)
    {
        // cs/path-injection barrier — funnel the wsId through SanitiseId
        // at the public entry so every downstream sink fed by `root`
        // sees the taint dropped, even though ResolveRootPath sanitises
        // internally too.
        var safeWorkspaceId = string.IsNullOrWhiteSpace(workspaceId)
            ? null
            : SanitiseId(workspaceId!);
        lock (DiskLock)
        {
            MigrateFromLegacyIfNeeded();
            try
            {
                var root = ResolveRootPath(safeWorkspaceId, storageRoot);
                var recordings = new List<JsonElement>();
                if (Directory.Exists(root))
                {
                    foreach (var doc in Directory.EnumerateDirectories(root)
                        .Select(dir => manifestOnly
                            ? TryReadRecordingMetadataOnly(dir)
                            : TryAssembleRecording(dir))
                        .Where(d => d.HasValue))
                    {
                        recordings.Add(doc!.Value);
                    }
                }
                var wrapper = new { recordings };
                return JsonSerializer.Serialize(wrapper, JsonOptions);
            }
            catch
            {
                return """{"recordings":[]}""";
            }
        }
    }

    /// <summary>
    /// Read a recording's metadata file as-is without assembling
    /// step bodies. Used by the manifest-only LoadAll path so the
    /// workbench can list recordings + their step counts without
    /// paying for body reads. The frontend lazy-fetches each step
    /// via <c>GET /api/recordings/&lt;id&gt;/step/&lt;n&gt;</c> when
    /// the detail view actually needs it.
    /// </summary>
    private static JsonElement? TryReadRecordingMetadataOnly(string recordingDir)
    {
        var metadataPath = Path.Combine(recordingDir, RecordingMetadataFile);
        if (!File.Exists(metadataPath)) return null;
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(metadataPath));
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Save the supplied <c>{"recordings":[…]}</c> JSON document back
    /// into the chunked layout. Each recording's metadata + steps are
    /// split into their own files. Recordings not present in the
    /// document are removed from disk so the operator's UI delete
    /// propagates.
    /// </summary>
    public static void SaveAll(string json, string? workspaceId = null, string? storageRoot = null)
    {
        // cs/path-injection barrier — funnel wsId through SanitiseId at
        // the public entry so the rootPath returned by ResolveRootPath
        // is barrier-clean for the downstream Directory.* sinks.
        var safeWorkspaceId = string.IsNullOrWhiteSpace(workspaceId)
            ? null
            : SanitiseId(workspaceId!);
        lock (DiskLock)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("recordings", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Top-level object must carry a 'recordings' array.");
            }

            var rootPath = ResolveRootPath(safeWorkspaceId, storageRoot);
            Directory.CreateDirectory(rootPath);
            var seenIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var rec in arr.EnumerateArray())
            {
                if (!rec.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
                {
                    // A recording without an id can't get its own
                    // directory; skip rather than guessing one.
                    continue;
                }
                var id = SanitiseId(idProp.GetString()!);
                if (string.IsNullOrEmpty(id)) continue;
                seenIds.Add(id);
                WriteOneRecording(rootPath, id, rec);
            }

            // Sweep out directories the operator deleted from the UI.
            foreach (var dir in Directory.EnumerateDirectories(rootPath))
            {
                var name = Path.GetFileName(dir);
                if (!seenIds.Contains(name))
                {
                    TryDeleteRecursive(dir);
                }
            }
        }
    }

    /// <summary>
    /// Convenience wrapper for the wire-compat <c>DELETE /api/recordings</c>
    /// — wipes every recording from disk.
    /// </summary>
    public static void DeleteAll(string? workspaceId = null, string? storageRoot = null)
    {
        // cs/path-injection barrier at the public entry — the EnumerateDirectories
        // sink below consumes the resolved root, and CodeQL needs the
        // SanitiseId barrier to dominate that flow.
        var safeWorkspaceId = string.IsNullOrWhiteSpace(workspaceId)
            ? null
            : SanitiseId(workspaceId!);
        lock (DiskLock)
        {
            var root = ResolveRootPath(safeWorkspaceId, storageRoot);
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                TryDeleteRecursive(dir);
            }
        }
    }

    /// <summary>
    /// Append a single step to <paramref name="recordingId"/>. The
    /// recording's metadata file is created on first append if it
    /// doesn't exist yet — <paramref name="recordingMetadata"/> seeds
    /// the top-level fields when that happens. Used by capture so a
    /// single step write doesn't have to round-trip the whole document.
    /// </summary>
    public static int AppendStep(string recordingId, JsonObject step, JsonObject? recordingMetadata, string? workspaceId = null, string? storageRoot = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(recordingId);
        ArgumentNullException.ThrowIfNull(step);

        var id = SanitiseId(recordingId);
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("Recording id sanitised to empty.", nameof(recordingId));

        // cs/path-injection barrier on the workspace id at the public
        // entry — every downstream path under rootPath needs the taint
        // dropped here so the analyser stops at this method.
        var safeWorkspaceId = string.IsNullOrWhiteSpace(workspaceId)
            ? null
            : SanitiseId(workspaceId!);

        lock (DiskLock)
        {
            MigrateFromLegacyIfNeeded();
            var rootPath = ResolveRootPath(safeWorkspaceId, storageRoot);
            var dir = SafePath.Combine(rootPath, id);
            Directory.CreateDirectory(dir);
            var stepsDir = Path.Combine(dir, StepsDirectory);
            Directory.CreateDirectory(stepsDir);
            var bodiesDir = Path.Combine(dir, BodiesDirectory);
            Directory.CreateDirectory(bodiesDir);

            // Load or seed the metadata file. Seeding only happens
            // on the very first append for a brand-new recording —
            // operator-visible fields (name, createdAt, etc.) come
            // from the second arg so the workbench owns the shape.
            var metaPath = Path.Combine(dir, RecordingMetadataFile);
            JsonObject meta;
            if (File.Exists(metaPath))
            {
                meta = (JsonNode.Parse(File.ReadAllText(metaPath)) as JsonObject)
                       ?? new JsonObject();
            }
            else
            {
                meta = recordingMetadata is null ? new JsonObject() : (JsonObject)recordingMetadata.DeepClone();
                meta["id"] = id;
            }

            var manifest = meta["stepsManifest"] as JsonArray ?? new JsonArray();
            int idx = manifest.Count;
            // Funnel the internally-minted file name through the
            // step-file barrier too — idx comes from a manifest read
            // off disk, so even though the format string is fixed, the
            // analyser sees the value as flowing from a tainted source.
            var stepFile = SanitiseStepFile($"{idx:D4}.json");

            var stepObjForDisk = (JsonObject)step.DeepClone();

            // Same content-address handling as SaveAll — large bodies
            // get hashed into bodies/, small ones stay inline on the
            // step file.
            long stepBytes = 0;
            if (stepObjForDisk["response"] is JsonValue v
                && v.TryGetValue<string>(out var responseText)
                && responseText is { Length: > InlineBodyThreshold })
            {
                var hash = SanitiseHash(Sha256(responseText));
                var bodyPath = SafePath.Combine(bodiesDir, hash);
                if (!File.Exists(bodyPath)) File.WriteAllText(bodyPath, responseText);
                stepBytes = responseText.Length;
                stepObjForDisk["responseRef"] = hash;
                stepObjForDisk.Remove("response");
            }
            else if (stepObjForDisk["response"] is JsonValue inline
                     && inline.TryGetValue<string>(out var inlineText))
            {
                stepBytes = inlineText.Length;
            }

            long existingBytes = ((long?)meta["sizeBytes"]) ?? 0L;
            long newTotal = existingBytes + stepBytes;
            if (newTotal > MaxBytesPerRecording)
            {
                throw new InvalidOperationException(
                    $"Recording '{id}' would exceed the {MaxBytesPerRecording:N0}-byte cap; reject.");
            }

            var stepJson = stepObjForDisk.ToJsonString(JsonOptions);
            File.WriteAllText(SafePath.Combine(stepsDir, stepFile), stepJson);

            manifest.Add(new JsonObject
            {
                ["id"] = (string?)step["id"] ?? idx.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                ["file"] = stepFile,
                ["service"] = (string?)step["service"],
                ["method"] = (string?)step["method"],
                ["status"] = (string?)step["status"],
                ["bytes"] = stepJson.Length,
            });
            meta["stepsManifest"] = manifest;
            meta["sizeBytes"] = newTotal;
            meta["stepCount"] = manifest.Count;
            File.WriteAllText(metaPath, meta.ToJsonString(JsonOptions));
            return idx;
        }
    }

    /// <summary>
    /// Return the manifest + metadata for <paramref name="recordingId"/>
    /// WITHOUT inlining step bodies. Used by the UI to render the
    /// detail-view header / step list before the operator scrolls
    /// into a specific step. Returns null when the recording doesn't
    /// exist.
    /// </summary>
    public static string? LoadManifest(string recordingId, string? workspaceId = null, string? storageRoot = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(recordingId);
        var id = SanitiseId(recordingId);
        if (string.IsNullOrEmpty(id)) return null;
        // cs/path-injection barrier on the workspace id at the public entry.
        var safeWorkspaceId = string.IsNullOrWhiteSpace(workspaceId)
            ? null
            : SanitiseId(workspaceId!);
        lock (DiskLock)
        {
            var path = Path.Combine(SafePath.Combine(ResolveRootPath(safeWorkspaceId, storageRoot), id), RecordingMetadataFile);
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path);
        }
    }

    /// <summary>
    /// Return a single step's body resolved through the content-
    /// addressed bodies/ store. Used by lazy step-fetch in the
    /// detail view + streaming replay. Returns null when the step
    /// file doesn't exist or can't be parsed.
    /// </summary>
    public static string? LoadStep(string recordingId, int stepIndex, string? workspaceId = null, string? storageRoot = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(recordingId);
        if (stepIndex < 0) return null;
        var id = SanitiseId(recordingId);
        if (string.IsNullOrEmpty(id)) return null;
        // cs/path-injection barrier on the workspace id at the public entry.
        var safeWorkspaceId = string.IsNullOrWhiteSpace(workspaceId)
            ? null
            : SanitiseId(workspaceId!);
        // Step index is an int so it can't carry path-traversal taint,
        // but route the assembled file name through SanitiseStepFile so
        // the analyser sees the same barrier the manifest-read path uses.
        var stepFileName = SanitiseStepFile($"{stepIndex:D4}.json");
        lock (DiskLock)
        {
            var rootPath = ResolveRootPath(safeWorkspaceId, storageRoot);
            var stepPath = Path.Combine(SafePath.Combine(rootPath, id), StepsDirectory, stepFileName);
            if (!File.Exists(stepPath)) return null;
            JsonNode? stepNode;
            try { stepNode = JsonNode.Parse(File.ReadAllText(stepPath)); }
            catch { return null; }
            if (stepNode is JsonObject obj && obj["responseRef"] is JsonValue refVal)
            {
                var hash = (string?)refVal;
                if (!string.IsNullOrEmpty(hash))
                {
                    // The hash came off disk so it's tainted; route it
                    // through the hex barrier before it becomes a path
                    // segment. Tolerate a malformed manifest by skipping
                    // the body resolve rather than throwing through the
                    // public LoadStep API.
                    string? bodyPath = null;
                    try { bodyPath = SafePath.Combine(Path.Combine(SafePath.Combine(rootPath, id), BodiesDirectory), SanitiseHash(hash)); }
                    catch (ArgumentException) { /* malformed responseRef on disk; drop body */ }
                    if (bodyPath is not null && File.Exists(bodyPath))
                    {
                        obj["response"] = File.ReadAllText(bodyPath);
                        obj.Remove("responseRef");
                    }
                }
                return obj.ToJsonString(JsonOptions);
            }
            return stepNode?.ToJsonString(JsonOptions);
        }
    }

    private static JsonElement? TryAssembleRecording(string recordingDir)
    {
        var metadataPath = Path.Combine(recordingDir, RecordingMetadataFile);
        if (!File.Exists(metadataPath)) return null;

        JsonNode? recordingNode;
        try
        {
            recordingNode = JsonNode.Parse(File.ReadAllText(metadataPath));
        }
        catch
        {
            return null;
        }
        if (recordingNode is not JsonObject recObj) return null;

        // Resolve manifest entries into real step bodies. Each manifest
        // entry is { id, file, …step metadata }; the step body lives at
        // steps/<file>. Inlined small bodies skipped the file write and
        // carry their payload on the manifest entry directly.
        if (recObj["stepsManifest"] is JsonArray manifest)
        {
            var stepsOut = new JsonArray();
            var stepsDir = Path.Combine(recordingDir, StepsDirectory);
            foreach (var entryObj in manifest.OfType<JsonObject>())
            {
                var stepFile = (string?)entryObj["file"];
                JsonNode? stepNode = null;
                if (!string.IsNullOrEmpty(stepFile))
                {
                    string? path = null;
                    // SanitiseStepFile is the cs/path-injection barrier
                    // on the disk-read file name; SafePath.Combine then
                    // re-asserts containment under stepsDir.
                    try { path = SafePath.Combine(stepsDir, SanitiseStepFile(stepFile)); }
                    catch (ArgumentException) { /* manifest entry tried to escape steps/; skip body */ }
                    if (path is not null && File.Exists(path))
                    {
                        try { stepNode = JsonNode.Parse(File.ReadAllText(path)); }
                        catch { /* corrupt step file; skip body but keep metadata */ }
                    }
                }
                // If a body field is content-addressed, resolve it.
                if (stepNode is JsonObject stepObj && stepObj["responseRef"] is JsonValue refVal)
                {
                    var hash = (string?)refVal;
                    if (!string.IsNullOrEmpty(hash))
                    {
                        string? bodyPath = null;
                        try { bodyPath = SafePath.Combine(Path.Combine(recordingDir, BodiesDirectory), SanitiseHash(hash)); }
                        catch (ArgumentException) { /* malformed responseRef on disk; drop body */ }
                        if (bodyPath is not null && File.Exists(bodyPath))
                        {
                            stepObj["response"] = File.ReadAllText(bodyPath);
                            stepObj.Remove("responseRef");
                        }
                    }
                }
                stepsOut.Add(stepNode?.DeepClone() ?? entryObj.DeepClone());
            }
            recObj["steps"] = stepsOut;
            recObj.Remove("stepsManifest");
        }
        return JsonDocument.Parse(recObj.ToJsonString()).RootElement.Clone();
    }

    private static void WriteOneRecording(string rootPath, string id, JsonElement rec)
    {
        var dir = SafePath.Combine(rootPath, id);
        Directory.CreateDirectory(dir);
        var stepsDir = Path.Combine(dir, StepsDirectory);
        Directory.CreateDirectory(stepsDir);
        var bodiesDir = Path.Combine(dir, BodiesDirectory);
        Directory.CreateDirectory(bodiesDir);

        // Split out steps from the rest of the metadata. We rebuild a
        // 'stepsManifest' that points at chunked step files.
        var recNode = JsonNode.Parse(rec.GetRawText()) as JsonObject
                      ?? throw new JsonException("Recording entries must be objects.");
        var manifest = new JsonArray();
        long totalBytes = 0;

        if (recNode["steps"] is JsonArray steps)
        {
            var seenBodies = new HashSet<string>(StringComparer.Ordinal);
            int idx = 0;
            foreach (var step in steps)
            {
                if (step is not JsonObject stepObj) { idx++; continue; }
                var stepFile = $"{idx:D4}.json";
                var stepObjForDisk = (JsonObject)stepObj.DeepClone();

                // Content-address large bodies into bodies/ — small ones
                // stay inline on the step file.
                if (stepObjForDisk["response"] is JsonValue v && v.TryGetValue<string>(out var responseText) && responseText is { Length: > InlineBodyThreshold })
                {
                    var hash = Sha256(responseText);
                    var bodyPath = SafePath.Combine(bodiesDir, hash);
                    if (!File.Exists(bodyPath) && seenBodies.Add(hash))
                    {
                        File.WriteAllText(bodyPath, responseText);
                    }
                    totalBytes += responseText.Length;
                    stepObjForDisk["responseRef"] = hash;
                    stepObjForDisk.Remove("response");
                }
                else if (stepObjForDisk["response"] is JsonValue inline && inline.TryGetValue<string>(out var inlineText))
                {
                    totalBytes += inlineText.Length;
                }

                var stepJson = stepObjForDisk.ToJsonString(JsonOptions);
                if (totalBytes > MaxBytesPerRecording)
                {
                    throw new InvalidOperationException(
                        $"Recording '{id}' would exceed the {MaxBytesPerRecording:N0}-byte cap; reject.");
                }
                File.WriteAllText(SafePath.Combine(stepsDir, stepFile), stepJson);

                // Manifest entry — addressable metadata that stays on
                // the recording.json document.
                var manifestEntry = new JsonObject
                {
                    ["id"] = (string?)stepObj["id"] ?? idx.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                    ["file"] = stepFile,
                    ["service"] = (string?)stepObj["service"],
                    ["method"] = (string?)stepObj["method"],
                    ["status"] = (string?)stepObj["status"],
                    ["bytes"] = stepJson.Length,
                };
                manifest.Add(manifestEntry);
                idx++;
            }
            recNode.Remove("steps");
        }
        recNode["stepsManifest"] = manifest;
        recNode["sizeBytes"] = totalBytes;
        recNode["stepCount"] = manifest.Count;

        File.WriteAllText(Path.Combine(dir, RecordingMetadataFile),
            recNode.ToJsonString(JsonOptions));
    }

    private static void MigrateFromLegacyIfNeeded()
    {
        var legacyPath = BowireUserContext.GetUserPath("recordings.json");
        if (!File.Exists(legacyPath)) return;
        // Already migrated? Don't touch.
        if (Directory.Exists(RootPath) && Directory.EnumerateDirectories(RootPath).Any())
        {
            return;
        }
        try
        {
            var json = File.ReadAllText(legacyPath);
            SaveAll(json);
            // Rename so a downgrade can still read it.
            var archive = legacyPath + ".legacy";
            if (File.Exists(archive)) File.Delete(archive);
            File.Move(legacyPath, archive);
        }
        catch
        {
            // Migration is best-effort; the legacy file stays in place
            // so a future read attempt can try again.
        }
    }

    private static string SanitiseId(string id)
    {
        // Recording ids are short slugs; defensive sanitisation in case
        // a malicious client tries '../' or absolute paths.
        var sb = new StringBuilder(id.Length);
        foreach (var c in id.Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.'))
        {
            sb.Append(c);
        }
        var result = sb.ToString().TrimStart('.').TrimEnd('.');
        if (string.IsNullOrEmpty(result)) result = "anon";

        // CodeQL cs/path-injection barrier — anchored Regex.IsMatch
        // against the allow-list. By construction the cleaned slug
        // always matches (we built it from the same character class),
        // so this is a recognised sanitiser the analyser drops the
        // taint on. Throw rather than silently substitute on a miss
        // so a future change to the cleaning loop can't sneak past
        // the barrier.
        if (!SafeIdPattern().IsMatch(result))
        {
            throw new ArgumentException(
                "Sanitised recording id failed the path-safety allow-list: " + id,
                nameof(id));
        }
        return result;
    }

    /// <summary>
    /// Validates that a content-addressed body file name is pure hex.
    /// Sha256 hashes we generate locally always pass; values read back
    /// from a step manifest's <c>responseRef</c> field carry on-disk
    /// taint and need the barrier before they land in a path.
    /// </summary>
    private static string SanitiseHash(string hash)
    {
        // CodeQL cs/path-injection barrier on the content-address.
        if (!SafeHashPattern().IsMatch(hash))
        {
            throw new ArgumentException(
                "Body-store hash failed the hex allow-list: " + hash,
                nameof(hash));
        }
        return hash;
    }

    /// <summary>
    /// Validates a step file name pulled from a recording's manifest.
    /// Step file names are minted internally as <c>NNNN.json</c>, but
    /// they're read back from the on-disk manifest before being
    /// combined with the steps directory, which is the path-injection
    /// vector this barrier closes.
    /// </summary>
    private static string SanitiseStepFile(string stepFile)
    {
        // The allow-list mirrors what we ever write — four digits plus
        // ".json". A wider class (alnum + '.') keeps the barrier
        // recognisable as a generic anchored allow-list to CodeQL
        // without forcing the digit-count check at runtime.
        if (!SafeIdPattern().IsMatch(stepFile))
        {
            throw new ArgumentException(
                "Step file name failed the path-safety allow-list: " + stepFile,
                nameof(stepFile));
        }
        return stepFile;
    }

    private static string Sha256(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        // ToHexString already emits uppercase; the lowercase pass was
        // cosmetic. CA1308 flags lowercase invariant — for a content-
        // addressing scheme either case works since we control both
        // sides.
        return Convert.ToHexString(hash);
    }

    private static void TryDeleteRecursive(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* best-effort; surface via UI delete next round */ }
    }
}

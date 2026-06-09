// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
internal static class ChunkedRecordingStore
{
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
    internal static string ResolveRootPath(string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId)) return RootPath;
        var sanitised = SanitiseId(workspaceId!);
        if (string.IsNullOrEmpty(sanitised)) return RootPath;
        return _testRootOverride is not null
            ? Path.Combine(_testRootOverride, "workspaces", sanitised, "recordings")
            : BowireUserContext.GetUserPath(Path.Combine("workspaces", sanitised, "recordings"));
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
    public static string LoadAll(string? workspaceId = null, bool manifestOnly = false)
    {
        lock (DiskLock)
        {
            MigrateFromLegacyIfNeeded();
            try
            {
                var root = ResolveRootPath(workspaceId);
                var recordings = new List<JsonElement>();
                if (Directory.Exists(root))
                {
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        var doc = manifestOnly
                            ? TryReadRecordingMetadataOnly(dir)
                            : TryAssembleRecording(dir);
                        if (doc.HasValue) recordings.Add(doc.Value);
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
    public static void SaveAll(string json, string? workspaceId = null)
    {
        lock (DiskLock)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("recordings", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Top-level object must carry a 'recordings' array.");
            }

            var rootPath = ResolveRootPath(workspaceId);
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
    public static void DeleteAll(string? workspaceId = null)
    {
        lock (DiskLock)
        {
            var root = ResolveRootPath(workspaceId);
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
    public static int AppendStep(string recordingId, JsonObject step, JsonObject? recordingMetadata, string? workspaceId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(recordingId);
        ArgumentNullException.ThrowIfNull(step);

        var id = SanitiseId(recordingId);
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("Recording id sanitised to empty.", nameof(recordingId));

        lock (DiskLock)
        {
            MigrateFromLegacyIfNeeded();
            var rootPath = ResolveRootPath(workspaceId);
            var dir = Path.Combine(rootPath, id);
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
            var stepFile = $"{idx:D4}.json";

            var stepObjForDisk = (JsonObject)step.DeepClone();

            // Same content-address handling as SaveAll — large bodies
            // get hashed into bodies/, small ones stay inline on the
            // step file.
            long stepBytes = 0;
            if (stepObjForDisk["response"] is JsonValue v
                && v.TryGetValue<string>(out var responseText)
                && responseText is { Length: > InlineBodyThreshold })
            {
                var hash = Sha256(responseText);
                var bodyPath = Path.Combine(bodiesDir, hash);
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
            File.WriteAllText(Path.Combine(stepsDir, stepFile), stepJson);

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
    public static string? LoadManifest(string recordingId, string? workspaceId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(recordingId);
        var id = SanitiseId(recordingId);
        if (string.IsNullOrEmpty(id)) return null;
        lock (DiskLock)
        {
            var path = Path.Combine(ResolveRootPath(workspaceId), id, RecordingMetadataFile);
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
    public static string? LoadStep(string recordingId, int stepIndex, string? workspaceId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(recordingId);
        if (stepIndex < 0) return null;
        var id = SanitiseId(recordingId);
        if (string.IsNullOrEmpty(id)) return null;
        lock (DiskLock)
        {
            var rootPath = ResolveRootPath(workspaceId);
            var stepPath = Path.Combine(rootPath, id, StepsDirectory, $"{stepIndex:D4}.json");
            if (!File.Exists(stepPath)) return null;
            JsonNode? stepNode;
            try { stepNode = JsonNode.Parse(File.ReadAllText(stepPath)); }
            catch { return null; }
            if (stepNode is JsonObject obj && obj["responseRef"] is JsonValue refVal)
            {
                var hash = (string?)refVal;
                if (!string.IsNullOrEmpty(hash))
                {
                    var bodyPath = Path.Combine(rootPath, id, BodiesDirectory, hash);
                    if (File.Exists(bodyPath))
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
            foreach (var entry in manifest)
            {
                if (entry is not JsonObject entryObj) continue;
                var stepFile = (string?)entryObj["file"];
                JsonNode? stepNode = null;
                if (!string.IsNullOrEmpty(stepFile))
                {
                    var path = Path.Combine(stepsDir, stepFile);
                    if (File.Exists(path))
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
                        var bodyPath = Path.Combine(recordingDir, BodiesDirectory, hash);
                        if (File.Exists(bodyPath))
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
        var dir = Path.Combine(rootPath, id);
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
                    var bodyPath = Path.Combine(bodiesDir, hash);
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
                File.WriteAllText(Path.Combine(stepsDir, stepFile), stepJson);

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
        foreach (var c in id)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
        }
        var result = sb.ToString().TrimStart('.').TrimEnd('.');
        return string.IsNullOrEmpty(result) ? "anon" : result;
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

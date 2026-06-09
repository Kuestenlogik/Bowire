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
    public static string LoadAll()
    {
        lock (DiskLock)
        {
            MigrateFromLegacyIfNeeded();
            try
            {
                var recordings = new List<JsonElement>();
                if (Directory.Exists(RootPath))
                {
                    foreach (var dir in Directory.EnumerateDirectories(RootPath))
                    {
                        var doc = TryAssembleRecording(dir);
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
    /// Save the supplied <c>{"recordings":[…]}</c> JSON document back
    /// into the chunked layout. Each recording's metadata + steps are
    /// split into their own files. Recordings not present in the
    /// document are removed from disk so the operator's UI delete
    /// propagates.
    /// </summary>
    public static void SaveAll(string json)
    {
        lock (DiskLock)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("recordings", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Top-level object must carry a 'recordings' array.");
            }

            Directory.CreateDirectory(RootPath);
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
                WriteOneRecording(id, rec);
            }

            // Sweep out directories the operator deleted from the UI.
            foreach (var dir in Directory.EnumerateDirectories(RootPath))
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
    public static void DeleteAll()
    {
        lock (DiskLock)
        {
            if (!Directory.Exists(RootPath)) return;
            foreach (var dir in Directory.EnumerateDirectories(RootPath))
            {
                TryDeleteRecursive(dir);
            }
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

    private static void WriteOneRecording(string id, JsonElement rec)
    {
        var dir = Path.Combine(RootPath, id);
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

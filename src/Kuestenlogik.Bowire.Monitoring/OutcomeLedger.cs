// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Monitoring;

/// <summary>
/// The append-only outcome ledger (#102, Decision 2). Each probe gets a
/// <c>&lt;name&gt;.jsonl</c> file under the ledger root; every run appends one
/// JSON line. The <b>last</b> row is the source of truth for restart resume
/// (when did this probe last run) and transition detection (was it passing or
/// failing). Reading the last row is a cheap tail read; the workbench surface
/// reads the whole file for the sparkline.
/// </summary>
public sealed class OutcomeLedger
{
    private readonly string _root;
    private readonly Lock _gate = new();

    /// <summary>
    /// JSON shape shared with the workbench reader: camelCase throughout — the
    /// top-level keys are pinned via <see cref="JsonPropertyNameAttribute"/>, and
    /// the naming policy carries camelCase into the nested assertion verdicts so
    /// the whole line is uniform (<c>passed</c> / <c>description</c>, not the
    /// PascalCase record defaults). The result enum serialises as a string so
    /// <c>&lt;probe&gt;.jsonl</c> stays human-readable; case-insensitive reads
    /// keep older lines loadable.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Create a ledger rooted at <paramref name="root"/> (created on first append).</summary>
    public OutcomeLedger(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
    }

    /// <summary>Absolute path of a probe's ledger file.</summary>
    public string PathFor(string probeName)
        => System.IO.Path.Combine(_root, Sanitise(probeName) + ".jsonl");

    /// <summary>Append one outcome as a JSON line (creates the root + file on demand).</summary>
    public void Append(string probeName, ProbeOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var line = JsonSerializer.Serialize(outcome, Json);
        lock (_gate)
        {
            Directory.CreateDirectory(_root);
            File.AppendAllText(PathFor(probeName), line + "\n");
        }
    }

    /// <summary>
    /// The last recorded outcome for a probe, or <c>null</c> when it has never
    /// run (no file / empty file). Tolerant of a trailing partial line — scans
    /// backward for the last parseable row.
    /// </summary>
    public ProbeOutcome? LastOutcome(string probeName)
    {
        var path = PathFor(probeName);
        lock (_gate)
        {
            if (!File.Exists(path)) return null;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (IOException)
            {
                return null;
            }
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;
                try
                {
                    var outcome = JsonSerializer.Deserialize<ProbeOutcome>(line, Json);
                    if (outcome is not null) return outcome;
                }
                catch (JsonException)
                {
                    // Skip a corrupt / partially-written trailing line.
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Probe names present under the ledger root — the <c>*.jsonl</c> file
    /// stems, sorted case-insensitively. These are the <em>sanitised</em>
    /// names (see <see cref="PathFor"/>); the workbench surface lists and
    /// re-queries by exactly these stems, so the round-trip stays lossless
    /// even when the original probe name carried characters the sanitiser
    /// folded away. Empty when the root doesn't exist yet (no probe ever ran).
    /// </summary>
    public IReadOnlyList<string> ListProbeNames()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_root)) return [];
            try
            {
                return [.. Directory.EnumerateFiles(_root, "*.jsonl")
                    .Select(System.IO.Path.GetFileNameWithoutExtension)
                    .Where(static n => !string.IsNullOrEmpty(n))
                    .Select(static n => n!)
                    .Order(StringComparer.OrdinalIgnoreCase)];
            }
            catch (IOException)
            {
                return [];
            }
        }
    }

    /// <summary>
    /// A probe's recorded outcomes in ledger order (oldest first), skipping
    /// corrupt / partially-written lines. <paramref name="maxRows"/> &gt; 0
    /// keeps only the newest rows — the tail the sparkline + outcome table
    /// render. Empty when the probe has never run.
    /// </summary>
    public IReadOnlyList<ProbeOutcome> ReadOutcomes(string probeName, int maxRows = 0)
    {
        var path = PathFor(probeName);
        lock (_gate)
        {
            if (!File.Exists(path)) return [];
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (IOException)
            {
                return [];
            }
            var outcomes = new List<ProbeOutcome>(lines.Length);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                try
                {
                    var outcome = JsonSerializer.Deserialize<ProbeOutcome>(line, Json);
                    if (outcome is not null) outcomes.Add(outcome);
                }
                catch (JsonException)
                {
                    // Skip a corrupt / partially-written line.
                }
            }
            return maxRows > 0 && outcomes.Count > maxRows
                ? outcomes[^maxRows..]
                : outcomes;
        }
    }

    private static string Sanitise(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        var s = new string(chars);
        return string.IsNullOrEmpty(s) ? "probe" : s;
    }
}

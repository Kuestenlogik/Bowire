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

    private static string Sanitise(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        var s = new string(chars);
        return string.IsNullOrEmpty(s) ? "probe" : s;
    }
}

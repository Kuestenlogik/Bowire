// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kuestenlogik.Bowire.Parallel;

/// <summary>
/// One JSONL line per distributed-run event (#313), chained so a removed or
/// edited line shows.
/// </summary>
/// <remarks>
/// <para>
/// Every line carries <c>prevHash</c>, the SHA-256 of the line before it, and
/// the first carries 64 zeros. Snipping a line out of the middle, or editing
/// one, breaks the chain at the next line — <see cref="FirstBrokenLine"/>
/// finds where. What a chain cannot show is lines cut off the end; keep the
/// file where the process that writes it cannot also delete it if that
/// matters.
/// </para>
/// <para>
/// Written by the coordinator (<c>dispatch</c>, one per job) and by the
/// executor (<c>run</c> per job it ran, <c>refused</c> per target the
/// allowlist kept out, <c>unauthorized</c> for a wrong or missing token).
/// A failure to write is swallowed after a first attempt: the audit log
/// never takes a run down with it.
/// </para>
/// </remarks>
public sealed class BowireParallelAuditLog
{
    /// <summary>The <c>prevHash</c> of the first line.</summary>
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private string? _lastHash;

    /// <summary>A log under <c>{storageRoot}/audit/parallel.jsonl</c>.</summary>
    public BowireParallelAuditLog(string storageRoot, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        File = Path.Combine(Path.GetFullPath(storageRoot), "audit", "parallel.jsonl");
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The log file.</summary>
    public string File { get; }

    /// <summary>Append one event.</summary>
    /// <param name="kind"><c>dispatch</c>, <c>run</c>, <c>refused</c> or <c>unauthorized</c>.</param>
    /// <param name="fields">What else the line says — any JSON-serialisable object.</param>
    public void Record(string kind, object? fields = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var node = new JsonObject
        {
            ["at"] = _clock.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["kind"] = kind,
        };
        if (fields is not null && JsonSerializer.SerializeToNode(fields) is JsonObject extra)
        {
            foreach (var (key, value) in extra.ToList())
            {
                extra.Remove(key);
                if (key is not ("at" or "kind" or "prevHash")) node[key] = value;
            }
        }

        lock (_gate)
        {
            try
            {
                _lastHash ??= LastHash(File);
                node["prevHash"] = _lastHash;
                var line = node.ToJsonString();
                Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                System.IO.File.AppendAllText(File, line + "\n");
                _lastHash = Hash(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing an audit line is bad; losing the run over it is worse.
            }
        }
    }

    /// <summary>
    /// The 1-based number of the first line whose <c>prevHash</c> does not
    /// match the line before it, or null when the chain is intact.
    /// </summary>
    public static int? FirstBrokenLine(string file)
    {
        if (!System.IO.File.Exists(file)) return null;
        var expected = Genesis;
        var number = 0;
        foreach (var line in System.IO.File.ReadLines(file))
        {
            if (line.Length == 0) continue;
            number++;
            string? prev;
            try { prev = JsonNode.Parse(line)?["prevHash"]?.GetValue<string>(); }
            catch (JsonException) { return number; }
            if (!string.Equals(prev, expected, StringComparison.Ordinal)) return number;
            expected = Hash(line);
        }
        return null;
    }

    private static string LastHash(string file)
    {
        if (!System.IO.File.Exists(file)) return Genesis;
        string? last = null;
        foreach (var line in System.IO.File.ReadLines(file))
            if (line.Length > 0) last = line;
        return last is null ? Genesis : Hash(last);
    }

    private static string Hash(string line) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(line)));
}

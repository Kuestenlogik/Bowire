// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Benchmarking;

/// <summary>
/// On-disk store for benchmark schedules and their run history (#232),
/// under <c>.bowire/benchmark-schedules/</c>.
/// <para>
/// Persistence <em>is</em> the restart-survival requirement: the hosted
/// service holds no schedule state of its own, it re-reads this directory on
/// boot. One JSON file per schedule, plus a sibling
/// <c>&lt;id&gt;.runs.json</c> holding the newest-first history, so a run
/// appended by a firing never rewrites the schedule definition an operator
/// may be editing.
/// </para>
/// </summary>
public sealed class BowireBenchmarkScheduleStore
{
    /// <summary>Directory name under the project's <c>.bowire</c> folder.</summary>
    public const string DirectoryName = "benchmark-schedules";

    /// <summary>How many runs are kept per schedule before the oldest are dropped.</summary>
    public const int MaxRunsPerSchedule = 50;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly string _directory;

    /// <summary>Create a store rooted at <paramref name="rootPath"/> (defaults to the current directory).</summary>
    public BowireBenchmarkScheduleStore(string? rootPath = null)
    {
        // Fully qualified: this type's own Directory property shadows the
        // System.IO one inside the class body.
        _directory = Path.Combine(
            rootPath ?? System.IO.Directory.GetCurrentDirectory(), ".bowire", DirectoryName);
    }

    /// <summary>The resolved schedules directory.</summary>
    public string Directory => _directory;

    /// <summary>
    /// Every stored schedule. A missing directory is an empty list, and an
    /// unreadable or malformed file is skipped — one bad file must not stop
    /// the other schedules from running.
    /// </summary>
    public async Task<List<BowireBenchmarkSchedule>> LoadAllAsync(CancellationToken ct = default)
    {
        var schedules = new List<BowireBenchmarkSchedule>();
        if (!System.IO.Directory.Exists(_directory)) return schedules;

        foreach (var file in System.IO.Directory
            .EnumerateFiles(_directory, "*.json")
            .Where(f => !f.EndsWith(".runs.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var schedule = JsonSerializer.Deserialize<BowireBenchmarkSchedule>(json, JsonOpts);
                if (schedule is not null && !string.IsNullOrWhiteSpace(schedule.Id)) schedules.Add(schedule);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Skip this one; the rest of the schedules stand.
            }
        }
        return schedules;
    }

    /// <summary>Read one schedule by id, or null when it isn't stored.</summary>
    public async Task<BowireBenchmarkSchedule?> LoadAsync(string id, CancellationToken ct = default)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<BowireBenchmarkSchedule>(json, JsonOpts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Write a schedule, creating the directory when needed.</summary>
    public async Task<string> SaveAsync(BowireBenchmarkSchedule schedule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (string.IsNullOrWhiteSpace(schedule.Id))
        {
            throw new ArgumentException("schedule needs an id", nameof(schedule));
        }

        System.IO.Directory.CreateDirectory(_directory);
        var path = PathFor(schedule.Id);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(schedule, JsonOpts), ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>Remove a schedule and its history. Returns false when it wasn't there.</summary>
    public bool Delete(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        var runs = RunsPathFor(id);
        if (File.Exists(runs)) File.Delete(runs);
        return true;
    }

    /// <summary>Run history for a schedule, newest first.</summary>
    public async Task<List<BowireBenchmarkScheduleRun>> LoadRunsAsync(string id, CancellationToken ct = default)
    {
        var path = RunsPathFor(id);
        if (!File.Exists(path)) return [];
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<BowireBenchmarkScheduleRun>>(json, JsonOpts) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Prepend a run to the schedule's history, trimming to
    /// <see cref="MaxRunsPerSchedule"/>. Newest-first so a reader wanting
    /// "how did the last run go?" takes the first element.
    /// </summary>
    public async Task AppendRunAsync(BowireBenchmarkScheduleRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        System.IO.Directory.CreateDirectory(_directory);
        var runs = await LoadRunsAsync(run.ScheduleId, ct).ConfigureAwait(false);
        runs.Insert(0, run);
        if (runs.Count > MaxRunsPerSchedule) runs.RemoveRange(MaxRunsPerSchedule, runs.Count - MaxRunsPerSchedule);
        await File.WriteAllTextAsync(
            RunsPathFor(run.ScheduleId), JsonSerializer.Serialize(runs, JsonOpts), ct).ConfigureAwait(false);
    }

    private string PathFor(string id) => Path.Combine(_directory, Sanitise(id) + ".json");

    private string RunsPathFor(string id) => Path.Combine(_directory, Sanitise(id) + ".runs.json");

    /// <summary>
    /// Ids come from an API caller, so a '..' or a slash must not escape the
    /// schedules directory.
    /// </summary>
    internal static string Sanitise(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "unnamed";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = id.Select(c => Array.IndexOf(invalid, c) >= 0 || c is ' ' or '.' ? '_' : c).ToArray();
        var safe = new string(chars).Trim('_');
        return safe.Length == 0 ? "unnamed" : safe;
    }
}

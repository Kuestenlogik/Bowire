// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Mock.Loading;

/// <summary>
/// Loads a Bowire recording from disk. Accepts two on-disk shapes:
/// <list type="bullet">
/// <item>The full recordings-store envelope <c>{"recordings":[...]}</c>
/// as written by <c>~/.bowire/recordings.json</c>. If the store contains
/// exactly one recording, it is returned; otherwise <see cref="Load"/>
/// throws unless a <c>name</c> or <c>id</c> is supplied.</item>
/// <item>A single-recording document at the top level — <c>{"id": ..., "steps": [...]}</c> —
/// for mocks that live next to the code they mock.</item>
/// </list>
/// </summary>
public static class RecordingLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Parse the given file and return a single recording ready for replay.
    /// </summary>
    /// <param name="path">Absolute or relative path to a recording JSON file.</param>
    /// <param name="select">Optional recording name or id to disambiguate a store with multiple recordings.</param>
    public static BowireRecording Load(string path, string? select = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Recording path is required.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException($"Recording file not found: {path}", path);

        var json = File.ReadAllText(path);
        var recording = ParseAndPickOne(json, select, path);
        Validate(recording, path);
        return recording;
    }

    /// <summary>
    /// Same as <see cref="Load"/> but operates on an in-memory JSON string —
    /// used by tests and by the <see cref="RecordingWatcher"/> when a file
    /// change is detected.
    /// </summary>
    public static BowireRecording Parse(string json, string? select = null, string sourceLabel = "<string>")
    {
        var recording = ParseAndPickOne(json, select, sourceLabel);
        Validate(recording, sourceLabel);
        return recording;
    }

    private static BowireRecording ParseAndPickOne(string json, string? select, string sourceLabel)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Store-wrapped shape: {"recordings":[...]}
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("recordings", out _))
        {
            var store = JsonSerializer.Deserialize<BowireRecordingStore>(json, JsonOptions)
                ?? throw new InvalidDataException($"Recording store at '{sourceLabel}' deserialised as null.");

            if (store.Recordings.Count == 0)
                throw new InvalidDataException($"Recording store at '{sourceLabel}' is empty.");

            if (select is not null)
            {
                var match = store.Recordings.FirstOrDefault(r =>
                    string.Equals(r.Id, select, StringComparison.Ordinal) ||
                    string.Equals(r.Name, select, StringComparison.Ordinal));
                return match ?? throw new InvalidDataException(
                    $"No recording named or identified as '{select}' in '{sourceLabel}'. " +
                    $"Available: {string.Join(", ", store.Recordings.Select(r => $"'{r.Name}' ({r.Id})"))}.");
            }

            if (store.Recordings.Count > 1)
                throw new InvalidDataException(
                    $"Recording store at '{sourceLabel}' contains {store.Recordings.Count} recordings. " +
                    $"Pass --name or --id to pick one. " +
                    $"Available: {string.Join(", ", store.Recordings.Select(r => $"'{r.Name}' ({r.Id})"))}.");

            return store.Recordings[0];
        }

        // Single-recording shape at the top level
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("steps", out _))
        {
            return JsonSerializer.Deserialize<BowireRecording>(json, JsonOptions)
                ?? throw new InvalidDataException($"Recording at '{sourceLabel}' deserialised as null.");
        }

        throw new InvalidDataException(
            $"File '{sourceLabel}' is neither a recording-store document (with a 'recordings' array) " +
            $"nor a single recording (with a 'steps' array).");
    }

    private static void Validate(BowireRecording recording, string sourceLabel)
    {
        if (!RecordingFormatVersion.IsSupported(recording.RecordingFormatVersion))
        {
            throw new InvalidDataException(
                $"Recording '{recording.Name}' at '{sourceLabel}' has format version " +
                $"{recording.RecordingFormatVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<missing>"}. " +
                $"This mock server supports version {RecordingFormatVersion.Current}. " +
                $"Re-record against a matching Bowire build.");
        }

        if (recording.Steps.Count == 0)
        {
            throw new InvalidDataException(
                $"Recording '{recording.Name}' at '{sourceLabel}' has no steps.");
        }
    }
}

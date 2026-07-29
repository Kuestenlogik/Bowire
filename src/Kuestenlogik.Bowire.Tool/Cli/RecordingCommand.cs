// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Mock.Loading;
using Kuestenlogik.Bowire.Recordings.Correlation;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// <c>bowire recording</c> — standalone tooling for the
/// <c>.bwr</c> file format (#210 — see <c>docs/recordings/bwr-format.md</c>).
///
/// <para>
/// <c>validate</c> parses a <c>.bwr</c> off disk, runs it through
/// <see cref="RecordingLoader.Load(string,string?)"/>, then runs the
/// standalone self-containment check (no <c>responseRef</c> fields on
/// any step). Exits with sysexits-style codes so a CI shell can branch
/// on the failure mode without parsing stderr.
/// </para>
///
/// <para>
/// <c>correlate</c> (#539) reads the same file through the same loader
/// and hands it to
/// <see cref="RecordingCorrelationAnalyzer"/> — the identical analysis
/// the workbench's Correlated-timeline tab runs over
/// <c>POST /api/recordings/correlate</c>, so the terminal and the UI
/// cannot disagree about what correlates.
/// </para>
///
/// <para>
/// Future siblings (<c>info</c>, <c>diff</c>, <c>extract</c>) reuse the
/// same scaffolding. None of them touch the workspace tree —
/// <see cref="RecordingLoader"/> is workspace-agnostic by construction.
/// </para>
/// </summary>
internal static class RecordingCommand
{
    // sysexits.h-style exit codes. Match what the workspace + export
    // commands already emit so a Makefile / GH Actions step can switch
    // on the failure mode without scraping stderr.
    private const int ExitOk = 0;
    private const int ExitUsage = 64;
    private const int ExitDataErr = 65;
    private const int ExitNoInput = 66;
    private const int ExitSoftware = 70;

    public static Command Build()
    {
        var recording = new Command("recording",
            "Standalone tooling for .bwr files — workspace-agnostic. Exposes `validate` and `correlate`; `info` / `diff` / `extract` slot in here when needed.");
        recording.Add(BuildValidateCommand());
        recording.Add(BuildCorrelateCommand());
        return recording;
    }

    private static Command BuildValidateCommand()
    {
        var validate = new Command("validate",
            "Parse + schema-check a .bwr file. Verifies recordingFormatVersion is supported by this build, at least one step is present, and no step carries a responseRef body-ref (a standalone .bwr inlines every body). Exit 0 on success, 64 bad args, 65 malformed file, 66 file not found.");

        var pathArg = new Argument<string>("path")
        {
            Description = "Path to the .bwr file to validate."
        };
        validate.Add(pathArg);

        var nameOpt = new Option<string?>("--name")
        {
            Description = "Disambiguate when the file is a store-wrapped envelope with multiple recordings. Matches against the recording's `name` or `id` field."
        };
        validate.Add(nameOpt);

        validate.SetAction((pr, ct) =>
        {
            var path = pr.GetValue(pathArg);
            var name = pr.GetValue(nameOpt);
            var io = CommandIo.Resolve(
                pr.InvocationConfiguration.Output,
                pr.InvocationConfiguration.Error);
            return Task.FromResult(RunValidate(path, name, io));
        });

        return validate;
    }

    private static int RunValidate(string? path, string? name, CommandIo io)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            io.ErrLine("bowire recording validate: path is required.");
            return ExitUsage;
        }

        try
        {
            var rec = RecordingLoader.Load(path, name);

            // Self-containment check: standalone .bwr files MUST inline
            // every body. responseRef is the chunked workspace-store
            // shape, never the standalone shape. The deserialised
            // model drops unknown properties, so the check runs
            // against the raw source JSON.
            var refSteps = FindResponseRefStepIds(path, name);
            if (refSteps.Count > 0)
            {
                io.ErrLine(
                    $"bowire recording validate: '{path}' carries responseRef body-refs on {refSteps.Count} step(s) " +
                    $"({string.Join(", ", refSteps.Take(5))}{(refSteps.Count > 5 ? ", …" : string.Empty)}). " +
                    $"A standalone .bwr must inline every body — re-export through the workbench or " +
                    $"resolve the refs manually before sharing.");
                return ExitDataErr;
            }

            io.OutLine($"OK: {path} — '{rec.Name}' ({rec.Id}), {rec.Steps.Count} step(s), formatVersion {rec.RecordingFormatVersion}.");
            return ExitOk;
        }
        catch (FileNotFoundException ex)
        {
            io.ErrLine($"bowire recording validate: {ex.Message}");
            return ExitNoInput;
        }
        catch (ArgumentException ex)
        {
            io.ErrLine($"bowire recording validate: {ex.Message}");
            return ExitUsage;
        }
        catch (InvalidDataException ex)
        {
            io.ErrLine($"bowire recording validate: {ex.Message}");
            return ExitDataErr;
        }
        catch (JsonException ex)
        {
            io.ErrLine($"bowire recording validate: invalid JSON in '{path}': {ex.Message}");
            return ExitDataErr;
        }
        catch (UnauthorizedAccessException ex)
        {
            io.ErrLine($"bowire recording validate: cannot read '{path}': {ex.Message}");
            return ExitNoInput;
        }
        catch (IOException ex)
        {
            io.ErrLine($"bowire recording validate: I/O error on '{path}': {ex.Message}");
            return ExitSoftware;
        }
    }

    private static Command BuildCorrelateCommand()
    {
        var correlate = new Command("correlate",
            "Read a .bwr as one correlated transaction: resolve a correlation signal (a traceparent / x-correlation-id header, else a shared id-shaped payload field), then print every step on a shared time axis with a strong/weak/no-match verdict. Exit 0 on success, 64 bad args, 65 malformed file, 66 file not found.");

        var pathArg = new Argument<string>("path")
        {
            Description = "Path to the .bwr file to correlate."
        };
        correlate.Add(pathArg);

        var nameOpt = new Option<string?>("--name")
        {
            Description = "Disambiguate when the file is a store-wrapped envelope with multiple recordings. Matches against the recording's `name` or `id` field."
        };
        correlate.Add(nameOpt);

        var keyOpt = new Option<string?>("--key")
        {
            Description = "Correlate on this key instead of the auto-detected one, as `name=value` (e.g. `--key shipId=101`). Overrides any `correlation` field persisted in the file."
        };
        correlate.Add(keyOpt);

        var jsonOpt = new Option<bool>("--json")
        {
            Description = "Emit the full correlation model as JSON instead of the table — the same shape the workbench's /api/recordings/correlate returns."
        };
        correlate.Add(jsonOpt);

        correlate.SetAction((pr, ct) =>
        {
            var path = pr.GetValue(pathArg);
            var name = pr.GetValue(nameOpt);
            var key = pr.GetValue(keyOpt);
            var json = pr.GetValue(jsonOpt);
            var io = CommandIo.Resolve(
                pr.InvocationConfiguration.Output,
                pr.InvocationConfiguration.Error);
            return Task.FromResult(RunCorrelate(path, name, key, json, io));
        });

        return correlate;
    }

    private static int RunCorrelate(string? path, string? name, string? key, bool json, CommandIo io)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            io.ErrLine("bowire recording correlate: path is required.");
            return ExitUsage;
        }

        RecordingCorrelationKey? explicitKey = null;
        if (!string.IsNullOrWhiteSpace(key))
        {
            var split = key.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0 || split == key.Length - 1)
            {
                io.ErrLine($"bowire recording correlate: --key must be `name=value` (got '{key}').");
                return ExitUsage;
            }
            var keyName = key[..split].Trim();
            var keyValue = key[(split + 1)..].Trim();
            explicitKey = new RecordingCorrelationKey(
                keyName, keyValue, RecordingCorrelationAnalyzer.ResolveSource(keyName));
        }

        try
        {
            var rec = RecordingLoader.Load(path, name);
            var timeline = RecordingCorrelationAnalyzer.Analyze(rec, explicitKey);

            if (json)
            {
                io.OutLine(JsonSerializer.Serialize(timeline, s_correlateJson));
                return ExitOk;
            }

            WriteCorrelationTable(timeline, io);
            return ExitOk;
        }
        catch (FileNotFoundException ex)
        {
            io.ErrLine($"bowire recording correlate: {ex.Message}");
            return ExitNoInput;
        }
        catch (ArgumentException ex)
        {
            io.ErrLine($"bowire recording correlate: {ex.Message}");
            return ExitUsage;
        }
        catch (InvalidDataException ex)
        {
            io.ErrLine($"bowire recording correlate: {ex.Message}");
            return ExitDataErr;
        }
        catch (JsonException ex)
        {
            io.ErrLine($"bowire recording correlate: invalid JSON in '{path}': {ex.Message}");
            return ExitDataErr;
        }
        catch (UnauthorizedAccessException ex)
        {
            io.ErrLine($"bowire recording correlate: cannot read '{path}': {ex.Message}");
            return ExitNoInput;
        }
        catch (IOException ex)
        {
            io.ErrLine($"bowire recording correlate: I/O error on '{path}': {ex.Message}");
            return ExitSoftware;
        }
    }

    // Plain columns, no ASCII bar art: a terminal table is the honest
    // CLI form of this view, and the repo's diagram convention is
    // Mermaid or SVG, never drawn characters.
    private static void WriteCorrelationTable(RecordingCorrelationTimeline t, CommandIo io)
    {
        io.OutLine($"{t.RecordingName} ({t.RecordingId}) — {t.Events.Count} step(s), {t.Lanes.Count} protocol(s)");
        io.OutLine(t.Key is null
            ? "key:      (none) — no correlation header and no id shared by two or more steps"
            : $"key:      {t.Key.Name} = {t.Key.Value}  [{t.Key.Source}]");
        io.OutLine($"matched:  {Num(t.MatchedStepCount)}/{Num(t.Events.Count)} step(s) across {Num(t.MatchedProtocolCount)}/{Num(t.Lanes.Count)} protocol(s)");
        io.OutLine($"span:     {Num(t.SpanMs)} ms ({t.Timebase} timebase)");
        io.OutLine();

        io.OutLine("    OFFSET  PROTOCOL    SERVICE / METHOD                                     DUR  STATUS    MATCH");
        foreach (var e in t.Events)
        {
            var offset = "+" + Num(e.OffsetMs) + "ms";
            var duration = Num(e.DurationMs) + "ms";
            var target = Clip(e.Service + " / " + e.Method, 46);
            var protocol = Clip(e.Protocol, 10);
            var status = Clip(e.Status, 8);
            var match = e.Match switch
            {
                RecordingCorrelationMatch.Strong => "strong",
                RecordingCorrelationMatch.Weak => "weak",
                _ => "–",
            };
            var frames = e.Frames.Count > 0 ? " (" + Num(e.Frames.Count) + " frames)" : string.Empty;
            io.OutLine(
                $"{offset,10}  {protocol,-10}  {target,-46}  {duration,8}  {status,-8}  {match}{frames}");
        }

        var others = t.Suggestions
            .Where(s => t.Key is null
                || !string.Equals(s.Name, t.Key.Name, StringComparison.Ordinal)
                || !string.Equals(s.Value, t.Key.Value, StringComparison.Ordinal))
            .Take(5)
            .ToList();
        if (others.Count > 0)
        {
            io.OutLine();
            io.OutLine("other candidate keys (pass one with --key name=value):");
            foreach (var s in others)
            {
                io.OutLine($"  {s.Name} = {s.Value}  [{s.Source}]  {Num(s.StepCount)} step(s), {string.Join(", ", s.Protocols)}");
            }
        }

        foreach (var warning in t.Warnings)
        {
            io.OutLine();
            io.OutLine($"note: {warning}");
        }
    }

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Clip(string? value, int width)
    {
        var s = value ?? string.Empty;
        return s.Length <= width ? s : string.Concat(s.AsSpan(0, Math.Max(1, width - 1)), "…");
    }

    // Same wire shape the /api/recordings/correlate endpoint emits, so
    // `--json` output and a captured HTTP response are diffable.
    private static readonly JsonSerializerOptions s_correlateJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // BowireRecordingStep doesn't model responseRef directly — the
    // workspace-side chunked layout carries it on the per-step JSON
    // document, but the deserialised model only knows Response. Re-read
    // the raw file as a JsonDocument and walk the steps array so we
    // can spot the marker key without round-tripping through the
    // typed model. Returns the ids of the offending steps so the error
    // message can name them.
    private static List<string> FindResponseRefStepIds(string path, string? select)
    {
        var raw = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        // Same envelope-detection RecordingLoader uses — store-wrapped
        // (`{"recordings":[...]}`) or single-recording-at-root.
        IEnumerable<JsonElement> recordings;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("recordings", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            recordings = arr.EnumerateArray();
            if (select is not null)
            {
                recordings = recordings.Where(r =>
                    (r.TryGetProperty("id", out var idEl) && idEl.GetString() == select) ||
                    (r.TryGetProperty("name", out var nameEl) && nameEl.GetString() == select));
            }
        }
        else
        {
            recordings = new[] { root };
        }

        var found = new List<string>();
        foreach (var rec in recordings)
        {
            if (rec.ValueKind != JsonValueKind.Object) continue;
            if (!rec.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) continue;
            foreach (var step in steps.EnumerateArray())
            {
                if (step.ValueKind != JsonValueKind.Object) continue;
                if (step.TryGetProperty("responseRef", out _))
                {
                    var id = step.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "?" : "?";
                    found.Add(id);
                }
            }
        }
        return found;
    }
}

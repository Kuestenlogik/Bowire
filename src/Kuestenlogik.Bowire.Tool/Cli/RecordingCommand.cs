// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Text.Json;
using Kuestenlogik.Bowire.Mock.Loading;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// <c>bowire recording</c> — standalone tooling for the
/// <c>.bwr</c> file format (#210 — see <c>docs/recordings/bwr-format.md</c>).
///
/// <para>
/// Today's single subcommand is <c>validate</c>: parse a <c>.bwr</c>
/// off disk, run it through <see cref="RecordingLoader.Load(string,string?)"/>,
/// then run the standalone self-containment check (no <c>responseRef</c>
/// fields on any step). Exits with sysexits-style codes so a CI shell
/// can branch on the failure mode without parsing stderr.
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
            "Standalone tooling for .bwr files — workspace-agnostic. Currently exposes `validate`; `info` / `diff` / `extract` slot in here when needed.");
        recording.Add(BuildValidateCommand());
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

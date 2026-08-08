// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Text.Json;
using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// <c>bowire project</c> — the checked-in <c>.bowire/project.json</c>
/// convention (#172). <c>show</c> auto-discovers the manifest by walking up
/// from the current directory and prints the resolved project; <c>validate</c>
/// loads it (auto-discovered, or a specific <c>--file</c>) and reports OK or the
/// list of actionable validation errors. Both share the on-disk model + loader
/// in Core (<see cref="BowireProjectLoader"/> / <see cref="BowireProjectFile"/>)
/// so the CLI, and any future workbench/MCP surface, cannot disagree about what
/// a manifest means.
/// </summary>
internal static class ProjectCommand
{
    // sysexits.h-style exit codes, matching the recording / workspace commands
    // so a CI shell can branch on the failure mode without scraping stderr.
    private const int ExitOk = 0;
    private const int ExitDataErr = 65;   // present but invalid
    private const int ExitNoInput = 66;   // no manifest found / file missing

    public static Command Build()
    {
        var project = new Command("project",
            "Work with the checked-in .bowire/project.json manifest (#172) — the version-controlled pointer to a repo's sources, suites, security config, and rules. `show` auto-discovers and prints it; `validate` schema-checks it.");
        project.Add(BuildShowCommand());
        project.Add(BuildValidateCommand());
        return project;
    }

    private static Command BuildShowCommand()
    {
        var show = new Command("show",
            "Auto-discover .bowire/project.json (walking up from the current directory), load it, and print the resolved project. Exit 0 on success, 65 if the manifest is invalid, 66 if none was found.");
        show.SetAction((pr, _) =>
        {
            var io = CommandIo.Resolve(pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error);
            return Task.FromResult(RunShow(io));
        });
        return show;
    }

    private static int RunShow(CommandIo io)
    {
        var located = BowireProjectLoader.Discover();
        if (located is null)
        {
            io.ErrLine("bowire project show: no .bowire/project.json found in the current directory or any parent.");
            return ExitNoInput;
        }

        BowireProjectFile project;
        try
        {
            project = BowireProjectLoader.Load(located.FilePath);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            io.ErrLine($"bowire project show: '{located.FilePath}' is not a valid project file: {ex.Message}");
            return ExitDataErr;
        }

        Render(project, located, io);
        return ExitOk;
    }

    private static Command BuildValidateCommand()
    {
        var fileOpt = new Option<string?>("--file")
        {
            Description = "Validate this specific manifest path instead of auto-discovering .bowire/project.json from the current directory.",
        };
        var validate = new Command("validate",
            "Load + schema-check a .bowire/project.json. Auto-discovers by walking up from the current directory unless --file is given. Exit 0 when valid, 65 when present but invalid, 66 when none was found.");
        validate.Add(fileOpt);
        validate.SetAction((pr, _) =>
        {
            var io = CommandIo.Resolve(pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error);
            return Task.FromResult(RunValidate(pr.GetValue(fileOpt), io));
        });
        return validate;
    }

    private static int RunValidate(string? file, CommandIo io)
    {
        string filePath;
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!File.Exists(file))
            {
                io.ErrLine($"bowire project validate: file not found: '{file}'.");
                return ExitNoInput;
            }
            filePath = file;
        }
        else
        {
            var located = BowireProjectLoader.Discover();
            if (located is null)
            {
                io.ErrLine("bowire project validate: no .bowire/project.json found in the current directory or any parent (use --file to point at one).");
                return ExitNoInput;
            }
            filePath = located.FilePath;
        }

        BowireProjectFile project;
        try
        {
            project = BowireProjectLoader.Load(filePath);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            io.ErrLine($"bowire project validate: '{filePath}' is invalid: {ex.Message}");
            return ExitDataErr;
        }

        var errors = project.Validate();
        if (errors.Count > 0)
        {
            io.ErrLine($"bowire project validate: '{filePath}' has {errors.Count} problem(s):");
            foreach (var error in errors)
                io.ErrLine($"  - {error}");
            return ExitDataErr;
        }

        io.OutLine($"OK: {filePath} — valid project file (version {project.Version}).");
        return ExitOk;
    }

    private static void Render(BowireProjectFile project, BowireProjectLocation located, CommandIo io)
    {
        io.OutLine($"Project: {(string.IsNullOrWhiteSpace(project.Name) ? "(unnamed)" : project.Name)}");
        io.OutLine($"  file:    {located.FilePath}");
        io.OutLine($"  root:    {located.ProjectRoot}");
        io.OutLine($"  version: {project.Version}");

        io.OutLine($"  sources ({project.Sources.Count}):");
        foreach (var source in project.Sources)
        {
            var schemas = source.Schemas.Count == 0 ? string.Empty : $" [{string.Join(", ", source.Schemas)}]";
            io.OutLine($"    - {source.Url}{schemas}");
        }

        io.OutLine($"  suites ({project.Suites.Count}):");
        foreach (var (key, path) in project.Suites)
            io.OutLine($"    - {key}: {path}");

        if (project.Security is not null)
        {
            io.OutLine("  security:");
            io.OutLine($"    auth: {project.Security.Auth ?? "(none)"}");
            io.OutLine($"    scan: {(project.Security.Scan.Count == 0 ? "(none)" : string.Join(", ", project.Security.Scan))}");
        }
        else
        {
            io.OutLine("  security: (none)");
        }

        io.OutLine($"  rules: {project.Rules ?? "(none)"}");
    }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// <c>bowire auth-recording</c> — manage captured auth recordings (#563): the
/// credentials a schema mock's auth requirement resolves by id
/// (<c>auth.authRecordingId</c>). Parity with the workbench auth-card picker
/// and the <c>bowire.auth-recording.*</c> MCP tools — the same on-disk
/// <c>AuthRecordingStore</c> backs all three, so a recording captured here is
/// immediately selectable in the UI and resolvable by a running mock.
/// </summary>
internal static class AuthRecordingCommand
{
    private const int ExitOk = 0;
    private const int ExitUsage = 64;
    private const int ExitDataErr = 65;

    public static Command Build()
    {
        var cmd = new Command("auth-recording",
            "Manage captured auth recordings — the credentials a mock's auth requirement resolves by id. capture / list / remove; the same store the workbench picker and the MCP tools use.");
        cmd.Add(BuildCaptureCommand());
        cmd.Add(BuildListCommand());
        cmd.Add(BuildRemoveCommand());
        return cmd;
    }

    private static Option<string?> WorkspaceOption() => new("--workspace")
    {
        Description = "Workspace to scope the recording to (default: the shared, unscoped store)."
    };

    private static Command BuildCaptureCommand()
    {
        var idOpt = new Option<string>("--id") { Description = "Recording id, referenced by a mock's auth.authRecordingId." };
        var credEnvOpt = new Option<string?>("--credential-env")
        {
            Description = "Name of the environment variable holding the credential to store. Read from the environment so the secret never lands in shell history or process args.",
        };
        var nameOpt = new Option<string?>("--name") { Description = "Human label shown in the picker (default: the id)." };
        var schemeOpt = new Option<string>("--scheme") { Description = "Credential scheme: bearer / basic / apikey.", DefaultValueFactory = _ => "bearer" };
        schemeOpt.CompletionSources.Add("bearer", "basic", "apikey");
        var headerOpt = new Option<string?>("--header") { Description = "Header the credential is presented in (default: Authorization)." };
        var wsOpt = WorkspaceOption();

        var capture = new Command("capture",
            "Store a credential as a named auth recording. The value is read from --credential-env, never a raw flag. Exit 0 ok, 64 bad args, 65 store error.");
        capture.Add(idOpt);
        capture.Add(credEnvOpt);
        capture.Add(nameOpt);
        capture.Add(schemeOpt);
        capture.Add(headerOpt);
        capture.Add(wsOpt);
        capture.SetAction((pr, _) =>
        {
            var io = CommandIo.Resolve(pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error);
            return Task.FromResult(RunCapture(
                pr.GetValue(idOpt), pr.GetValue(credEnvOpt), pr.GetValue(nameOpt),
                pr.GetValue(schemeOpt), pr.GetValue(headerOpt), pr.GetValue(wsOpt), io));
        });
        return capture;
    }

    private static int RunCapture(
        string? id, string? credentialEnv, string? name, string? scheme, string? header, string? workspace, CommandIo io)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            io.ErrLine("bowire auth-recording capture: --id is required.");
            return ExitUsage;
        }
        if (string.IsNullOrWhiteSpace(credentialEnv))
        {
            io.ErrLine("bowire auth-recording capture: --credential-env is required (the env var holding the credential).");
            return ExitUsage;
        }
        var credential = Environment.GetEnvironmentVariable(credentialEnv);
        if (string.IsNullOrEmpty(credential))
        {
            io.ErrLine($"bowire auth-recording capture: environment variable '{credentialEnv}' is not set or empty.");
            return ExitUsage;
        }

        var recording = new AuthRecording
        {
            Id = id,
            Name = name,
            Scheme = string.IsNullOrWhiteSpace(scheme) ? "bearer" : scheme,
            Header = header,
            Credential = credential,
            CapturedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        try
        {
            AuthRecordingStore.Save(workspace ?? string.Empty, storageRoot: null, recording);
        }
        catch (ArgumentException ex)
        {
            io.ErrLine($"bowire auth-recording capture: {ex.Message}");
            return ExitDataErr;
        }
        io.OutLine($"Captured auth recording '{id}'.");
        return ExitOk;
    }

    private static Command BuildListCommand()
    {
        var wsOpt = WorkspaceOption();
        var list = new Command("list", "List captured auth recordings — credential-free (ids, names, schemes).");
        list.Add(wsOpt);
        list.SetAction((pr, _) =>
        {
            var io = CommandIo.Resolve(pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error);
            return Task.FromResult(RunList(pr.GetValue(wsOpt), io));
        });
        return list;
    }

    private static int RunList(string? workspace, CommandIo io)
    {
        var recordings = AuthRecordingStore.List(workspace ?? string.Empty, storageRoot: null);
        if (recordings.Count == 0)
        {
            io.OutLine("No auth recordings.");
            return ExitOk;
        }
        var idWidth = recordings.Max(r => r.Id.Length);
        foreach (var r in recordings)
        {
            io.OutLine($"  {r.Id.PadRight(idWidth)}  {r.Name}  [{r.Scheme ?? "bearer"}]");
        }
        return ExitOk;
    }

    private static Command BuildRemoveCommand()
    {
        var idArg = new Argument<string>("id") { Description = "Recording id to remove." };
        var wsOpt = WorkspaceOption();
        var remove = new Command("remove", "Delete a captured auth recording.");
        remove.Add(idArg);
        remove.Add(wsOpt);
        remove.SetAction((pr, _) =>
        {
            var io = CommandIo.Resolve(pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error);
            return Task.FromResult(RunRemove(pr.GetValue(idArg), pr.GetValue(wsOpt), io));
        });
        return remove;
    }

    private static int RunRemove(string? id, string? workspace, CommandIo io)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            io.ErrLine("bowire auth-recording remove: id is required.");
            return ExitUsage;
        }
        var removed = AuthRecordingStore.Delete(workspace ?? string.Empty, storageRoot: null, id);
        io.OutLine(removed ? $"Removed auth recording '{id}'." : $"No auth recording '{id}'.");
        return ExitOk;
    }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Grpc.Core;
using Kuestenlogik.Bowire.Interop;
using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// <c>bowire import &lt;file|dir&gt;</c> and <c>bowire export http</c> (#673, #674):
/// the CLI face of the Interop rpcs. Both call <see cref="InteropRpc"/>, the
/// handler behind the gRPC service the workbench uses, so the CLI reads the
/// same formats and reports the same skips.
/// </summary>
internal static class InteropCommands
{
    /// <summary>Give the existing <c>import</c> command its own action for collections.</summary>
    public static void AttachImport(Command import)
    {
        var pathArg = new Argument<string?>("path")
        {
            Description = "An Insomnia export (v4 JSON/YAML or v5 YAML), or a .http file or a directory of them with http-client.env.json.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var formatOpt = new Option<string?>("--format") { Description = "Skip sniffing: insomnia or http." };
        var nameOpt = new Option<string?>("--name", "-n") { Description = "The collection name when the source has none." };
        var outOpt = new Option<string?>("--out", "-o")
        {
            Description = "Where to write the .bww workspace file. \"-\" writes it to stdout. "
                + "Default: <name>.bww next to the input. Import it with the workbench or `bowire workspace import`.",
        };
        import.Add(pathArg); import.Add(formatOpt); import.Add(nameOpt); import.Add(outOpt);
        import.SetAction(async (pr, ct) =>
        {
            var path = pr.GetValue(pathArg);
            var stdout = pr.InvocationConfiguration.Output;
            var stderr = pr.InvocationConfiguration.Error;
            if (string.IsNullOrWhiteSpace(path))
            {
                await stderr.WriteLineAsync("bowire import: give a file or directory (or `bowire import har <file>` for a HAR trace).").ConfigureAwait(false);
                return 64;
            }
            return await ImportAsync(path, pr.GetValue(formatOpt), pr.GetValue(nameOpt), pr.GetValue(outOpt), stdout, stderr, ct).ConfigureAwait(false);
        });
    }

    internal static async Task<int> ImportAsync(string path, string? format, string? name, string? output, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        var request = new ImportCollectionRequest { Format = format ?? "", Name = name ?? "" };
        try
        {
            request.Files.Add(await InteropSources.ReadAsync(full, ct).ConfigureAwait(false));
        }
        catch (FileNotFoundException)
        {
            await stderr.WriteLineAsync($"bowire import: {path} does not exist.").ConfigureAwait(false);
            return 66;
        }

        ImportCollectionResponse result;
        try
        {
            result = InteropRpc.ImportCollection(request);
        }
        catch (RpcException ex)
        {
            await stderr.WriteLineAsync("bowire import: " + ex.Status.Detail).ConfigureAwait(false);
            return 65;
        }

        var workspaceName = name ?? result.Collections.FirstOrDefault()?.Name ?? Path.GetFileNameWithoutExtension(full.TrimEnd('/', '\\'));
        var document = BowireWorkspaceJson.ToWorkspaceFile(result, workspaceName);
        var reportTo = output == "-" ? stderr : stdout;
        if (output == "-")
        {
            await stdout.WriteLineAsync(document).ConfigureAwait(false);
        }
        else
        {
            var target = output ?? Path.Combine(Path.GetDirectoryName(full.TrimEnd('/', '\\')) ?? ".", SafeFileName(workspaceName) + ".bww");
            await File.WriteAllTextAsync(target, document, ct).ConfigureAwait(false);
            var requests = result.Collections.Sum(c => c.Items.Count);
            await reportTo.WriteLineAsync(
                $"Imported {requests} request(s) in {result.Collections.Count} collection(s) and {result.Environments.Count} environment(s) from {result.Format} → {target}").ConfigureAwait(false);
        }
        await WriteReportAsync(result.Report, reportTo).ConfigureAwait(false);
        return 0;
    }

    /// <summary><c>bowire export http</c>.</summary>
    public static Command BuildExportHttp()
    {
        var fileArg = new Argument<string>("file")
        {
            Description = "A .bww workspace file, a workspace's collections.json, or an array of collections.",
        };
        var outOpt = new Option<string?>("--output", "-o") { Description = "The .http file to write. Default: <file>.http next to the input; \"-\" for stdout." };
        var envOpt = new Option<string?>("--env-output") { Description = "Where to write http-client.env.json. Default: next to the .http file." };
        var dialectOpt = new Option<string?>("--dialect") { Description = "jetbrains (default: Rider, IntelliJ, Visual Studio 2022) or vscode (REST Client)." };
        var export = new Command("http", "Write collections as a .http file (and environments as http-client.env.json) the IDEs run.");
        export.Add(fileArg); export.Add(outOpt); export.Add(envOpt); export.Add(dialectOpt);
        export.SetAction(async (pr, ct) =>
        {
            var stdout = pr.InvocationConfiguration.Output;
            var stderr = pr.InvocationConfiguration.Error;
            var input = Path.GetFullPath(pr.GetValue(fileArg)!);
            if (!File.Exists(input))
            {
                await stderr.WriteLineAsync($"bowire export http: {input} does not exist.").ConfigureAwait(false);
                return 66;
            }
            var request = new ExportHttpRequest { Dialect = pr.GetValue(dialectOpt) ?? "" };
            try
            {
                var (collections, environments) = BowireWorkspaceJson.Read(await File.ReadAllTextAsync(input, ct).ConfigureAwait(false));
                request.Collections.Add(collections);
                request.Environments.Add(environments);
                var result = InteropRpc.ExportHttp(request);

                var output = pr.GetValue(outOpt);
                var reportTo = output == "-" ? stderr : stdout;
                if (output == "-")
                {
                    await stdout.WriteAsync(result.Http).ConfigureAwait(false);
                }
                else
                {
                    output ??= Path.ChangeExtension(input, ".http");
                    await File.WriteAllTextAsync(output, result.Http, ct).ConfigureAwait(false);
                    await reportTo.WriteLineAsync($"Wrote {output}").ConfigureAwait(false);
                }
                if (result.EnvJson.Length > 0)
                {
                    var envOutput = pr.GetValue(envOpt)
                        ?? Path.Combine(Path.GetDirectoryName(output == "-" || output is null ? input : Path.GetFullPath(output))!, "http-client.env.json");
                    await File.WriteAllTextAsync(envOutput, result.EnvJson, ct).ConfigureAwait(false);
                    await reportTo.WriteLineAsync($"Wrote {envOutput}").ConfigureAwait(false);
                }
                await WriteReportAsync(result.Report, reportTo).ConfigureAwait(false);
                return 0;
            }
            catch (InteropFormatException ex)
            {
                await stderr.WriteLineAsync("bowire export http: " + ex.Message).ConfigureAwait(false);
                return 65;
            }
            catch (RpcException ex)
            {
                await stderr.WriteLineAsync("bowire export http: " + ex.Status.Detail).ConfigureAwait(false);
                return 65;
            }
        });
        return export;
    }

    private static async Task WriteReportAsync(ImportReport report, TextWriter writer)
    {
        foreach (var entry in report.Entries.Where(e => e.Kind != ReportEntry.Types.Kind.Mapped))
        {
            var label = entry.Kind == ReportEntry.Types.Kind.Skipped ? "skipped" : "changed";
            await writer.WriteLineAsync($"  {label}: {entry.Subject}: {entry.Detail}").ConfigureAwait(false);
        }
        foreach (var credential in report.Credentials)
            await writer.WriteLineAsync($"  credential in clear text: {credential.Subject}: {credential.Field}").ConfigureAwait(false);
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        return safe.Length == 0 ? "imported" : safe;
    }
}

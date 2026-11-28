// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Text.Json;
using Kuestenlogik.Bowire.Ai;
using Kuestenlogik.Bowire.Ai.Anthropic;
using Kuestenlogik.Bowire.Ai.Mcp;
using Kuestenlogik.Bowire.Ai.OpenAi;
using Kuestenlogik.Bowire.Scaffold;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// <c>bowire scaffold</c> (#177) — a sentence or a spec file in, a service
/// folder out: schema, C# stub, workbench collection, smoke test.
/// </summary>
/// <remarks>
/// Without <c>--ai</c> the sentence is read by the deterministic parser, so
/// the command needs no model and gives the same result everywhere. With
/// <c>--ai</c> the assistant writes the spec — local model first, the
/// configured provider (<c>Bowire__Ai__*</c> or Settings → AI) as fallback.
/// Either way the files come from the checked-in templates, and
/// <c>--print-spec</c> stops at the spec so it can be edited and fed back
/// with <c>--spec</c>.
/// </remarks>
internal static class ScaffoldCommand
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static Command Build()
    {
        var cmd = new Command(
            "scaffold",
            BowireLocale.T("cli.scaffold.scaffold.command"));

        var intentArg = new Argument<string?>("intent")
        {
            Description = BowireLocale.T("cli.scaffold.intent"),
            Arity = ArgumentArity.ZeroOrOne,
        };
        var protocolOpt = new Option<string?>("--protocol", "-p")
        {
            Description = BowireLocale.T("cli.scaffold.protocol"),
        };
        var languageOpt = new Option<string?>("--language", "-l")
        {
            Description = BowireLocale.T("cli.scaffold.language"),
        };
        var specOpt = new Option<string?>("--spec")
        {
            Description = BowireLocale.T("cli.scaffold.spec"),
        };
        var outOpt = new Option<string?>("--out", "-o")
        {
            Description = BowireLocale.T("cli.scaffold.out"),
        };
        var aiOpt = new Option<bool>("--ai")
        {
            Description = BowireLocale.T("cli.scaffold.ai"),
        };
        var printOpt = new Option<bool>("--print-spec")
        {
            Description = BowireLocale.T("cli.scaffold.print-spec"),
        };
        var forceOpt = new Option<bool>("--force")
        {
            Description = BowireLocale.T("cli.scaffold.force"),
        };
        var baseUrlOpt = new Option<string?>("--base-url")
        {
            Description = BowireLocale.T("cli.scaffold.base-url"),
        };

        cmd.Add(intentArg);
        cmd.Add(protocolOpt);
        cmd.Add(languageOpt);
        cmd.Add(specOpt);
        cmd.Add(outOpt);
        cmd.Add(aiOpt);
        cmd.Add(printOpt);
        cmd.Add(forceOpt);
        cmd.Add(baseUrlOpt);
        cmd.SetAction(async (pr, ct) => await RunAsync(
            pr.GetValue(intentArg), pr.GetValue(protocolOpt), pr.GetValue(specOpt), pr.GetValue(outOpt),
            pr.GetValue(aiOpt), pr.GetValue(printOpt), pr.GetValue(forceOpt), pr.GetValue(baseUrlOpt),
            pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, ct,
            language: pr.GetValue(languageOpt)).ConfigureAwait(false));
        return cmd;
    }

    internal static async Task<int> RunAsync(
        string? intent, string? protocol, string? specPath, string? outDir, bool ai, bool printSpec, bool force,
        string? baseUrl, TextWriter stdout, TextWriter stderr, CancellationToken ct,
        Func<CancellationToken, Task<IReadOnlyList<ScaffoldModel>>>? models = null, string? language = null)
    {
        ScaffoldSpec spec;
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(specPath))
        {
            if (!File.Exists(specPath))
            {
                await stderr.WriteLineAsync($"Spec file not found: {specPath}").ConfigureAwait(false);
                return 2;
            }
            try
            {
                spec = JsonSerializer.Deserialize<ScaffoldSpec>(await File.ReadAllTextAsync(specPath, ct).ConfigureAwait(false), s_json)
                    ?? throw new JsonException("empty");
            }
            catch (JsonException ex)
            {
                await stderr.WriteLineAsync($"{specPath} is not a scaffold spec: {ex.Message}").ConfigureAwait(false);
                return 2;
            }
            if (!string.IsNullOrWhiteSpace(protocol)) spec = spec with { Protocol = protocol };
        }
        else if (!string.IsNullOrWhiteSpace(intent))
        {
            if (ai)
            {
                var candidates = await (models ?? ConfiguredModelsAsync)(ct).ConfigureAwait(false);
                var proposal = await BowireAiScaffold.ProposeAsync(candidates, intent, protocol, ct).ConfigureAwait(false);
                spec = proposal.Spec;
                notes.AddRange(proposal.Notes);
                notes.Add(proposal.Source == "ai" ? $"spec written by {proposal.Model}" : "no model gave a usable spec; the parser read the sentence");
            }
            else
            {
                var (parsed, parserNotes) = ScaffoldIntentParser.Parse(intent, protocol, language);
                spec = parsed;
                notes.AddRange(parserNotes);
            }
        }
        else
        {
            await stderr.WriteLineAsync("Say what to build (bowire scaffold \"REST CRUD for User with email + role\") or pass --spec.").ConfigureAwait(false);
            return 2;
        }
        if (!string.IsNullOrWhiteSpace(baseUrl)) spec = spec with { BaseUrl = baseUrl };
        if (!string.IsNullOrWhiteSpace(language)) spec = spec with { Language = language };

        var (normalized, errors) = spec.Normalize();
        foreach (var note in notes) await stderr.WriteLineAsync("note: " + note).ConfigureAwait(false);
        if (normalized is null)
        {
            foreach (var e in errors) await stderr.WriteLineAsync("error: " + e).ConfigureAwait(false);
            return 2;
        }
        if (printSpec)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(normalized, s_json)).ConfigureAwait(false);
            return 0;
        }

        var target = Path.GetFullPath(string.IsNullOrWhiteSpace(outDir) ? normalized.Service! : outDir);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() && !force)
        {
            await stderr.WriteLineAsync($"{target} is not empty; pass --force to write into it.").ConfigureAwait(false);
            return 2;
        }
        foreach (var file in ScaffoldGenerator.Generate(normalized))
        {
            var path = Path.Combine(target, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, file.Content, ct).ConfigureAwait(false);
            await stdout.WriteLineAsync($"  {file.Kind,-10} {file.Path}").ConfigureAwait(false);
        }
        await stdout.WriteLineAsync($"Scaffolded {normalized.Service} ({normalized.Protocol}) in {target}").ConfigureAwait(false);
        var run = (normalized.Protocol, normalized.Language) switch
        {
            ("graphql" or "grpc", "node") => "npm install && node server.mjs",
            ("graphql" or "grpc", "python") => "pip install -r requirements.txt && python server.py",
            ("grpc", "go") => "go generate ./... && go mod tidy && go run .",
            (_, var lang) => lang switch
            {
                "go" => "go run .",
                "node" => "node server.mjs",
                "python" => "python server.py",
                "rust" => "cargo run",
                _ => "dotnet run",
            },
        };
        await stdout.WriteLineAsync($"Next: {run} there, then bowire test bowire/*.smoke-test.json").ConfigureAwait(false);
        return 0;
    }

    // The same providers the workbench offers, configured the same way:
    // Bowire__Ai__* in the environment, then what Settings -> AI saved.
    private static async Task<IReadOnlyList<ScaffoldModel>> ConfiguredModelsAsync(CancellationToken ct)
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var services = new ServiceCollection();
        services.AddBowireAi(config);
        services.AddBowireAiOpenAi();
        services.AddBowireAiAnthropic();
        services.AddBowireAiMcp();
        var sp = services.BuildServiceProvider();
        var runtime = sp.GetRequiredService<BowireAiRuntime>();
        var preferLocal = config.GetValue<bool?>("Bowire:Ai:Scaffold:PreferLocal") ?? true;
        return await BowireAiScaffold.ModelsAsync(runtime.Options, runtime.Current, sp.GetServices<IBowireAiProviderFactory>(), preferLocal, ct).ConfigureAwait(false);
    }
}

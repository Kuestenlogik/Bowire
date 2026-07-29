// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.CommandLine;
using Kuestenlogik.Bowire.App.Cli;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// #538 — the C# half of the "Copy as Bowire CLI" honesty mechanism.
/// <para>
/// The workbench's Code tab can emit a runnable <c>bowire call …</c> line.
/// That export is the only one whose target is Bowire itself, which makes
/// it the only one that can rot silently: rename a flag in
/// <see cref="BowireCli.BuildCallCommand"/> and every other generator
/// (curl, grpcurl, wscat, …) keeps working while this one starts handing
/// operators a command the CLI rejects.
/// </para>
/// <para>
/// So the generator and the grammar are pinned from both sides against one
/// fixture, <c>Cli/cli-export-golden.json</c>:
/// </para>
/// <list type="bullet">
///   <item>
///     <c>wwwroot-js/cli-export.test.mjs</c> asserts the JS generator still
///     produces each scenario's <c>argv</c> and rendered string.
///   </item>
///   <item>
///     This class feeds the SAME <c>argv</c> arrays through the real
///     <c>System.CommandLine</c> <c>call</c> command and asserts zero parse
///     errors.
///   </item>
/// </list>
/// <para>
/// Rename or drop a flag on <c>call</c> → this test fails. Change the
/// generator → the JS test fails. Neither can drift alone. Parsing is done
/// against the command object directly (the
/// <see cref="BowireCliPhase3PolishTests"/> shape), so nothing boots and no
/// network is touched.
/// </para>
/// </summary>
public sealed class CliExportGrammarTests
{
    [Fact]
    public void Fixture_is_present_and_non_empty()
    {
        // Without this, an accidentally emptied or renamed fixture would
        // make every [Theory] below vacuously green.
        var scenarios = LoadScenarios();
        Assert.NotEmpty(scenarios);
        Assert.All(scenarios, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Scenario));
            Assert.NotEmpty(s.Argv);
        });
    }

    [Fact]
    public void Every_generated_command_parses_against_the_real_call_grammar()
    {
        var call = BowireCli.BuildCallCommand(EmptyConfig());

        foreach (var scenario in LoadScenarios())
        {
            var args = StripInvocationPrefix(scenario.Argv);
            var errors = call.Parse(args).Errors.Select(e => e.Message).ToList();
            Assert.True(errors.Count == 0,
                $"scenario '{scenario.Scenario}' produced a command `bowire call` rejects:"
                + Environment.NewLine + "  args:   " + string.Join(" ", args)
                + Environment.NewLine + "  errors: " + string.Join("; ", errors));
        }
    }

    [Fact]
    public void Every_generated_command_binds_the_target_as_the_positional()
    {
        // A parse with zero errors is not enough on its own: an option
        // whose arity greedily swallowed the positional would also parse
        // clean, and the operator would get "Usage: bowire call …" at
        // runtime. Assert the service/method token actually landed in the
        // `target` argument.
        var call = BowireCli.BuildCallCommand(EmptyConfig());
        var target = call.Arguments.Single(a => a.Name == "target");

        foreach (var scenario in LoadScenarios())
        {
            var args = StripInvocationPrefix(scenario.Argv);
            var parsed = call.Parse(args);
            var bound = parsed.GetValue((Argument<string>)target);
            Assert.True(!string.IsNullOrEmpty(bound),
                $"scenario '{scenario.Scenario}' did not bind a target: " + string.Join(" ", args));
        }
    }

    [Theory]
    // The flags the generator emits, spelled exactly as it spells them.
    // -H has no --header alias and --data is the long form of -d; getting
    // either wrong is the classic way a hand-written exporter drifts.
    //
    // Asserted against the declared option names/aliases rather than the
    // rendered help text: System.CommandLine's help prints only ONE of an
    // option's spellings (it shows `-url`, not `--url`), and a substring
    // search over help would in any case match a flag merely *mentioned*
    // in another option's description.
    [InlineData("--url")]
    [InlineData("--protocol")]
    [InlineData("--stream")]
    [InlineData("-d")]
    [InlineData("--data")]
    [InlineData("-H")]
    [InlineData("--compact")]
    [InlineData("--var")]
    [InlineData("--env")]
    [InlineData("--env-file")]
    public void Call_still_declares(string flag)
    {
        var call = BowireCli.BuildCallCommand(EmptyConfig());
        Assert.Contains(call.Options, o =>
            string.Equals(o.Name, flag, StringComparison.Ordinal) || o.Aliases.Contains(flag));
    }

    [Fact]
    public void Hint_form_urls_survive_the_parser_untouched()
    {
        // The generator prefers `protocol@url` over --protocol. That form
        // only works because BuildCliOptions splits it with
        // BowireServerUrl.Parse; a plain Option<string> would happily bind
        // the whole thing and the CLI would try to resolve a host called
        // "grpc@localhost".
        var call = BowireCli.BuildCallCommand(EmptyConfig());
        var parsed = call.Parse(["--url", "grpc@http://localhost:5001", "fleet.Fleet/Get"]);
        Assert.Empty(parsed.Errors);
    }

    [Fact]
    public void Repeated_data_and_headers_do_not_swallow_the_positional()
    {
        // The client-streaming scenario's shape, asserted directly: three
        // -d repeats plus two -H repeats after the target.
        var call = BowireCli.BuildCallCommand(EmptyConfig());
        var parsed = call.Parse([
            "--url", "grpc@http://localhost:5001", "fleet.Fleet/Upload",
            "-d", "{\"n\":1}", "-d", "{\"n\":2}", "-d", "{\"n\":3}",
            "-H", "a: b", "-H", "c: d"
        ]);
        Assert.Empty(parsed.Errors);
        var target = (Argument<string>)call.Arguments.Single(a => a.Name == "target");
        Assert.Equal("fleet.Fleet/Upload", parsed.GetValue(target));
    }

    // ---------------- helpers ----------------

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    /// <summary>
    /// Drop the leading <c>bowire</c> / <c>call</c> tokens: the fixture
    /// stores what an operator pastes into a shell, this test parses
    /// against the <c>call</c> command object.
    /// </summary>
    private static string[] StripInvocationPrefix(IReadOnlyList<string> argv)
    {
        var skip = 0;
        if (argv.Count > skip && argv[skip] == "bowire") skip++;
        if (argv.Count > skip && argv[skip] == "call") skip++;
        return [.. argv.Skip(skip)];
    }

    private static List<GoldenScenario> LoadScenarios()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Cli", "cli-export-golden.json");
        Assert.True(File.Exists(path),
            $"cli-export-golden.json was not copied next to the test assembly (looked at {path}). "
            + "Check the <Content Include=\"Cli\\cli-export-golden.json\"> item in the test project.");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var scenarios = new List<GoldenScenario>();
        foreach (var entry in doc.RootElement.GetProperty("scenarios").EnumerateArray())
        {
            scenarios.Add(new GoldenScenario(
                entry.GetProperty("scenario").GetString() ?? "<unnamed>",
                [.. entry.GetProperty("argv").EnumerateArray().Select(a => a.GetString() ?? "")]));
        }
        return scenarios;
    }

    private sealed record GoldenScenario(string Scenario, IReadOnlyList<string> Argv);
}

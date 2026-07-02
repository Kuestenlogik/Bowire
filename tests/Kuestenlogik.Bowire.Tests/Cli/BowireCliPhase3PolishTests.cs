// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.CommandLine.Completions;
using Kuestenlogik.Bowire.App.Cli;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// Coverage for #38 — CLI Phase 3 polish:
/// <list type="bullet">
///   <item>per-option validators (<c>--port</c> range, <c>--recording</c>
///   existence, <c>--chaos</c> spec) that reject at Parse time,</item>
///   <item>static completion sources on enumerated options
///   (<c>fuzz --payloads</c>), and</item>
///   <item>the pretty parse-error renderer wired into
///   <see cref="BowireCli.RunAsync"/> (stderr-routed, stdout stays clean,
///   ANSI suppressed for captured streams).</item>
/// </list>
/// Validator assertions inspect <c>Parse(...).Errors</c> directly so no
/// server ever boots; the renderer test drives the full RunAsync entry.
/// </summary>
public sealed class BowireCliPhase3PolishTests
{
    // ---------------- --port range validator ----------------

    [Theory]
    [InlineData("70000")]
    [InlineData("0")]
    [InlineData("-1")]
    public void Mock_port_out_of_range_is_a_parse_error(string port)
    {
        var errors = ParseMock("--port", port);
        Assert.Contains(errors, e => e.Contains("port must be between 1 and 65535", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Mock_port_in_range_parses_clean()
    {
        var errors = ParseMock("--port", "6000");
        Assert.DoesNotContain(errors, e => e.Contains("port must be between", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------- --recording existence validator ----------------

    [Fact]
    public void Mock_recording_missing_file_is_a_parse_error()
    {
        var errors = ParseMock("--recording", "does-not-exist-12345.bwr");
        Assert.Contains(errors, e => e.Contains("file not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Mock_recording_existing_file_parses_clean()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bwr");
        File.WriteAllText(path, "{}");
        try
        {
            var errors = ParseMock("--recording", path);
            Assert.DoesNotContain(errors, e => e.Contains("file not found", StringComparison.OrdinalIgnoreCase));
        }
        finally { File.Delete(path); }
    }

    // ---------------- --chaos spec validator ----------------

    [Theory]
    [InlineData("bogus")]                 // no key:value
    [InlineData("fail-rate:5")]           // out of 0..1
    [InlineData("wobble:1")]              // unknown key
    public void Mock_chaos_bad_spec_is_a_parse_error(string spec)
    {
        var errors = ParseMock("--chaos", spec);
        Assert.Contains(errors, e => e.Contains("--chaos", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Mock_chaos_valid_spec_parses_clean()
    {
        var errors = ParseMock("--chaos", "latency:100-500,fail-rate:0.05");
        Assert.DoesNotContain(errors, e => e.Contains("--chaos", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------- completion sources ----------------

    [Fact]
    public void Fuzz_payloads_offers_category_completions()
    {
        var fuzz = BowireCli.BuildFuzzCommand();
        var payloads = fuzz.Options.Single(o => o.Name == "--payloads");
        var labels = payloads.GetCompletions(CompletionContext.Empty).Select(c => c.Label).ToList();
        Assert.Contains("sqli", labels);
        Assert.Contains("xss", labels);
        Assert.Contains("pathtrav", labels);
        Assert.Contains("cmdinj", labels);
    }

    // ---------------- pretty error rendering via RunAsync ----------------

    [Fact]
    public async Task RunAsync_parse_error_routes_to_stderr_and_leaves_stdout_clean()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        string[] args = ["mock", "--port", "70000"];
        var rc = await BowireCli.RunAsync(args, EmptyConfig(), pluginDir: "", stdout, stderr);

        Assert.Equal(1, rc);
        Assert.Equal(string.Empty, stdout.ToString());
        var err = stderr.ToString();
        Assert.Contains("port must be between 1 and 65535", err, StringComparison.OrdinalIgnoreCase);
        // Hint targets the resolved sub-command, not the bare root.
        Assert.Contains("mock --help", err, StringComparison.OrdinalIgnoreCase);
        // A captured (non-TTY) writer must never receive ANSI escapes.
        Assert.DoesNotContain('\x1b', err);
    }

    // ---------------- CliErrorRenderer unit ----------------

    [Fact]
    public void Renderer_marks_errors_and_emits_help_hint_without_color()
    {
        var root = new RootCommand("bowire");
        var required = new Option<string>("--needed") { Required = true };
        root.Options.Add(required);
        var parse = root.Parse(Array.Empty<string>());

        using var sw = new StringWriter();
        var rc = CliErrorRenderer.Render(parse, sw, useColor: false);

        var text = sw.ToString();
        Assert.Equal(1, rc);
        Assert.Contains("✗", text);
        Assert.Contains("required", text, StringComparison.OrdinalIgnoreCase);
        // Root name defaults to the host executable; assert only the hint shape.
        Assert.Contains("--help' for usage.", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\x1b', text);
    }

    [Fact]
    public void Renderer_colorizes_when_asked()
    {
        var root = new RootCommand("bowire");
        var required = new Option<string>("--needed") { Required = true };
        root.Options.Add(required);
        var parse = root.Parse(Array.Empty<string>());

        using var sw = new StringWriter();
        CliErrorRenderer.Render(parse, sw, useColor: true);

        Assert.Contains('\x1b', sw.ToString());
    }

    [Fact]
    public void Renderer_help_hint_targets_the_resolved_subcommand()
    {
        var root = new RootCommand("bowire");
        var sub = new Command("mock", "m");
        sub.Options.Add(new Option<string>("--needed") { Required = true });
        root.Subcommands.Add(sub);
        string[] args = ["mock"];
        var parse = root.Parse(args);

        using var sw = new StringWriter();
        CliErrorRenderer.Render(parse, sw, useColor: false);

        // The hint drills into the resolved sub-command (root name varies
        // with the host exe, so anchor on the 'mock --help' tail).
        Assert.Contains("mock --help", sw.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- helpers ----------------

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    private static List<string> ParseMock(params string[] args)
    {
        var mock = BowireCli.BuildMockCommand(EmptyConfig());
        return mock.Parse(args).Errors.Select(e => e.Message).ToList();
    }
}

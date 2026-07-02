// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Kuestenlogik.Bowire.App.Cli;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// Behavioural coverage for #211 — the positional <c>bowire mock foo.bwr</c>
/// shape. The option form <c>--recording foo.bwr</c> stays as it was;
/// these tests pin the new positional + the mutex it carries against
/// <c>--recording</c> / <c>--schema</c> / <c>--grpc-schema</c> / <c>--graphql-schema</c>.
///
/// Doesn't boot a real mock server — the action handler validates the
/// argument set and either passes through to MockCommand.RunAsync
/// (which would then try to open the recording) or rejects with a
/// usage error first. The tests assert exit code <c>64</c> + the
/// stderr copy that operators see; the happy path lands in the
/// integration suite where a real Kestrel binds.
/// </summary>
public sealed class MockPositionalRecordingTests
{
    // sysexits-style — same EX_USAGE value the workspace + recording
    // commands surface for argument-shape failures. CI shells branch
    // on this directly rather than scraping stderr.
    private const int ExitUsage = 64;

    [Fact]
    public void Build_advertises_positional_path_argument_for_recording_shorthand()
    {
        var mock = BowireCli.BuildMockCommand(EmptyConfig());
        Assert.Equal("mock", mock.Name);
        var positional = mock.Arguments.SingleOrDefault(a => a.Name == "path");
        Assert.NotNull(positional);
        Assert.Contains(".bwr", positional!.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("docs/recordings/bwr-format.md", positional.Description, StringComparison.OrdinalIgnoreCase);
        // Optional — the option form (--recording) stays valid, so the
        // positional must be ZeroOrOne, not exactly one.
        Assert.Equal(ArgumentArity.ZeroOrOne, positional.Arity);
    }

    [Fact]
    public async Task Positional_and_option_recording_together_rejected_with_usage_exit()
    {
        // #38 — the recording path is now existence-validated at Parse
        // time, so both inputs must point at real files to get *past*
        // validation and reach the shape mutex we're actually asserting.
        using var a = new TempFile();
        using var b = new TempFile();
        var (rc, _, stderr) = await Invoke(a.Path, "--recording", b.Path);
        Assert.Equal(ExitUsage, rc);
        // Concrete copy lock — operators searching `bowire mock --help`
        // expect this exact phrasing when they hit the mutex.
        Assert.Contains("EITHER as the positional argument OR via --recording", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--schema",         "openapi.yml")]
    [InlineData("--grpc-schema",    "set.pb")]
    [InlineData("--graphql-schema", "sdl.graphql")]
    public async Task Positional_with_schema_flag_rejected_with_usage_exit(string flag, string flagPath)
    {
        // Positional must exist (#38 existence validation); the schema
        // flag isn't existence-checked, so a bare name is fine — the
        // shape mutex is what should reject the combination.
        using var rec = new TempFile();
        var (rc, _, stderr) = await Invoke(rec.Path, flag, flagPath);
        Assert.Equal(ExitUsage, rc);
        Assert.Contains("pick one input", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bare_option_form_without_positional_still_works()
    {
        // No positional, just --recording with a nonexistent file. Post-#38
        // this trips the existence validator (rc 1) rather than reaching
        // RunAsync — either way the key back-compat guarantee holds: the
        // *positional* usage mutex must NOT be what fires for a single
        // --recording input.
        var (rc, _, stderr) = await Invoke("--recording", "nope.bwr");
        Assert.NotEqual(ExitUsage, rc);
        Assert.DoesNotContain("positional argument", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Positional_alone_is_routed_to_RecordingPath()
    {
        // Same guarantee from the other direction — a lone positional must
        // not trip either shape-mutex message. (A nonexistent path now
        // trips the existence validator; the 64 mutex path stays reserved
        // for genuine argument-shape conflicts.)
        var (rc, _, stderr) = await Invoke("nope.bwr");
        Assert.NotEqual(ExitUsage, rc);
        Assert.DoesNotContain("EITHER as the positional", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pick one input", stderr, StringComparison.OrdinalIgnoreCase);
    }

    // ----------------------------------------------------------------

    /// <summary>Self-deleting temp file so existence-validated paths resolve.</summary>
    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName() + ".bwr");

        public TempFile() => File.WriteAllText(Path, "{}");

        public void Dispose()
        {
            try { File.Delete(Path); } catch (IOException) { /* best-effort */ }
        }
    }

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    private static async Task<(int rc, string stdout, string stderr)> Invoke(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var mock = BowireCli.BuildMockCommand(EmptyConfig());
        var parse = mock.Parse(args);
        var rc = await parse.InvokeAsync(new InvocationConfiguration
        {
            Output = stdout,
            Error = stderr,
        }, TestContext.Current.CancellationToken);
        return (rc, stdout.ToString(), stderr.ToString());
    }
}

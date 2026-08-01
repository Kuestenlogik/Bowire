// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Microsoft.Extensions.Configuration;
using Kuestenlogik.Bowire.Tests.Plugins;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Smoke tests for <see cref="BowireCli.RunAsync"/>'s parser surface.
/// The handlers behind each subcommand spin up real servers / network
/// calls so we can't exercise them as units; instead we drive the
/// entry point with --help / unknown-subcommand inputs that
/// System.CommandLine resolves locally without dispatching to a
/// handler. The result confirms BuildRoot wires every documented
/// subcommand at the correct nesting level.
/// </summary>
// No [Collection] needed any more — BowireCli.RunAsync takes
// stdout/stderr TextWriter parameters that flow into
// InvocationConfiguration, so System.CommandLine's help output goes
// straight into the test's StringWriter without touching Console.Out.
public sealed class BowireCliTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().Build();

    [Fact]
    public async Task RunAsync_RootHelp_PrintsAndReturnsZero()
    {
        using var sw = new StringWriter();
        var rc = await BowireCli.RunAsync(["--help"], EmptyConfig(), plugins: TestPluginLoaders.None(),
            stdout: sw, stderr: TextWriter.Null);
        Assert.Equal(0, rc);
        var output = sw.ToString();
        // Every subcommand should appear in the help blob.
        Assert.Contains("list", output, StringComparison.Ordinal);
        Assert.Contains("describe", output, StringComparison.Ordinal);
        Assert.Contains("call", output, StringComparison.Ordinal);
        Assert.Contains("mock", output, StringComparison.Ordinal);
        Assert.Contains("mcp", output, StringComparison.Ordinal);
        Assert.Contains("plugin", output, StringComparison.Ordinal);
        Assert.Contains("test", output, StringComparison.Ordinal);
        // scan ships as an IBowireCliCommand contribution from
        // Kuestenlogik.Bowire.Security.Scanner — assert it lands
        // in the root help blob so the auto-discovery + Scanner-
        // assembly force-load stay wired. The 1.5.1 release went
        // out specifically to repair this path after a previous
        // refactor lost the eager assembly reference.
        Assert.Contains("scan", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("describe")]
    [InlineData("call")]
    [InlineData("mock")]
    [InlineData("plugin")]
    [InlineData("test")]
    [InlineData("scan")]
    public async Task RunAsync_SubcommandHelp_PrintsAndReturnsZero(string subcommand)
    {
        using var sw = new StringWriter();
        var rc = await BowireCli.RunAsync([subcommand, "--help"], EmptyConfig(), plugins: TestPluginLoaders.None(),
            stdout: sw, stderr: TextWriter.Null);
        Assert.Equal(0, rc);
        Assert.NotEmpty(sw.ToString());
    }

    [Fact]
    public async Task RunAsync_TestHelp_ListsEnvFileAndVarsAlias()
    {
        // #181 — the CI-runner flag surface: --env-file and the --vars
        // alias for --env must be wired on the test subcommand.
        using var sw = new StringWriter();
        var rc = await BowireCli.RunAsync(["test", "--help"], EmptyConfig(), plugins: TestPluginLoaders.None(),
            stdout: sw, stderr: TextWriter.Null);
        Assert.Equal(0, rc);
        var output = sw.ToString();
        Assert.Contains("--env-file", output, StringComparison.Ordinal);
        Assert.Contains("--vars", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_McpServe_Help_PrintsAndReturnsZero()
    {
        // mcp is a parent command — its concrete handler lives on the
        // serve subcommand. Help on either should be a no-op exit.
        using var sw = new StringWriter();
        var rc = await BowireCli.RunAsync(
            ["mcp", "serve", "--help"], EmptyConfig(), plugins: TestPluginLoaders.None(),
            stdout: sw, stderr: TextWriter.Null);
        Assert.Equal(0, rc);
        Assert.Contains("--bind", sw.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("download")]
    [InlineData("list")]
    [InlineData("uninstall")]
    [InlineData("update")]
    [InlineData("inspect")]
    public async Task RunAsync_PluginSubcommand_Help_PrintsAndReturnsZero(string sub)
    {
        using var sw = new StringWriter();
        var rc = await BowireCli.RunAsync(
            ["plugin", sub, "--help"], EmptyConfig(), plugins: TestPluginLoaders.None(),
            stdout: sw, stderr: TextWriter.Null);
        Assert.Equal(0, rc);
        Assert.NotEmpty(sw.ToString());
    }

    // ---- #537: catalogue verb + --catalogue-* root flags -------------

    [Fact]
    public async Task RunAsync_RootHelp_ListsCatalogueVerbAndFlags()
    {
        using var sw = new StringWriter();
        var rc = await BowireCli.RunAsync(["--help"], EmptyConfig(), plugins: TestPluginLoaders.None(),
            stdout: sw, stderr: TextWriter.Null);
        Assert.Equal(0, rc);
        var output = sw.ToString();
        Assert.Contains("catalogue", output, StringComparison.Ordinal);
        // The flags must actually be declared on the root command, not
        // only switch-mapped in BrowserUiHost: BowireCli.RunAsync fails
        // the run on any parse error, so an unregistered flag would make
        // `bowire --catalogue-provider local` a hard error rather than a
        // working option.
        Assert.Contains("--catalogue-provider", output, StringComparison.Ordinal);
        Assert.Contains("--catalogue-path", output, StringComparison.Ordinal);
        Assert.Contains("--catalogue-url", output, StringComparison.Ordinal);
        Assert.Contains("--catalogue-consul", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("providers")]
    [InlineData("use")]
    [InlineData("clear")]
    public async Task RunAsync_CatalogueSubcommand_Help_PrintsAndReturnsZero(string sub)
    {
        using var sw = new StringWriter();
        var rc = await BowireCli.RunAsync(
            ["catalogue", sub, "--help"], EmptyConfig(), plugins: TestPluginLoaders.None(),
            stdout: sw, stderr: TextWriter.Null);
        Assert.Equal(0, rc);
        Assert.NotEmpty(sw.ToString());
    }

    [Fact]
    public async Task RunAsync_CatalogueRootFlag_ParsesWithoutError()
    {
        // Parse-only: --help short-circuits before the root action would
        // try to bind a Kestrel port.
        using var sw = new StringWriter();
        using var err = new StringWriter();
        var rc = await BowireCli.RunAsync(
            ["--catalogue-provider", "local", "--catalogue-path", "/tmp/x.json", "--help"],
            EmptyConfig(), plugins: TestPluginLoaders.None(), stdout: sw, stderr: err);
        Assert.Equal(0, rc);
        Assert.Empty(err.ToString());
    }
}

/// <summary>
/// #537 — behavioural coverage for the <c>bowire catalogue</c> verb.
/// Separate from <see cref="BowireCliTests"/> because these drive the
/// command bodies rather than the parser, and the ones that exercise
/// <c>use</c> / <c>clear</c> redirect the persisted-override path via
/// the process-global <c>BOWIRE_CATALOGUE_CONFIG_PATH</c> env var —
/// which is exactly what the CwdSerialised collection exists to
/// serialise.
/// </summary>
[Collection("CwdSerialised")]
public sealed class CatalogueCommandTests : IDisposable
{
    private readonly string? _previousConfigEnv;
    private readonly string _configPath;
    private readonly string _cataloguePath;

    public CatalogueCommandTests()
    {
        _previousConfigEnv = Environment.GetEnvironmentVariable("BOWIRE_CATALOGUE_CONFIG_PATH");
        _configPath = Path.Combine(Path.GetTempPath(), $"bowire-cat-cfg-{Guid.NewGuid():N}.json");
        _cataloguePath = Path.Combine(Path.GetTempPath(), $"bowire-cat-{Guid.NewGuid():N}.json");
        Environment.SetEnvironmentVariable("BOWIRE_CATALOGUE_CONFIG_PATH", _configPath);
        File.WriteAllText(_cataloguePath, """
            {
              "version": 1,
              "entries": [
                { "url": "http://localhost:5183/graphql", "name": "Shop", "protocols": ["graphql"], "tags": ["env:dev"] },
                { "url": "grpcweb@http://localhost:5184", "name": "Ledger" }
              ]
            }
            """);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BOWIRE_CATALOGUE_CONFIG_PATH", _previousConfigEnv);
        try { if (File.Exists(_configPath)) File.Delete(_configPath); } catch { /* best-effort */ }
        try { if (File.Exists(_cataloguePath)) File.Delete(_cataloguePath); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task List_with_explicit_local_path_prints_the_composed_urls()
    {
        using var sw = new StringWriter();
        var rc = await CatalogueCommand.RunListAsync(
            provider: "local", path: _cataloguePath, url: null, consul: null, json: false,
            sw, TextWriter.Null, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        var output = sw.ToString();
        // The protocol hint is folded in, exactly as the workbench folds
        // it — `bowire catalogue list` prints the strings that end up in
        // the Sources rail, not the raw document.
        Assert.Contains("graphql@http://localhost:5183/graphql", output, StringComparison.Ordinal);
        // …and an entry that already carried its own hint is untouched.
        Assert.Contains("grpcweb@http://localhost:5184", output, StringComparison.Ordinal);
        Assert.DoesNotContain("grpc@grpcweb@", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_json_emits_the_raw_snapshot()
    {
        using var sw = new StringWriter();
        var rc = await CatalogueCommand.RunListAsync(
            provider: null, path: _cataloguePath, url: null, consul: null, json: true,
            sw, TextWriter.Null, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        using var doc = System.Text.Json.JsonDocument.Parse(sw.ToString());
        // --path alone is unambiguous, so the provider id is inferred.
        Assert.Equal("local", doc.RootElement.GetProperty("providerId").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task List_without_any_provider_exits_1()
    {
        // No flags and no persisted override — a CI step can gate on the
        // non-zero exit instead of parsing the message.
        using var sw = new StringWriter();
        using var err = new StringWriter();
        var rc = await CatalogueCommand.RunListAsync(
            provider: null, path: null, url: null, consul: null, json: false,
            sw, err, TestContext.Current.CancellationToken);

        Assert.Equal(1, rc);
        Assert.Contains("no catalogue provider configured", err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_with_an_unloaded_provider_exits_78_and_names_the_package()
    {
        using var sw = new StringWriter();
        using var err = new StringWriter();
        var rc = await CatalogueCommand.RunListAsync(
            provider: "kubernetes", path: null, url: null, consul: null, json: false,
            sw, err, TestContext.Current.CancellationToken);

        Assert.Equal(78, rc);
        Assert.Contains("Kuestenlogik.Bowire.Catalogue.Kubernetes", err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Use_persists_the_override_and_clear_removes_it()
    {
        using var sw = new StringWriter();
        var rc = CatalogueCommand.RunUse("local", _cataloguePath, null, null, null, sw, TextWriter.Null);
        Assert.Equal(0, rc);
        Assert.True(File.Exists(_configPath));
        Assert.Contains("\"provider\": \"local\"", File.ReadAllText(_configPath), StringComparison.Ordinal);

        using var sw2 = new StringWriter();
        Assert.Equal(0, CatalogueCommand.RunClear(sw2, TextWriter.Null));
        Assert.False(File.Exists(_configPath));
        Assert.Contains("Cleared", sw2.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_with_no_flags_reads_the_persisted_override()
    {
        // The contract that makes `bowire catalogue list` show what the
        // workbench shows: with nothing on the command line, resolution
        // goes through the same persisted override the host hydrates.
        Assert.Equal(0, CatalogueCommand.RunUse(
            "local", _cataloguePath, null, null, null, TextWriter.Null, TextWriter.Null));

        using var sw = new StringWriter();
        var rc = await CatalogueCommand.RunListAsync(
            provider: null, path: null, url: null, consul: null, json: true,
            sw, TextWriter.Null, TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        using var doc = System.Text.Json.JsonDocument.Parse(sw.ToString());
        Assert.Equal(2, doc.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public void Clear_without_a_stored_override_is_a_no_op_success()
    {
        using var sw = new StringWriter();
        Assert.Equal(0, CatalogueCommand.RunClear(sw, TextWriter.Null));
        Assert.Contains("nothing to clear", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Use_with_an_unloaded_provider_exits_78()
    {
        using var err = new StringWriter();
        var rc = CatalogueCommand.RunUse("agent", null, null, null, null, TextWriter.Null, err);
        Assert.Equal(78, rc);
        Assert.Contains("Kuestenlogik.Bowire.Catalogue.Agent", err.ToString(), StringComparison.Ordinal);
        // Nothing was persisted — a rejected id must not leave a
        // half-applied override behind.
        Assert.False(File.Exists(_configPath));
    }

    [Fact]
    public void Providers_lists_the_core_three_and_names_the_missing_packages()
    {
        using var sw = new StringWriter();
        Assert.Equal(0, CatalogueCommand.RunProviders(json: false, sw, TextWriter.Null));
        var output = sw.ToString();
        Assert.Contains("local", output, StringComparison.Ordinal);
        Assert.Contains("http", output, StringComparison.Ordinal);
        Assert.Contains("consul", output, StringComparison.Ordinal);
        Assert.Contains("bowire plugin install Kuestenlogik.Bowire.Catalogue.Kubernetes",
            output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://h:1/graphql", new[] { "graphql" }, "graphql@http://h:1/graphql")]
    [InlineData("grpcweb@http://h:1", new[] { "grpc" }, "grpcweb@http://h:1")]
    [InlineData("https://h/p", null, "https://h/p")]
    [InlineData("https://h/p", new string[0], "https://h/p")]
    public void ComposeEntryUrl_matches_the_workbench_composition(
        string url, string[]? protocols, string expected)
    {
        // Same rule as catalogueEntryUrl in wwwroot/js/catalogue.js and
        // scripts/ci/smoke-samples.mjs. Three copies of this logic exist
        // for three runtimes; this test is what keeps them honest.
        var entry = new Kuestenlogik.Bowire.Sources.BowireCatalogueEntry(url, Protocols: protocols);
        Assert.Equal(expected, CatalogueCommand.ComposeEntryUrl(entry));
    }
}

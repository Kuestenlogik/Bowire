// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Cli;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// Coverage for the assembly-scan CLI-command registry. The Scanner
/// project's ScanCliCommand is the pilot consumer — these tests pin
/// the discovery contract so future plugin contributors get reliable
/// behaviour.
/// </summary>
public sealed class BowireCliCommandRegistryTests
{
    [Fact]
    public void Discover_finds_scan_command_from_security_scanner_assembly()
    {
        var registry = BowireCliCommandRegistry.Discover();
        var scan = registry.Commands.SingleOrDefault(c => c.Id == "scan");
        Assert.NotNull(scan);
        // Built command should be valid (name matches subcommand verb).
        var built = scan!.Build();
        Assert.Equal("scan", built.Name);
    }

    [Fact]
    public void Discover_honours_disabled_command_ids()
    {
        // Even though ScanCliCommand exists in the loaded SecurityScanner
        // assembly, passing "scan" to disabledCommandIds should suppress
        // it. Mirrors --disable-cli-command on the CLI.
        var registry = BowireCliCommandRegistry.Discover(
            disabledCommandIds: ["scan"]);
        Assert.DoesNotContain(registry.Commands, c => c.Id == "scan");
    }

    [Fact]
    public void Discover_disabled_match_is_case_insensitive()
    {
        // CLI flag values arrive in whatever case the user typed —
        // the registry's HashSet comparer needs to absorb that.
        var registry = BowireCliCommandRegistry.Discover(
            disabledCommandIds: ["SCAN"]);
        Assert.DoesNotContain(registry.Commands, c => c.Id == "scan");
    }

    [Fact]
    public void Register_allows_manual_addition_for_tests()
    {
        var registry = new BowireCliCommandRegistry();
        Assert.Empty(registry.Commands);
        registry.Register(new FakeCliCommand());
        Assert.Single(registry.Commands);
        Assert.Equal("fake", registry.Commands[0].Id);
    }

    private sealed class FakeCliCommand : IBowireCliCommand
    {
        public string Id => "fake";
        public System.CommandLine.Command Build() => new("fake", "test command");
    }
}

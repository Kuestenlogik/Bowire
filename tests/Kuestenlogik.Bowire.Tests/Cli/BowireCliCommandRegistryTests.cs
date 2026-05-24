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
        // Force the Scanner assembly to load. Discover() walks
        // AppDomain.CurrentDomain.GetAssemblies() which only sees
        // assemblies whose types running code has already touched
        // — a ProjectReference puts the DLL in bin/ but does not
        // by itself trigger CLR load. Without this poke, CI test
        // ordering can leave the assembly cold and the assertion
        // below fails (caught between v1.5.0 → v1.5.1, then again
        // after the cli-tools section refactor).
        //
        // typeof() alone can be elided by the JIT in Release builds.
        // Calling .Assembly forces the runtime to materialise the
        // type's metadata, which guarantees the host assembly ends
        // up on AppDomain.CurrentDomain.GetAssemblies(). The
        // Assert.NotNull on the captured reference doubles as a
        // belt-and-braces use site so the optimiser can't tear it
        // down.
        var scannerAssembly = typeof(Kuestenlogik.Bowire.Security.Scanner.Cli.ScanCliCommand).Assembly;
        Assert.NotNull(scannerAssembly);

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
        // Even though ScanCliCommand exists in the loaded Security.Scanner
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

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Contracts;

namespace Kuestenlogik.Bowire.Contracts.Tests;

/// <summary>
/// #364 — the on-disk result store that feeds the matrix. `contract verify`
/// writes here, the workbench endpoint reads: the seam that lets the grid
/// render without the workbench itself calling out to a provider.
/// </summary>
public sealed class ContractResultStoreTests : IDisposable
{
    private readonly string _root;

    public ContractResultStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "bowire-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task LoadAll_MissingDirectory_YieldsEmptyNotAnError()
    {
        // An operator who never ran a verification sees an empty matrix.
        var reports = await ContractResultStore.LoadAllAsync(
            Path.Combine(_root, "never-used"), TestContext.Current.CancellationToken);

        Assert.Empty(reports);
    }

    [Fact]
    public async Task Save_ThenLoad_RoundTripsTheReport()
    {
        var report = Report("web", "orders", passed: true);

        var path = await ContractResultStore.SaveAsync(report, _root, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(path));

        var loaded = await ContractResultStore.LoadAllAsync(_root, TestContext.Current.CancellationToken);

        var round = Assert.Single(loaded);
        Assert.Equal("web", round.Consumer);
        Assert.Equal("orders", round.Provider);
        Assert.Equal(report.StartedAt, round.StartedAt);
        Assert.True(round.Passed);
        var interaction = Assert.Single(round.Interactions);
        Assert.Equal("GET /thing", interaction.Description);
        Assert.Single(interaction.Assertions); // collections survive the trip
    }

    [Fact]
    public async Task Save_SamePairTwice_OverwritesRatherThanAccumulates()
    {
        // One file per pair: a re-run replaces the previous verdict, so the
        // matrix can't drift into showing a stale duplicate.
        await ContractResultStore.SaveAsync(
            Report("web", "orders", passed: false), _root, TestContext.Current.CancellationToken);
        await ContractResultStore.SaveAsync(
            Report("web", "orders", passed: true), _root, TestContext.Current.CancellationToken);

        var loaded = await ContractResultStore.LoadAllAsync(_root, TestContext.Current.CancellationToken);

        var only = Assert.Single(loaded);
        Assert.True(only.Passed);
    }

    [Fact]
    public async Task LoadAll_SkipsMalformedFileAndKeepsTheRest()
    {
        // One corrupt file must not blank the whole grid.
        await ContractResultStore.SaveAsync(
            Report("web", "orders", passed: true), _root, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(ContractResultStore.ResolveDirectory(_root), "broken.json"),
            "{ this is not json",
            TestContext.Current.CancellationToken);

        var loaded = await ContractResultStore.LoadAllAsync(_root, TestContext.Current.CancellationToken);

        Assert.Single(loaded);
    }

    [Fact]
    public void FileNameFor_SanitisesPathHostileNames()
    {
        // Party names come from contracts, not from us — a slash must not
        // escape the results directory.
        var name = ContractResultStore.FileNameFor("web/ui", "orders:v2");

        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.EndsWith(".json", name, StringComparison.Ordinal);
        Assert.Equal(name, Path.GetFileName(name));
    }

    [Fact]
    public async Task Save_PathHostileNames_StillRoundTrips()
    {
        var report = Report("web/ui", "orders:v2", passed: true);

        await ContractResultStore.SaveAsync(report, _root, TestContext.Current.CancellationToken);
        var loaded = await ContractResultStore.LoadAllAsync(_root, TestContext.Current.CancellationToken);

        var round = Assert.Single(loaded);
        Assert.Equal("web/ui", round.Consumer);   // sanitising touches the file name, not the data
        Assert.Equal("orders:v2", round.Provider);
    }

    private static ContractVerificationReport Report(string consumer, string provider, bool passed)
    {
        var report = new ContractVerificationReport
        {
            Consumer = consumer,
            Provider = provider,
            StartedAt = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            TotalAssertions = 1,
            PassedAssertions = passed ? 1 : 0,
            FailedInteractions = passed ? 0 : 1,
        };
        var interaction = new ContractInteractionResult
        {
            Description = "GET /thing",
            Method = "GET",
            Status = passed ? "200" : "500",
        };
        interaction.Assertions.Add(new ContractAssertion
        {
            Path = "status",
            Op = "eq",
            Expected = "200",
            ActualText = passed ? "200" : "500",
            Passed = passed,
        });
        report.Interactions.Add(interaction);
        return report;
    }
}

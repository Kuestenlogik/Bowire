// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Benchmarking;

namespace Kuestenlogik.Bowire.Benchmarking.Tests;

/// <summary>
/// #232 — the schedule model: cron parsing, timezone resolution and the
/// next-occurrence arithmetic the scheduler fires on.
/// </summary>
public sealed class BowireBenchmarkScheduleTests
{
    private static BowireBenchmarkSchedule Daily(string cron = "0 3 * * *", string tz = "")
        => new() { Id = "nightly", Name = "Nightly", Cron = cron, Timezone = tz, Enabled = true };

    [Fact]
    public void NextOccurrence_IsInterpretedInTheScheduledTimezone()
    {
        // 03:00 Europe/Berlin in January is 02:00 UTC. Reading the cron in
        // the host's local zone instead would fire this at a different
        // wall-clock time on a laptop than on a CI runner.
        var schedule = Daily("0 3 * * *", "Europe/Berlin");
        var next = schedule.NextOccurrenceUtc(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 1, 15, 2, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void NextOccurrence_ShiftsWithSummerTime()
    {
        // Same schedule in July: CEST is UTC+2, so 03:00 local is 01:00 UTC.
        // This is precisely the arithmetic a hand-rolled parser gets wrong.
        var schedule = Daily("0 3 * * *", "Europe/Berlin");
        var next = schedule.NextOccurrenceUtc(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 7, 15, 1, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void NextOccurrence_DefaultsToUtcNotTheHostZone()
    {
        // An empty timezone must mean UTC, so the same stored schedule fires
        // at the same instant wherever it runs.
        var schedule = Daily("0 3 * * *");
        var next = schedule.NextOccurrenceUtc(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 1, 15, 3, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void NextOccurrence_UnknownTimezoneFallsBackToUtcRatherThanStopping()
    {
        var schedule = Daily("0 3 * * *", "Mars/Olympus_Mons");
        var next = schedule.NextOccurrenceUtc(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 1, 15, 3, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void NextOccurrence_DisabledScheduleHasNone()
    {
        var schedule = Daily();
        schedule.Enabled = false;
        Assert.Null(schedule.NextOccurrenceUtc(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void NextOccurrence_UnparseableCronYieldsNullInsteadOfThrowing()
    {
        // A bad expression in a stored file must not take down the boot pass.
        var schedule = Daily("not a cron");
        Assert.Null(schedule.NextOccurrenceUtc(DateTime.UtcNow));
        Assert.False(schedule.TryGetCronExpression(out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("*/15 * * * *")]
    [InlineData("0 0 * * 1")]
    [InlineData("30 4 1 * *")]
    public void TryGetCronExpression_AcceptsStandardFiveFieldExpressions(string cron)
    {
        Assert.True(Daily(cron).TryGetCronExpression(out var expression, out var error), error);
        Assert.NotNull(expression);
    }

    [Fact]
    public void ToRequest_CarriesTheLoadShape()
    {
        var schedule = new BowireBenchmarkSchedule
        {
            Id = "s", ServerUrl = "http://h", Service = "Svc", Method = "M",
            Body = "{}", Iterations = 200, Concurrency = 8, Warmup = 10,
        };
        var request = schedule.ToRequest();

        Assert.Equal("http://h", request.ServerUrl);
        Assert.Equal("Svc", request.Service);
        Assert.Equal("M", request.Method);
        Assert.Equal("{}", request.Body);
        Assert.Equal(200, request.Iterations);
        Assert.Equal(8, request.Concurrency);
        Assert.Equal(10, request.Warmup);
    }
}

/// <summary>
/// #232 — the store IS the restart-survival requirement: the scheduler keeps
/// no state of its own, so what survives a bounce is exactly what lands here.
/// </summary>
public sealed class BowireBenchmarkScheduleStoreTests : IDisposable
{
    private readonly string _root;
    private readonly BowireBenchmarkScheduleStore _store;

    public BowireBenchmarkScheduleStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "bowire-sched-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new BowireBenchmarkScheduleStore(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static BowireBenchmarkSchedule Schedule(string id = "nightly")
        => new()
        {
            Id = id, Name = "Nightly", Cron = "0 3 * * *", Timezone = "Europe/Berlin",
            ServerUrl = "http://h", Protocol = "rest", Service = "Svc", Method = "M",
            Iterations = 100, Concurrency = 4,
            Thresholds = { "p95 < 200" },
        };

    [Fact]
    public async Task LoadAll_MissingDirectoryIsEmptyNotAnError()
        => Assert.Empty(await new BowireBenchmarkScheduleStore(Path.Combine(_root, "nope"))
            .LoadAllAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Save_ThenLoad_SurvivesAsAFreshStoreInstance()
    {
        await _store.SaveAsync(Schedule(), TestContext.Current.CancellationToken);

        // A new store object over the same root is what a process restart
        // looks like from the scheduler's point of view.
        var afterRestart = new BowireBenchmarkScheduleStore(_root);
        var loaded = await afterRestart.LoadAllAsync(TestContext.Current.CancellationToken);

        var only = Assert.Single(loaded);
        Assert.Equal("nightly", only.Id);
        Assert.Equal("0 3 * * *", only.Cron);
        Assert.Equal("Europe/Berlin", only.Timezone);
        Assert.Equal(100, only.Iterations);
        Assert.Equal(["p95 < 200"], only.Thresholds);
    }

    [Fact]
    public async Task Save_WithoutAnId_IsRejected()
        => await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.SaveAsync(new BowireBenchmarkSchedule(), TestContext.Current.CancellationToken));

    [Fact]
    public async Task Delete_RemovesTheScheduleAndItsHistory()
    {
        await _store.SaveAsync(Schedule(), TestContext.Current.CancellationToken);
        await _store.AppendRunAsync(new BowireBenchmarkScheduleRun { ScheduleId = "nightly" },
            TestContext.Current.CancellationToken);

        Assert.True(_store.Delete("nightly"));
        Assert.Empty(await _store.LoadAllAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await _store.LoadRunsAsync("nightly", TestContext.Current.CancellationToken));
        Assert.False(_store.Delete("nightly"));
    }

    [Fact]
    public async Task AppendRun_KeepsHistoryNewestFirst()
    {
        await _store.AppendRunAsync(new BowireBenchmarkScheduleRun
        { ScheduleId = "nightly", StartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            TestContext.Current.CancellationToken);
        await _store.AppendRunAsync(new BowireBenchmarkScheduleRun
        { ScheduleId = "nightly", StartedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
            TestContext.Current.CancellationToken);

        var runs = await _store.LoadRunsAsync("nightly", TestContext.Current.CancellationToken);
        Assert.Equal(2, runs.Count);
        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), runs[0].StartedAt);
    }

    [Fact]
    public async Task AppendRun_TrimsToTheHistoryCap()
    {
        for (var i = 0; i < BowireBenchmarkScheduleStore.MaxRunsPerSchedule + 10; i++)
        {
            await _store.AppendRunAsync(new BowireBenchmarkScheduleRun
            { ScheduleId = "nightly", Count = i }, TestContext.Current.CancellationToken);
        }

        var runs = await _store.LoadRunsAsync("nightly", TestContext.Current.CancellationToken);
        Assert.Equal(BowireBenchmarkScheduleStore.MaxRunsPerSchedule, runs.Count);
    }

    [Fact]
    public async Task LoadAll_SkipsAMalformedFileAndKeepsTheRest()
    {
        await _store.SaveAsync(Schedule(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_store.Directory, "broken.json"), "{ not json",
            TestContext.Current.CancellationToken);

        Assert.Single(await _store.LoadAllAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAll_DoesNotMistakeARunHistoryForASchedule()
    {
        // The history file sits beside the schedule; picking it up as a
        // schedule would produce a phantom entry with no cron.
        await _store.SaveAsync(Schedule(), TestContext.Current.CancellationToken);
        await _store.AppendRunAsync(new BowireBenchmarkScheduleRun { ScheduleId = "nightly" },
            TestContext.Current.CancellationToken);

        var loaded = await _store.LoadAllAsync(TestContext.Current.CancellationToken);
        Assert.Single(loaded);
        Assert.Equal("nightly", loaded[0].Id);
    }

    [Theory]
    [InlineData("../escape", "escape")]
    [InlineData("a/b", "a_b")]
    [InlineData("", "unnamed")]
    public void Sanitise_KeepsIdsInsideTheDirectory(string id, string expected)
    {
        var name = BowireBenchmarkScheduleStore.Sanitise(id);
        Assert.Equal(expected, name);
        Assert.Equal(name, Path.GetFileName(name));
    }
}

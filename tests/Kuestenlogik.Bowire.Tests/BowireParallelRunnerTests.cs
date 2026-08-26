// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Parallel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// The in-process runner behind parallel sessions.
/// </summary>
/// <remarks>
/// <para>
/// Every property here is one an operator reads off a load run and acts on:
/// how the targets were split across sessions, whether the run stopped at the
/// first failure or carried on, and which environment slot a failing session
/// was using. Getting one wrong does not throw — it produces a plausible
/// report of a run that did something else.
/// </para>
/// <para>
/// The pass path needs a server, so these tests start a real one on loopback
/// with an ephemeral port. Nothing leaves the machine.
/// </para>
/// </remarks>
public sealed class BowireParallelRunnerTests : IAsyncLifetime
{
    private WebApplication? _upstream;
    private string _url = "";
    private int _requests;
    private readonly List<string?> _envHeaders = [];
    private readonly Lock _gate = new();

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Map("/ok", (HttpContext ctx) =>
        {
            lock (_gate)
            {
                _requests++;
                _envHeaders.Add(ctx.Request.Headers["X-Bowire-Env"].FirstOrDefault());
            }
            return Results.Ok(new { ok = true });
        });
        app.Map("/boom", () => Results.StatusCode(500));
        await app.StartAsync(TestContext.Current.CancellationToken);
        _upstream = app;
        _url = app.Urls.First();
    }

    public async ValueTask DisposeAsync()
    {
        if (_upstream is not null)
        {
            await _upstream.StopAsync(CancellationToken.None);
            await _upstream.DisposeAsync();
        }
    }

    private int RequestCount { get { lock (_gate) return _requests; } }
    private List<string?> EnvHeaders { get { lock (_gate) return [.. _envHeaders]; } }

    private BowireParallelTarget Ok(string? label = null)
        => new() { Url = $"{_url}/ok", Method = "GET", Label = label };

    private BowireParallelTarget Boom()
        => new() { Url = $"{_url}/boom", Method = "GET" };

    private static BowireParallelTarget NoUrl() => new() { Url = "" };

    private static Task<BowireParallelResponse> Run(BowireParallelLocalRequest request)
        => BowireParallelRunner.RunAsync(
            request, configuration: null, NullLogger.Instance, TestContext.Current.CancellationToken);

    // ---- how targets are split ----

    [Fact]
    public async Task One_Session_Walks_Every_Target_In_Order()
    {
        var result = await Run(new BowireParallelLocalRequest
        {
            SessionCount = 1,
            Targets = [Ok("a"), Ok("b"), Ok("c")],
        });

        Assert.Equal(3, result.PassCount);
        Assert.Equal(0, result.FailCount);
        Assert.Equal([0, 1, 2], result.Results.Select(r => r.TargetIndex));
    }

    [Fact]
    public async Task Sessions_Split_The_Targets_Round_Robin()
    {
        // Session k owns the indices where index % N == k — the same slice
        // rule the browser-side runner uses, so a run reports the same shape
        // whether it was driven from the UI or fanned out to a host.
        var result = await Run(new BowireParallelLocalRequest
        {
            SessionCount = 2,
            Targets = [Ok(), Ok(), Ok(), Ok()],
        });

        Assert.Equal(4, result.PassCount);
        Assert.Equal([0, 2], result.Results.Where(r => r.SessionIndex == 0).Select(r => r.TargetIndex));
        Assert.Equal([1, 3], result.Results.Where(r => r.SessionIndex == 1).Select(r => r.TargetIndex));
    }

    [Fact]
    public async Task More_Sessions_Than_Targets_Leaves_The_Extra_Ones_Idle()
    {
        // Not an error: a load profile is written once and run against lists
        // of different lengths.
        var result = await Run(new BowireParallelLocalRequest
        {
            SessionCount = 4,
            Targets = [Ok()],
        });

        Assert.Equal(1, result.PassCount);
        Assert.Equal(4, result.SessionCount);
    }

    [Fact]
    public async Task Results_Come_Back_Sorted_By_Session_Then_Target()
    {
        // Sessions finish in whatever order they finish; a report whose row
        // order changed run to run would be impossible to diff.
        var result = await Run(new BowireParallelLocalRequest
        {
            SessionCount = 3,
            Targets = [Ok(), Ok(), Ok(), Ok(), Ok(), Ok()],
        });

        var keys = result.Results.Select(r => (r.SessionIndex, r.TargetIndex)).ToList();
        Assert.Equal(keys.OrderBy(k => k.SessionIndex).ThenBy(k => k.TargetIndex), keys);
    }

    [Fact]
    public async Task A_Session_Count_Below_One_Is_Treated_As_One()
        // The wire type takes whatever a client sends.
        => Assert.Equal(1, (await Run(new BowireParallelLocalRequest
        {
            SessionCount = 0,
            Targets = [Ok()],
        })).SessionCount);

    [Fact]
    public async Task A_Run_With_No_Targets_Is_An_Empty_Report_Not_A_Failure()
    {
        var result = await Run(new BowireParallelLocalRequest { SessionCount = 2, Targets = [] });

        Assert.Equal(0, result.TargetCount);
        Assert.Empty(result.Results);
    }

    // ---- failure policy ----

    [Fact]
    public async Task A_Failing_Target_Is_Reported_And_The_Run_Carries_On()
    {
        // continueOnError is the default: a load run is about the aggregate,
        // and stopping at the first 500 would throw away the rest of the data.
        var result = await Run(new BowireParallelLocalRequest
        {
            SessionCount = 1,
            ContinueOnError = true,
            Targets = [Boom(), Ok(), Ok()],
        });

        Assert.Equal(1, result.FailCount);
        Assert.Equal(2, result.PassCount);
        Assert.Equal(3, result.Results.Count);
    }

    [Fact]
    public async Task Stopping_At_The_First_Failure_Keeps_What_Already_Finished()
    {
        // The partial result is the point: the operator needs to see how far
        // the run got, not an empty report.
        var result = await Run(new BowireParallelLocalRequest
        {
            SessionCount = 1,
            ContinueOnError = false,
            Targets = [Ok(), Boom(), Ok(), Ok()],
        });

        Assert.Equal(1, result.PassCount);
        Assert.Equal(1, result.FailCount);
        Assert.Contains(result.Sessions, s => s.Aborted == "first-failure");
    }

    [Fact]
    public async Task A_Target_With_No_Url_Fails_Without_A_Request()
    {
        // A half-filled row in the UI. It has to fail as a row rather than
        // taking the run down or being silently skipped.
        var before = RequestCount;

        var result = await Run(new BowireParallelLocalRequest
        {
            SessionCount = 1,
            Targets = [NoUrl()],
        });

        var row = Assert.Single(result.Results);
        Assert.False(row.Pass);
        Assert.Contains("Missing target URL", row.Error!, StringComparison.Ordinal);
        Assert.Equal(before, RequestCount);
    }

    // ---- environment slots ----

    [Fact]
    public async Task Each_Session_Gets_An_Env_From_The_Pool_Round_Robin()
    {
        // The env id is how an operator attributes a failure to one slot —
        // "staging-2 is the one rate-limiting us" is only sayable if the
        // assignment is stable.
        var result = await Run(new BowireParallelLocalRequest
        {
            SessionCount = 3,
            EnvPool = ["env-a", "env-b"],
            Targets = [Ok(), Ok(), Ok()],
        });

        var byIndex = result.Sessions.OrderBy(s => s.SessionIndex).Select(s => s.EnvId).ToList();
        Assert.Equal(["env-a", "env-b", "env-a"], byIndex);
    }

    [Fact]
    public async Task The_Env_Id_Is_Stamped_On_The_Upstream_Request()
    {
        // Not just reported locally: the upstream sees which slot called it.
        await Run(new BowireParallelLocalRequest
        {
            SessionCount = 1,
            EnvPool = ["env-a"],
            Targets = [Ok()],
        });

        Assert.Contains("env-a", EnvHeaders);
    }

    [Fact]
    public async Task Without_A_Pool_No_Env_Header_Is_Sent()
    {
        // An empty pool means "no env slots", not "a slot named empty".
        await Run(new BowireParallelLocalRequest { SessionCount = 1, Targets = [Ok()] });

        Assert.Contains(EnvHeaders, h => h is null);
    }

    // ---- the aggregate ----

    [Fact]
    public async Task The_Totals_Add_Up_To_The_Rows()
    {
        var result = await Run(new BowireParallelLocalRequest
        {
            SessionCount = 2,
            ContinueOnError = true,
            Targets = [Ok(), Boom(), Ok(), Boom()],
        });

        Assert.Equal(result.Results.Count, result.PassCount + result.FailCount);
        Assert.Equal(4, result.TargetCount);
        Assert.Equal(2, result.SessionCount);
    }

    [Fact]
    public async Task A_Ramp_Up_Still_Runs_Every_Target()
    {
        // Staggered starts change when sessions begin, never whether their
        // slice runs — the failure mode being ruled out is a last session
        // that starts after the run has already finished.
        var result = await Run(new BowireParallelLocalRequest
        {
            SessionCount = 2,
            RampUpSeconds = 0.2,
            Targets = [Ok(), Ok()],
        });

        Assert.Equal(2, result.PassCount);
    }
}

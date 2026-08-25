// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Benchmarking;
using Kuestenlogik.Bowire.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Benchmarking.Tests;

/// <summary>
/// The scheduling package's contributions to a host (#232) and the wire shape
/// its rail reads.
/// </summary>
/// <remarks>
/// Discovery-by-reference is the contract: a host that references this package
/// gets scheduled runs, and one that does not starts no scheduler at all. That
/// is worth holding — an unattended process that calls out to a target URL on
/// a timer should never appear because something else pulled it in.
/// </remarks>
public sealed class BowireBenchmarkSchedulingContributionsTests
{
    private static BowireBenchmarkSchedule Schedule(Action<BowireBenchmarkSchedule>? tweak = null)
    {
        var s = new BowireBenchmarkSchedule
        {
            Id = "sched-1",
            Name = "Nightly orders",
            Cron = "0 2 * * *",
            ServerUrl = "https://orders.example.com",
            Service = "orders.v1.OrderService",
            Method = "GetOrder",
            Iterations = 100,
            Concurrency = 4,
        };
        tweak?.Invoke(s);
        return s;
    }

    private static JsonElement Wire(BowireBenchmarkSchedule schedule,
        IReadOnlyList<BowireBenchmarkScheduleRun>? runs = null, DateTime? now = null)
        => JsonSerializer.SerializeToElement(BowireBenchmarkScheduleEndpoints.ToPayload(
            schedule, runs ?? [], now ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

    // ---- service registration ----

    [Fact]
    public void The_Service_Contribution_Registers_The_Store_Resolver_And_Scheduler()
    {
        var services = new ServiceCollection();
        new BowireBenchmarkSchedulingServiceContribution().ConfigureServices(services);

        var descriptors = services.ToList();
        Assert.Contains(descriptors, d => d.ServiceType == typeof(BowireBenchmarkScheduleStore));
        Assert.Contains(descriptors, d => d.ServiceType == typeof(IBowireBenchmarkProtocolResolver));
        // The hosted service is what actually fires runs — registering the
        // store without it would give a rail that lists schedules nothing runs.
        Assert.Contains(descriptors, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void The_Service_Contribution_Rejects_A_Null_Collection()
        => Assert.Throws<ArgumentNullException>(
            () => new BowireBenchmarkSchedulingServiceContribution().ConfigureServices(null!));

    [Fact]
    public void The_Endpoint_Contribution_Rejects_A_Null_Builder()
        => Assert.Throws<ArgumentNullException>(
            () => new BowireBenchmarkSchedulingEndpointContribution().MapEndpoints(null!, ""));

    // ---- protocol resolution ----

    [Fact]
    public void An_Unknown_Or_Blank_Protocol_Id_Resolves_To_Nothing()
    {
        // A schedule persisted against a plugin that has since been removed
        // must not throw on the next tick; the run is skipped instead.
        var resolver = new BowireRegistryProtocolResolver();

        Assert.Null(resolver.Resolve("no-such-protocol"));
        Assert.Null(resolver.Resolve(""));
        Assert.Null(resolver.Resolve("   "));
        Assert.Null(resolver.Resolve(null!));
    }

    // ---- the rail's wire shape ----

    [Fact]
    public void A_Schedule_Projects_Its_Target_As_One_Field()
    {
        // The rail shows one line per schedule; service and method are never
        // useful apart in that view.
        var wire = Wire(Schedule());

        Assert.Equal("sched-1", wire.GetProperty("id").GetString());
        Assert.Equal("Nightly orders", wire.GetProperty("name").GetString());
        Assert.Equal("orders.v1.OrderService/GetOrder", wire.GetProperty("target").GetString());
        Assert.Equal("https://orders.example.com", wire.GetProperty("serverUrl").GetString());
        Assert.Equal(100, wire.GetProperty("iterations").GetInt32());
        Assert.Equal(4, wire.GetProperty("concurrency").GetInt32());
    }

    [Fact]
    public void An_Unset_Timezone_Reads_As_UTC_Rather_Than_Blank()
    {
        // "" in the UI would look like a missing value the operator has to
        // fix; UTC is what the scheduler actually uses.
        Assert.Equal("UTC", Wire(Schedule(s => s.Timezone = "")).GetProperty("timezone").GetString());
        Assert.Equal("UTC", Wire(Schedule(s => s.Timezone = "   ")).GetProperty("timezone").GetString());
        Assert.Equal("Europe/Berlin",
            Wire(Schedule(s => s.Timezone = "Europe/Berlin")).GetProperty("timezone").GetString());
    }

    [Fact]
    public void A_Live_Schedule_Reports_When_It_Fires_Next()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var next = Wire(Schedule(), now: now).GetProperty("nextRun");

        Assert.Equal(JsonValueKind.String, next.ValueKind);
        Assert.True(next.GetDateTime() > now);
    }

    [Fact]
    public void A_Paused_Or_Unparseable_Schedule_Reports_No_Next_Run()
    {
        // Null rather than a made-up time: the UI says "paused" or "invalid",
        // and inventing a timestamp would make a broken cron look healthy.
        Assert.Equal(JsonValueKind.Null,
            Wire(Schedule(s => s.Enabled = false)).GetProperty("nextRun").ValueKind);
        Assert.Equal(JsonValueKind.Null,
            Wire(Schedule(s => s.Cron = "not a cron")).GetProperty("nextRun").ValueKind);
    }

    [Fact]
    public void A_Schedule_That_Never_Ran_Carries_No_Last_Run()
    {
        var wire = Wire(Schedule());

        Assert.Equal(JsonValueKind.Null, wire.GetProperty("lastRun").ValueKind);
        Assert.Equal(0, wire.GetProperty("runCount").GetInt32());
    }

    [Fact]
    public void The_Newest_Run_Rides_Along_So_The_List_Answers_Is_This_Healthy()
    {
        // Without this the rail would need a second request per row just to
        // colour it.
        var newest = new BowireBenchmarkScheduleRun
        {
            StartedAt = new DateTime(2026, 1, 2, 2, 0, 0, DateTimeKind.Utc),
            TriggeredBy = "schedule",
            Count = 100, Errors = 2,
            P50 = 8, P95 = 21.5, P99 = 40, Throughput = 250.5,
            Passed = false,
            Thresholds = { new BowireBenchmarkScheduleThreshold { Spec = "p95<20ms", Actual = 21.5, Ok = false } },
        };
        var older = new BowireBenchmarkScheduleRun { StartedAt = new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc) };

        var wire = Wire(Schedule(), [newest, older]);

        Assert.Equal(2, wire.GetProperty("runCount").GetInt32());
        var last = wire.GetProperty("lastRun");
        // Index 0 is newest — the store hands them back that way.
        Assert.Equal(newest.StartedAt, last.GetProperty("startedAt").GetDateTime());
        Assert.Equal(21.5, last.GetProperty("p95").GetDouble());
        Assert.Equal(2, last.GetProperty("errors").GetInt32());
        Assert.False(last.GetProperty("passed").GetBoolean());

        var threshold = last.GetProperty("thresholds")[0];
        Assert.Equal("p95<20ms", threshold.GetProperty("spec").GetString());
        Assert.Equal(21.5, threshold.GetProperty("actual").GetDouble());
        Assert.False(threshold.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void The_Enabled_Request_Body_Carries_The_New_State()
    {
        // Round-trips through the same casing the workbench posts.
        var body = JsonSerializer.Deserialize<BowireScheduleEnabledRequest>("""{"enabled":false}""");
        Assert.False(body!.Enabled);
    }
}

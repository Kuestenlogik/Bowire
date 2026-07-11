// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mock.Management;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Gap-closing coverage for the mock-management surface: the DI
/// extension, and the #404 per-stub CRUD + #408 named-scenario methods on
/// <see cref="BowireMockHostManager"/>. The existing lifecycle tests cover
/// Start / Stop / Get / faults / request-log; these pin the stub + scenario
/// delegations — both the "mock not running" null/false guards (no server
/// boot needed) and the happy path against a live mock.
/// </summary>
public sealed class MockManagementCoverageTests
{
    private static string BuildRecording()
    {
        var rec = new BowireRecording { Id = "rec_stub", Name = "stub-test", RecordingFormatVersion = 2 };
        rec.Steps.Add(new BowireRecordingStep
        {
            Id = "step_one",
            Protocol = "rest",
            Service = "S",
            Method = "M",
            MethodType = "Unary",
            HttpPath = "/probe",
            HttpVerb = "GET",
            Status = "OK",
            Response = "ok",
        });
        return JsonSerializer.Serialize(rec);
    }

    private static BowireRecordingStep NewStub(string id, string path) => new()
    {
        Id = id,
        Protocol = "rest",
        Service = "S",
        Method = "M2",
        MethodType = "Unary",
        HttpPath = path,
        HttpVerb = "GET",
        Status = "OK",
        Response = "added",
    };

    // ----------------------------- DI extension -----------------------------

    [Fact]
    public async Task AddBowireMockManagement_registers_a_resolvable_singleton()
    {
        var services = new ServiceCollection();
        services.AddBowireMockManagement();

        // The manager is IAsyncDisposable-only, so the container must be
        // disposed asynchronously.
        await using var sp = services.BuildServiceProvider();
        var a = sp.GetRequiredService<BowireMockHostManager>();
        var b = sp.GetRequiredService<BowireMockHostManager>();
        Assert.Same(a, b);
    }

    // ------------------------ not-running null/false guards ------------------

    [Fact]
    public async Task Stub_and_scenario_methods_are_null_or_false_when_mock_not_running()
    {
        await using var manager = new BowireMockHostManager();
        const string unknown = "not-running";

        Assert.Null(manager.GetStubs(unknown));
        Assert.Null(manager.GetStub(unknown, "step_one"));
        Assert.Null(manager.AddStub(unknown, NewStub("x", "/x")));
        Assert.False(manager.UpdateStub(unknown, "step_one", NewStub("step_one", "/y")));
        Assert.False(manager.RemoveStub(unknown, "step_one"));
        Assert.False(manager.ResetStubs(unknown));

        Assert.Null(manager.GetScenarioStates(unknown));
        Assert.False(manager.SetScenarioState(unknown, "flow", "Started"));
        Assert.False(manager.ResetScenarios(unknown));
    }

    // ----------------------------- stub CRUD path ----------------------------

    [Fact]
    public async Task Stub_crud_round_trips_against_a_running_mock()
    {
        await using var manager = new BowireMockHostManager();
        var handle = await manager.StartAsync(BuildRecording(), "rec_stub", "crud", port: 0, TestContext.Current.CancellationToken);
        var id = handle.MockId;

        // Baseline: the single recorded step is the only stub.
        var baseline = manager.GetStubs(id);
        Assert.NotNull(baseline);
        Assert.Single(baseline!);
        Assert.NotNull(manager.GetStub(id, "step_one"));

        // Add.
        var added = manager.AddStub(id, NewStub("step_two", "/added"));
        Assert.NotNull(added);
        Assert.Equal(2, manager.GetStubs(id)!.Count);

        // Update an existing stub.
        Assert.True(manager.UpdateStub(id, "step_one", NewStub("step_one", "/changed")));
        // Update a missing stub → false.
        Assert.False(manager.UpdateStub(id, "nope", NewStub("nope", "/z")));

        // Remove.
        Assert.True(manager.RemoveStub(id, "step_two"));
        Assert.False(manager.RemoveStub(id, "step_two"));

        // Reset restores the baseline recording.
        Assert.True(manager.ResetStubs(id));
        Assert.Single(manager.GetStubs(id)!);
    }

    // --------------------------- scenario-state path -------------------------

    [Fact]
    public async Task Scenario_methods_operate_on_a_running_mock()
    {
        await using var manager = new BowireMockHostManager();
        var handle = await manager.StartAsync(BuildRecording(), "rec_stub", "scenarios", port: 0, TestContext.Current.CancellationToken);
        var id = handle.MockId;

        // A recording with no scenarios still returns a (possibly empty) map
        // while running — not null.
        Assert.NotNull(manager.GetScenarioStates(id));

        // Setting an unknown scenario on a running mock is a false (handler
        // reached, scenario not found) — distinct from the not-running false.
        Assert.False(manager.SetScenarioState(id, "unknown-scenario", "Started"));

        // Reset is a no-op success while running.
        Assert.True(manager.ResetScenarios(id));
    }
}

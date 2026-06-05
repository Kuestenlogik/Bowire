// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for <see cref="BowireTelemetryServiceCollectionExtensions"/>
/// — the OTel SDK wire-up that powers the workbench's self-telemetry
/// seam (#29). Off-by-default semantics matter: a laptop install that
/// never sets <c>Bowire:Telemetry:Enabled=true</c> must not pay for an
/// OTel pipeline it didn't ask for.
/// </summary>
public sealed class BowireTelemetryServiceCollectionExtensionsTests
{
    [Fact]
    public void Throws_For_Null_Services()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BowireTelemetryServiceCollectionExtensions.AddBowireTelemetry(
                services: null!, new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void Throws_For_Null_Configuration()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddBowireTelemetry(configuration: null!));
    }

    [Fact]
    public void Disabled_By_Default_Does_Not_Wire_OpenTelemetry_Pipeline()
    {
        // Off-by-default discipline: an empty IConfiguration means
        // Bowire:Telemetry:Enabled is false, so no IMeterProvider /
        // TracerProvider is registered. Laptops stay quiet.
        var services = new ServiceCollection();
        var cfg = new ConfigurationBuilder().Build();

        services.AddBowireTelemetry(cfg);

        using var sp = services.BuildServiceProvider();
        Assert.Null(sp.GetService<MeterProvider>());
        Assert.Null(sp.GetService<TracerProvider>());

        // Options still bound + registered so endpoints / the dashboard
        // can introspect the current state.
        var opts = sp.GetRequiredService<BowireTelemetryOptions>();
        Assert.False(opts.Enabled);
    }

    [Fact]
    public void Enabled_Registers_Meter_And_Tracer_Providers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:Telemetry:Enabled"] = "true",
            })
            .Build();

        services.AddBowireTelemetry(cfg);

        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetService<MeterProvider>());
        Assert.NotNull(sp.GetService<TracerProvider>());
    }

    [Fact]
    public void Configure_Callback_Overrides_Bound_Options()
    {
        // configure runs after IConfiguration binding, so a host that
        // wants to force telemetry on irrespective of config can do so.
        var services = new ServiceCollection();
        services.AddLogging();
        var cfg = new ConfigurationBuilder().Build();

        services.AddBowireTelemetry(cfg, opts =>
        {
            opts.Enabled = true;
            opts.StripMethodLabels = true;
        });

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<BowireTelemetryOptions>();
        Assert.True(opts.Enabled);
        Assert.True(opts.StripMethodLabels);
        Assert.NotNull(sp.GetService<MeterProvider>());
    }

    [Fact]
    public void Enabled_With_StripMethodLabels_Wires_Pipeline_Without_Errors()
    {
        // The StripMethodLabels branch adds two extra MetricViews; we
        // can't easily assert on the views themselves without poking
        // at SDK internals, but building the provider exercises every
        // AddView call. A misconfigured view throws at build time, so
        // a successful BuildServiceProvider+resolve guards the branch.
        var services = new ServiceCollection();
        services.AddLogging();
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:Telemetry:Enabled"] = "true",
                ["Bowire:Telemetry:StripMethodLabels"] = "true",
            })
            .Build();

        services.AddBowireTelemetry(cfg);

        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetService<MeterProvider>());
    }

    [Fact]
    public void Idempotent_When_Options_Already_Registered()
    {
        // Re-calling AddBowireTelemetry must not register a second
        // BowireTelemetryOptions instance — the comment in the source
        // promises "no-op rather than registering a duplicate".
        var services = new ServiceCollection();
        services.AddLogging();
        var cfg = new ConfigurationBuilder().Build();

        services.AddBowireTelemetry(cfg);
        services.AddBowireTelemetry(cfg);

        using var sp = services.BuildServiceProvider();
        var optsList = sp.GetServices<BowireTelemetryOptions>().ToList();
        Assert.Single(optsList);
    }
}

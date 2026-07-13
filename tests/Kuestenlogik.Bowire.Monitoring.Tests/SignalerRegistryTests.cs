// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Monitoring.Tests;

/// <summary>
/// Coverage for <see cref="SignalerRegistry"/> — discovering the installed
/// signaler factories (Slack + PagerDuty are referenced by this test project)
/// and resolving a <c>--signal</c> spec, including the degrade paths.
/// </summary>
public sealed class SignalerRegistryTests
{
    [Fact]
    public void Discover_finds_the_referenced_signaler_packages()
    {
        var registry = SignalerRegistry.Discover();
        Assert.Contains("slack", registry.Schemes, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("pagerduty", registry.Schemes, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_returns_a_signaler_for_a_known_scheme()
    {
        var signaler = SignalerRegistry.Discover().Resolve("slack:https://hooks.slack.com/services/T/B/x", out var error);
        Assert.NotNull(signaler);
        Assert.Null(error);
    }

    [Fact]
    public void Resolve_unknown_scheme_reports_install_hint()
    {
        var signaler = SignalerRegistry.Discover().Resolve("teams:https://example/webhook", out var error);
        Assert.Null(signaler);
        Assert.Contains("teams", error!, StringComparison.Ordinal);
        Assert.Contains("install", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("noColon")]
    [InlineData(":emptyScheme")]
    public void Resolve_malformed_spec_reports_error(string spec)
    {
        var signaler = SignalerRegistry.Discover().Resolve(spec, out var error);
        Assert.Null(signaler);
        Assert.Contains("scheme", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_surfaces_a_factory_config_error()
    {
        // slack scheme is known, but the argument isn't a URL → config error.
        var signaler = SignalerRegistry.Discover().Resolve("slack:not-a-url", out var error);
        Assert.Null(signaler);
        Assert.Contains("webhook", error!, StringComparison.OrdinalIgnoreCase);
    }
}

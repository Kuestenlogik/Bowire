// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Testcontainers.Pulsar;

namespace Kuestenlogik.Bowire.IntegrationTests.Pulsar;

/// <summary>
/// Spins up an Apache Pulsar standalone container so the Pulsar
/// plugin's produce / subscribe round-trip suite can hit a real
/// broker. Testcontainers.Pulsar boots the broker with binary
/// protocol on container-6650 + admin REST on container-8080, but
/// both ports are exposed on the host through Testcontainers'
/// dynamic port mapping — so the host ports are RANDOM, not the
/// 6650/8080 pair <c>PulsarConnectionHelper.Resolve</c> assumes
/// when it derives the admin URL from a <c>pulsar://...</c> serverUrl.
/// Tests that exercise the admin REST path must therefore pass
/// <see cref="AdminUrl"/> (resolved from <c>GetMappedPublicPort(8080)</c>)
/// as the serverUrl rather than <see cref="BrokerUrl"/>.
/// </summary>
/// <remarks>
/// Tests using this fixture carry <c>[Trait("Category", "Docker")]</c>
/// so dev / CI hosts without a Docker daemon can opt out via
/// <c>dotnet test --filter "Category!=Docker"</c>.
/// </remarks>
public sealed class PulsarContainerFixture : IAsyncLifetime
{
    // Pin the image — Testcontainers.Pulsar 4.12 deprecated the
    // parameterless ctor; the 3.3.x line is the latest GA at the time
    // of writing and matches the Bowire.Samples/protocols/Pulsar compose pin.
    private readonly PulsarContainer _container =
        new PulsarBuilder("apachepulsar/pulsar:3.3.0").Build();

    /// <summary>The <c>pulsar://...</c> URL the plugin can use as serverUrl
    /// for produce / subscribe operations (binary protocol).</summary>
    public string BrokerUrl { get; private set; } = string.Empty;

    /// <summary>The <c>http://...</c> URL of the admin REST surface — needed
    /// by <c>DiscoverAsync</c> and any path that walks <c>/admin/v2/...</c>.
    /// Resolved from the Testcontainers-mapped port for container-8080,
    /// not derived from <see cref="BrokerUrl"/>.</summary>
    public string AdminUrl { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        BrokerUrl = _container.GetBrokerAddress();
        AdminUrl = "http://localhost:" + _container.GetMappedPublicPort(8080)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

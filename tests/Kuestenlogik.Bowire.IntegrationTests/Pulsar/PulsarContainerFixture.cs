// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Testcontainers.Pulsar;

namespace Kuestenlogik.Bowire.IntegrationTests.Pulsar;

/// <summary>
/// Spins up an Apache Pulsar standalone container so the Pulsar
/// plugin's produce / subscribe round-trip suite can hit a real
/// broker. Testcontainers.Pulsar boots the broker with the binary
/// protocol on the ephemeral-mapped 6650 + the admin REST surface on
/// 8080 — exactly the pair <c>PulsarConnectionHelper.Resolve</c>
/// expects.
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
    // of writing and matches the samples/Pulsar compose pin.
    private readonly PulsarContainer _container =
        new PulsarBuilder("apachepulsar/pulsar:3.3.0").Build();

    /// <summary>The <c>pulsar://...</c> URL the plugin can use as serverUrl.</summary>
    public string BrokerUrl { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        BrokerUrl = _container.GetBrokerAddress();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

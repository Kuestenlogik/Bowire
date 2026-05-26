// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Testcontainers.Nats;

namespace Kuestenlogik.Bowire.IntegrationTests.Nats;

/// <summary>
/// Spins up a <c>nats:2-alpine</c> container with JetStream enabled
/// (<c>-js</c>) so the NATS plugin's Phase-2 round-trip suite can
/// exercise the JetStream + Services discovery paths against a real
/// broker.
/// </summary>
/// <remarks>
/// <para>
/// Test classes wear <c>IClassFixture&lt;NatsContainerFixture&gt;</c>
/// and read <see cref="ServerUrl"/> off the fixture instead of
/// hard-coding ports. Testcontainers picks an ephemeral host port
/// so parallel suite runs (or a leftover broker from a crashed
/// previous run) don't collide on 4222.
/// </para>
/// <para>
/// Tests using this fixture carry <c>[Trait("Category", "Docker")]</c>
/// so CI / dev runs without a Docker daemon can opt out via
/// <c>dotnet test --filter "Category!=Docker"</c>.
/// </para>
/// </remarks>
public sealed class NatsContainerFixture : IAsyncLifetime
{
    private readonly NatsContainer _container = new NatsBuilder("nats:2-alpine")
        // Override the default command so the broker boots with
        // JetStream on. Testcontainers.Nats' default starts plain
        // core-only nats-server which would make every JetStream
        // path return 503. -m 8222 exposes the monitoring endpoint
        // (handy for ad-hoc debugging) but is otherwise unused by
        // the suite.
        .WithCommand("-js", "-m", "8222")
        .Build();

    /// <summary>The <c>nats://...</c> URL the plugin can use as serverUrl.</summary>
    public string ServerUrl { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        // Testcontainers.Nats hands back the bound URL with the
        // ephemeral host port already substituted in — exactly the
        // shape NatsConnectionHelper.NormaliseServerUrl expects.
        ServerUrl = _container.GetConnectionString();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

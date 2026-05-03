// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests the <see cref="MockCommand.RunAsync"/> argument-validation
/// frontier — exclusivity of the four input modes (recording / schema
/// / grpc-schema / graphql-schema), chaos-string parsing, and the
/// null-cli guard. The happy-path "spin up MockServer, replay" is
/// covered by the integration harness in <c>tests/Kuestenlogik.Bowire.IntegrationTests</c>.
/// </summary>
public sealed class MockCommandTests
{
    [Fact]
    public async Task RunAsync_NullCli_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => MockCommand.RunAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_NoSource_ReturnsUsageExit()
    {
        // No --recording / --schema / --grpc-schema / --graphql-schema
        // specified → exit code 2 with a usage hint on stderr.
        var rc = await MockCommand.RunAsync(new MockCliOptions(), TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_TwoSources_ReturnsUsageExit()
    {
        var cli = new MockCliOptions
        {
            RecordingPath = "rec.json",
            SchemaPath = "openapi.yml",
        };
        var rc = await MockCommand.RunAsync(cli, TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_AllFourSources_ReturnsUsageExit()
    {
        var cli = new MockCliOptions
        {
            RecordingPath = "rec.json",
            SchemaPath = "openapi.yml",
            GrpcSchemaPath = "fds.pb",
            GraphQlSchemaPath = "schema.graphql",
        };
        var rc = await MockCommand.RunAsync(cli, TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_BadChaosSpec_ReturnsUsageExit()
    {
        // --chaos with garbage syntax → ChaosOptions.Parse throws
        // FormatException → exit 2 (usage), not 1 (runtime).
        var cli = new MockCliOptions
        {
            // A schema-only mock so the chaos parse runs before any
            // schema-loader IO.
            SchemaPath = "openapi.yml",
            Chaos = "this-is-not-valid-syntax-!@#$",
        };
        var rc = await MockCommand.RunAsync(cli, TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
    }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Environments;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Environments the embedding host declares (#49).
/// </summary>
/// <remarks>
/// <para>
/// The merge is the visible half — the host's base URLs and tenant ids show up
/// in the switcher without anyone retyping them out of <c>appsettings.json</c>.
/// The strip is the half that keeps it correct: the workbench sends the whole
/// envelope back on every change, so without it a declared environment would be
/// written into <c>environments.json</c> on the first edit anybody made and
/// then exist twice, diverging the moment the host's configuration moved.
/// </para>
/// </remarks>
public class BowireProvisionedEnvironmentTests
{
    private const string Stored =
        """{"globals":{"token":"abc"},"environments":[{"id":"e1","name":"Mine","vars":{"a":"1"}}],"activeEnvId":"e1"}""";

    private static BowireProvisionedEnvironment Staging()
        => new BowireProvisionedEnvironment { Name = "Staging" }
            .Set("baseUrl", "https://staging.example.com")
            .Set("retries", 3);

    [Fact]
    public void TheHostsEnvironmentShowsUpAlongsideThePersonsOwn()
    {
        var merged = JsonDocument.Parse(
            BowireProvisionedEnvironments.Merge(Stored, [Staging()]));

        var environments = merged.RootElement.GetProperty("environments");
        Assert.Equal(2, environments.GetArrayLength());

        var host = environments[1];
        Assert.Equal("host:Staging", host.GetProperty("id").GetString());
        Assert.Equal("Staging", host.GetProperty("name").GetString());
        Assert.Equal("https://staging.example.com",
            host.GetProperty("vars").GetProperty("baseUrl").GetString());

        // Marked, so the workbench can render it as the host's rather than as
        // something to edit and lose.
        Assert.True(host.GetProperty("provisioned").GetBoolean());

        // And nothing of the person's was touched.
        Assert.Equal("Mine", environments[0].GetProperty("name").GetString());
        Assert.Equal("abc", merged.RootElement.GetProperty("globals").GetProperty("token").GetString());
        Assert.Equal("e1", merged.RootElement.GetProperty("activeEnvId").GetString());
    }

    [Fact]
    public void NonStringVariablesAreWrittenInvariantly()
    {
        // A host passing an int should not get a value that depends on the
        // machine's culture — a decimal comma in a URL is a support case.
        var merged = JsonDocument.Parse(
            BowireProvisionedEnvironments.Merge(Stored, [Staging()]));

        Assert.Equal("3", merged.RootElement.GetProperty("environments")[1]
            .GetProperty("vars").GetProperty("retries").GetString());
    }

    [Fact]
    public void SavingDoesNotWriteTheHostsEnvironmentIntoThePersonsFile()
    {
        // The load-bearing one. Round-trip what the workbench would send back.
        var asRendered = BowireProvisionedEnvironments.Merge(Stored, [Staging()]);

        var toSave = JsonDocument.Parse(
            BowireProvisionedEnvironments.Strip(asRendered, [Staging()]));

        var environments = toSave.RootElement.GetProperty("environments");
        Assert.Equal(1, environments.GetArrayLength());
        Assert.Equal("e1", environments[0].GetProperty("id").GetString());
    }

    [Fact]
    public void StrippingIsByIdRatherThanByTheFlagAClientSends()
    {
        // The flag is something a client could omit; the ids are ours.
        var claimed =
            """{"globals":{},"environments":[{"id":"host:Staging","name":"Staging","vars":{"baseUrl":"http://evil"}}],"activeEnvId":""}""";

        var toSave = JsonDocument.Parse(
            BowireProvisionedEnvironments.Strip(claimed, [Staging()]));

        Assert.Equal(0, toSave.RootElement.GetProperty("environments").GetArrayLength());
    }

    [Fact]
    public void ALeftoverStoredCopyLosesToTheDeclaration()
    {
        // From before the strip existed: the same id sitting in the file. The
        // declaration is the source of truth, so the stale value must not show
        // up as a second entry with the same name and a different answer.
        var withLeftover =
            """{"globals":{},"environments":[{"id":"host:Staging","name":"Staging","vars":{"baseUrl":"http://stale"}}],"activeEnvId":""}""";

        var merged = JsonDocument.Parse(
            BowireProvisionedEnvironments.Merge(withLeftover, [Staging()]));

        var environments = merged.RootElement.GetProperty("environments");
        Assert.Equal(1, environments.GetArrayLength());
        Assert.Equal("https://staging.example.com",
            environments[0].GetProperty("vars").GetProperty("baseUrl").GetString());
    }

    [Fact]
    public void AHostThatDeclaredNothingIsUntouched()
    {
        // The common case, and it must not so much as reformat the file.
        Assert.Equal(Stored, BowireProvisionedEnvironments.Merge(Stored, []));
        Assert.Equal(Stored, BowireProvisionedEnvironments.Strip(Stored, []));
    }

    [Fact]
    public void ACorruptEnvelopeIsLeftForTheStoreToReport()
    {
        const string corrupt = "{ this is not json";

        Assert.Equal(corrupt, BowireProvisionedEnvironments.Merge(corrupt, [Staging()]));
        Assert.Equal(corrupt, BowireProvisionedEnvironments.Strip(corrupt, [Staging()]));
    }

    [Fact]
    public void DeclaringTheSameNameTwiceKeepsTheLastWord()
    {
        // A host composing configuration in layers should get one entry, not
        // two sharing a name and disagreeing.
        var services = new ServiceCollection();
        services.AddBowireEnvironment("Staging", env => env.Set("baseUrl", "http://first"));
        services.AddBowireEnvironment("Staging", env => env.Set("baseUrl", "http://second"));

        var declared = services.BuildServiceProvider()
            .GetServices<BowireProvisionedEnvironment>()
            .ToList();

        Assert.Single(declared);
        Assert.Equal("http://second", declared[0].Variables["baseUrl"]);
    }

    [Fact]
    public void AVariableTheHostCouldNotResolveIsEmptyRatherThanAbsent()
    {
        // Better seen as empty in the switcher than discovered as an
        // unsubstituted {{placeholder}} in a failing request.
        var environment = new BowireProvisionedEnvironment { Name = "Staging" }
            .Set("baseUrl", (string?)null);

        Assert.Equal(string.Empty, environment.Variables["baseUrl"]);
    }
}

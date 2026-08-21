// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// The checkout-relative path every SARIF result carries as its
/// <c>physicalLocation</c>.
/// <para>
/// A DAST finding is about a URL, not a file, but Code Scanning insists on a
/// physical location and refuses an <c>https://</c> URI for it. This used to
/// be the literal <c>.github/workflows/scan-self.yml</c>, which was wrong for
/// any repo that merely consumes the composite action — every alert pointed
/// at a file that does not exist there. These pin the derivation so that
/// cannot come back silently.
/// </para>
/// </summary>
[Collection("CwdSerialised")]
public sealed class ScanSarifLocationTests : IDisposable
{
    private const string Var = "GITHUB_WORKFLOW_REF";
    private readonly string? _original = Environment.GetEnvironmentVariable(Var);

    public void Dispose() => Environment.SetEnvironmentVariable(Var, _original);

    [Theory]
    // The shape GitHub actually sets.
    [InlineData(
        "Kuestenlogik/Bowire/.github/workflows/scan-dogfood.yml@refs/heads/main",
        ".github/workflows/scan-dogfood.yml")]
    // A consumer's repo names their workflow, not ours — the whole point.
    [InlineData(
        "acme/api/.github/workflows/nightly-security.yml@refs/tags/v1.2.3",
        ".github/workflows/nightly-security.yml")]
    // A ref containing '@' must not confuse the split.
    [InlineData(
        "acme/api/.github/workflows/s.yml@refs/heads/feature@2",
        ".github/workflows/s.yml")]
    public void DerivesTheWorkflowPathFromTheEnvironment(string workflowRef, string expected)
    {
        Environment.SetEnvironmentVariable(Var, workflowRef);
        Assert.Equal(expected, ScanCommand.SarifPlaceholderPath());
    }

    [Theory]
    // Not running in Actions at all — the common local case.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Malformed: nothing left after stripping owner/repo. Falling back beats
    // emitting an empty uri, which Code Scanning rejects outright.
    [InlineData("owner/repo")]
    [InlineData("owner/repo/@refs/heads/main")]
    public void FallsBackToABareTokenWhenThereIsNoWorkflow(string? workflowRef)
    {
        Environment.SetEnvironmentVariable(Var, workflowRef);

        var path = ScanCommand.SarifPlaceholderPath();

        // A bare token, deliberately: it reads as the placeholder it is rather
        // than sending someone looking for a file that was never there.
        Assert.Equal("bowire-scan", path);
        Assert.DoesNotContain("/", path, StringComparison.Ordinal);
    }

    [Fact]
    public void NeverNamesTheSelfSmokeWorkflowByDefault()
    {
        // The regression this file exists for: a hardcoded path that happened
        // to be right in exactly one workflow of one repo.
        Environment.SetEnvironmentVariable(Var, null);
        Assert.NotEqual(".github/workflows/scan-self.yml", ScanCommand.SarifPlaceholderPath());
    }
}

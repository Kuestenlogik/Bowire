// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Tests.Plugins;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// <c>bowire auth-recording</c> — capture, list, remove.
/// </summary>
/// <remarks>
/// <para>
/// A captured recording is a credential on disk that a running mock resolves
/// by id, so the surface has one property worth defending above all: the
/// secret arrives through an environment variable and never through an
/// argument. Shell history and process listings are both readable by things
/// that should not see it, which is why <c>--credential-env</c> takes a
/// <em>name</em> and there is no <c>--credential</c>.
/// </para>
/// <para>
/// The listing is the other half of that: it exists so an operator can see
/// what is stored without printing what is stored.
/// </para>
/// <para>
/// The <c>--flow</c> half runs a login chain over the network, so only its
/// file-level refusal is exercised here.
/// </para>
/// </remarks>
[Collection("BowireUserContext")]
public sealed class AuthRecordingCommandTests : IDisposable
{
    private const string EnvVar = "BOWIRE_TEST_AUTH_RECORDING_SECRET";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-authrec-" + Guid.NewGuid().ToString("N"));
    private readonly IBowireUserStore _previous = BowireUserContext.Current;

    public AuthRecordingCommandTests()
    {
        // The store resolves through BowireUserContext, not BowirePaths — a
        // test that missed this would write real credentials into the
        // developer's own ~/.bowire/workspaces/auth-recordings.
        Directory.CreateDirectory(_root);
        BowireUserContext.Current = new DefaultBowireUserStore(_root);
    }

    public void Dispose()
    {
        BowireUserContext.Current = _previous;
        Environment.SetEnvironmentVariable(EnvVar, null);
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static async Task<(int Exit, string Out, string Err)> Cli(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await BowireCli.RunAsync(
            args, new ConfigurationBuilder().Build(),
            plugins: TestPluginLoaders.None(), stdout: stdout, stderr: stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static Task<(int Exit, string Out, string Err)> Capture(params string[] extra)
        => Cli([.. new[] { "auth-recording", "capture" }, .. extra]);

    // ---- capture ----

    [Fact]
    public async Task Capturing_Without_An_Id_Is_A_Usage_Error()
    {
        // The id is what a mock's auth.authRecordingId points at; without one
        // the recording could never be resolved.
        var (exit, _, err) = await Capture("--credential-env", EnvVar);

        Assert.Equal(64, exit);
        Assert.Contains("--id is required", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capturing_With_Neither_A_Source_Nor_A_Flow_Is_A_Usage_Error()
    {
        var (exit, _, err) = await Capture("--id", "prod-token");

        Assert.Equal(64, exit);
        Assert.Contains("exactly one", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capturing_With_Both_A_Source_And_A_Flow_Is_A_Usage_Error()
    {
        // They mean different things — read a variable, or run a login chain
        // over the network — and picking one silently would be a surprise in
        // the direction of "it made outbound calls I did not ask for".
        var (exit, _, err) = await Capture(
            "--id", "prod-token", "--credential-env", EnvVar, "--flow", "flow.json");

        Assert.Equal(64, exit);
        Assert.Contains("exactly one", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Environment_Variable_That_Is_Not_Set_Is_Reported_By_Name()
    {
        // The commonest real failure: a CI job that forgot to map the secret.
        // Naming the variable is what makes it a one-line fix.
        var (exit, _, err) = await Capture("--id", "prod-token", "--credential-env", EnvVar);

        Assert.Equal(64, exit);
        Assert.Contains(EnvVar, err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Captured_Recording_Says_So_Without_Printing_The_Credential()
    {
        Environment.SetEnvironmentVariable(EnvVar, "super-secret-token");

        var (exit, output, _) = await Capture("--id", "prod-token", "--credential-env", EnvVar);

        Assert.Equal(0, exit);
        Assert.Contains("Captured auth recording 'prod-token'", output, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-token", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Flow_File_That_Is_Not_There_Fails_Before_Anything_Is_Called()
    {
        // Exit 66 (no input) rather than 65 (data error): nothing was run, so
        // a CI job can tell "you pointed me at the wrong path" from "the login
        // chain failed".
        var (exit, _, err) = await Capture(
            "--id", "prod-token", "--flow", Path.Combine(_root, "no-such-flow.json"));

        Assert.Equal(66, exit);
        Assert.Contains("can't read flow file", err, StringComparison.Ordinal);
    }

    // ---- list ----

    [Fact]
    public async Task Listing_An_Empty_Store_Says_So()
    {
        var (exit, output, _) = await Cli("auth-recording", "list");

        Assert.Equal(0, exit);
        Assert.Contains("No auth recordings", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Listing_Shows_What_Is_Stored_But_Never_The_Credential()
    {
        // This is the whole point of a separate listing surface: an operator
        // checking which recordings exist must not thereby print secrets into
        // a terminal that is being shared or recorded.
        Environment.SetEnvironmentVariable(EnvVar, "super-secret-token");
        await Capture("--id", "prod-token", "--name", "Production", "--scheme", "bearer",
            "--credential-env", EnvVar);

        var (exit, output, _) = await Cli("auth-recording", "list");

        Assert.Equal(0, exit);
        Assert.Contains("prod-token", output, StringComparison.Ordinal);
        Assert.Contains("Production", output, StringComparison.Ordinal);
        Assert.Contains("bearer", output, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-token", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Several_Recordings_Are_All_Listed()
    {
        Environment.SetEnvironmentVariable(EnvVar, "t");
        await Capture("--id", "staging", "--credential-env", EnvVar);
        await Capture("--id", "production", "--credential-env", EnvVar);

        var (_, output, _) = await Cli("auth-recording", "list");

        Assert.Contains("staging", output, StringComparison.Ordinal);
        Assert.Contains("production", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Workspace_Scoped_Recording_Does_Not_Show_In_The_Shared_List()
    {
        // Scoping exists so two workspaces can hold a recording under the same
        // id without one silently answering for the other.
        Environment.SetEnvironmentVariable(EnvVar, "t");
        await Capture("--id", "prod-token", "--workspace", "ws-1", "--credential-env", EnvVar);

        var (_, shared, _) = await Cli("auth-recording", "list");
        var (_, scoped, _) = await Cli("auth-recording", "list", "--workspace", "ws-1");

        Assert.Contains("No auth recordings", shared, StringComparison.Ordinal);
        Assert.Contains("prod-token", scoped, StringComparison.Ordinal);
    }

    // ---- remove ----

    [Fact]
    public async Task Removing_A_Recording_Takes_It_Out_Of_The_Listing()
    {
        Environment.SetEnvironmentVariable(EnvVar, "t");
        await Capture("--id", "prod-token", "--credential-env", EnvVar);

        var (exit, output, _) = await Cli("auth-recording", "remove", "prod-token");

        Assert.Equal(0, exit);
        Assert.Contains("Removed auth recording", output, StringComparison.Ordinal);
        var (_, listing, _) = await Cli("auth-recording", "list");
        Assert.Contains("No auth recordings", listing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_Something_That_Is_Not_There_Says_So_Without_Failing()
    {
        // Delete is idempotent on purpose — a cleanup step in a CI job runs
        // whether or not the capture step got that far.
        var (exit, output, _) = await Cli("auth-recording", "remove", "never-captured");

        Assert.Equal(0, exit);
        Assert.Contains("No auth recording 'never-captured'", output, StringComparison.Ordinal);
    }
}

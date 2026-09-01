// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Projects;
using Kuestenlogik.Bowire.Tests.Plugins;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests.Auth;

/// <summary>
/// <c>bowire users</c> — the operator's side of #97, from the host rather
/// than from a browser session as the person the data belongs to.
/// </summary>
/// <remarks>
/// Shares <c>CwdSerialised</c> with the other suites that swap
/// <see cref="BowirePaths.Current"/>: it is process-global, so two of these
/// running at once would each see the other's storage root.
/// </remarks>
[Collection("CwdSerialised")]
public sealed class UsersCommandTests : IDisposable
{
    private const string Subject = "ada@example.com";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-users-cli-" + Guid.NewGuid().ToString("N"));
    private readonly IBowirePathResolver _previous = BowirePaths.Current;

    public UsersCommandTests()
    {
        Directory.CreateDirectory(_root);
        BowirePaths.Current = new FixedRootResolver(_root);
    }

    public void Dispose()
    {
        BowirePaths.Current = _previous;
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Legacy(string name, string content = "{}")
        => File.WriteAllText(Path.Combine(_root, name), content);

    private string Slot => new ScopedBowireUserStore(_root, Subject).Slot;

    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await BowireCli.RunAsync(
            args,
            new ConfigurationBuilder().Build(),
            plugins: TestPluginLoaders.None(),
            stdout: stdout,
            stderr: stderr,
            cancellationToken: TestContext.Current.CancellationToken);
        return (code, stdout.ToString(), stderr.ToString());
    }

    // ---- reporting before acting ----

    [Fact]
    public async Task Without_A_Flag_Nothing_On_Disk_Changes()
    {
        // An operator asking "what would this do" must be able to ask without
        // it being the answer.
        Legacy("environments.json");

        var (code, output, _) = await Run("users", "migrate", Subject);

        Assert.Equal(0, code);
        Assert.Contains("Available", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Slot));
    }

    [Fact]
    public async Task The_Report_Names_The_Files_It_Would_Move()
    {
        // "12 files" is not enough to recognise whose data this is.
        Legacy("environments.json");
        Legacy("collections.json");

        var (_, output, _) = await Run("users", "migrate", Subject);

        Assert.Contains("environments.json", output, StringComparison.Ordinal);
        Assert.Contains("collections.json", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_Decisions_At_Once_Are_Refused()
    {
        Legacy("environments.json");

        var (code, _, err) = await Run("users", "migrate", Subject, "--apply", "--decline");

        Assert.NotEqual(0, code);
        Assert.Contains("Choose one", err, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Slot));
    }

    // ---- acting ----

    [Fact]
    public async Task Applying_Copies_And_Says_The_Originals_Are_Still_There()
    {
        Legacy("environments.json");

        var (code, output, _) = await Run("users", "migrate", Subject, "--apply");

        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(Slot, "environments.json")));
        Assert.True(File.Exists(Path.Combine(_root, "environments.json")));
        Assert.Contains("originals are untouched", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applying_Twice_Is_Refused_Rather_Than_Repeated()
    {
        Legacy("environments.json");
        await Run("users", "migrate", Subject, "--apply");

        var (code, _, err) = await Run("users", "migrate", Subject, "--apply");

        Assert.NotEqual(0, code);
        Assert.Contains("AlreadyDecided", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Undoing_Hands_Back_Where_The_Slot_Went()
    {
        // The question an admin actually has after undoing is "did that delete
        // my work?", and the answer has to be a path.
        Legacy("environments.json");
        await Run("users", "migrate", Subject, "--apply");

        var (code, output, _) = await Run("users", "migrate", Subject, "--undo");

        Assert.Equal(0, code);
        Assert.Contains("Nothing was deleted", output, StringComparison.Ordinal);
        Assert.Contains(BowireUserSlot.DirectoryName, output, StringComparison.Ordinal);
    }

    // ---- listing ----

    [Fact]
    public async Task Listing_An_Install_Nobody_Has_Signed_Into_Is_Not_An_Error()
    {
        var (code, output, _) = await Run("users", "list");

        Assert.Equal(0, code);
        Assert.Contains("single-user", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_Shows_The_Slot_And_What_Was_Decided()
    {
        Legacy("environments.json");
        await Run("users", "migrate", Subject, "--apply");

        var (code, output, _) = await Run("users", "list");

        Assert.Equal(0, code);
        Assert.Contains(Path.GetFileName(Slot), output, StringComparison.Ordinal);
        Assert.Contains("migrated", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_Says_The_Subject_Cannot_Be_Read_Back()
    {
        // Otherwise the reader assumes the mapping is missing rather than
        // one-way, and goes looking for the table that would reverse it.
        Legacy("environments.json");
        await Run("users", "migrate", Subject, "--decline");

        var (_, output, _) = await Run("users", "list");

        Assert.Contains("cannot be read back", output, StringComparison.Ordinal);
    }

    /// <summary>A resolver that answers every scope with one directory.</summary>
    private sealed class FixedRootResolver(string root) : IBowirePathResolver
    {
        public string Root(BowireStorageScope scope) => root;

        public string Resolve(BowireStorageScope scope, params string[] segments)
            => segments is null || segments.Length == 0
                ? root
                : Path.Combine(root, Path.Combine(segments));
    }
}

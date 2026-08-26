// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mcp;

namespace Kuestenlogik.Bowire.Mcp.Tests;

/// <summary>
/// The typed-URL history — where an agent's reach comes from under
/// <c>--allow-invoke</c>.
/// </summary>
/// <remarks>
/// <para>
/// This file is not a convenience cache. It is the second source the MCP
/// allowlist can be seeded from, so every URL in it is a place an agent may
/// then send a request. That makes the write side the interesting half: only
/// URLs a human actually typed belong here, exactly once, and a corrupt file
/// has to read as "no history" rather than as something half-parsed.
/// </para>
/// <para>
/// In <see cref="BowireConfigFixture"/> because the home override it uses is
/// process-global.
/// </para>
/// </remarks>
[Collection(nameof(BowireConfigFixture))]
public sealed class McpTypedUrlStoreTests : IDisposable
{
    private readonly string? _previous = BowireMcpTypedUrlStore.HomeDirOverride;
    private readonly string _home = SafePath.Combine(
        Path.GetTempPath(), $"bowire-typedurls-{Guid.NewGuid():N}");

    public McpTypedUrlStoreTests()
    {
        Directory.CreateDirectory(_home);
        BowireMcpTypedUrlStore.HomeDirOverride = _home;
    }

    private readonly List<BowireMockHandleRegistry> _handles = [];

    public void Dispose()
    {
        foreach (var h in _handles) h.DisposeAsync().AsTask().GetAwaiter().GetResult();
        BowireMcpTypedUrlStore.HomeDirOverride = _previous;
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void WriteRaw(string contents)
    {
        var path = BowireMcpTypedUrlStore.FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    // ---- reading ----

    [Fact]
    public void A_Fresh_Install_Has_No_History()
        // Nothing typed yet, so nothing to widen an allowlist with.
        => Assert.Empty(BowireMcpTypedUrlStore.LoadAll());

    [Fact]
    public void A_Stored_Url_Is_Read_Back()
    {
        WriteRaw("""["https://api.example.com"]""");

        Assert.Equal(["https://api.example.com"], BowireMcpTypedUrlStore.LoadAll());
    }

    [Fact]
    public void A_File_That_Will_Not_Parse_Reads_As_No_History()
    {
        // "corrupt" and "empty" have to be indistinguishable to the caller —
        // the alternative is an agent's allowlist depending on whether a
        // half-written file happened to end mid-token.
        WriteRaw("[ this is not json");

        Assert.Empty(BowireMcpTypedUrlStore.LoadAll());
    }

    [Fact]
    public void A_Document_That_Is_Not_An_Array_Reads_As_No_History()
    {
        // A hand-edited file, or a future format. Neither should produce a
        // partial list.
        WriteRaw("""{"urls":["https://api.example.com"]}""");

        Assert.Empty(BowireMcpTypedUrlStore.LoadAll());
    }

    [Fact]
    public void Entries_That_Are_Not_Strings_Are_Skipped_Rather_Than_Coerced()
    {
        // A number or an object is not a URL, and coercing one would put
        // something meaningless on a security boundary.
        WriteRaw("""["https://api.example.com", 42, null, {"a":1}, "   "]""");

        Assert.Equal(["https://api.example.com"], BowireMcpTypedUrlStore.LoadAll());
    }

    // ---- writing ----

    [Fact]
    public void Adding_A_Url_Persists_It()
    {
        Assert.True(BowireMcpTypedUrlStore.Add("https://api.example.com"));

        Assert.Contains("https://api.example.com", BowireMcpTypedUrlStore.LoadAll());
    }

    [Fact]
    public void Adding_Creates_The_Directory_When_It_Is_Missing()
    {
        // First run on a fresh machine: nothing under ~/.bowire yet.
        Assert.True(BowireMcpTypedUrlStore.Add("https://api.example.com"));

        Assert.True(File.Exists(BowireMcpTypedUrlStore.FilePath));
    }

    [Fact]
    public void The_Same_Url_Twice_Is_Stored_Once()
    {
        BowireMcpTypedUrlStore.Add("https://api.example.com");

        Assert.False(BowireMcpTypedUrlStore.Add("https://api.example.com"));
        Assert.Single(BowireMcpTypedUrlStore.LoadAll());
    }

    [Fact]
    public void A_Url_That_Differs_Only_In_Case_Is_The_Same_Url()
    {
        // Hosts are case-insensitive, and two spellings of one host would
        // otherwise read as two grants.
        BowireMcpTypedUrlStore.Add("https://API.example.com");

        Assert.False(BowireMcpTypedUrlStore.Add("https://api.example.com"));
        Assert.Single(BowireMcpTypedUrlStore.LoadAll());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Nothing_Useful_Is_Not_Stored(string? url)
        => Assert.False(BowireMcpTypedUrlStore.Add(url!));

    [Fact]
    public void Two_Different_Urls_Both_Survive()
    {
        BowireMcpTypedUrlStore.Add("https://api.example.com");
        BowireMcpTypedUrlStore.Add("https://staging.example.com");

        var all = BowireMcpTypedUrlStore.LoadAll();
        Assert.Equal(2, all.Count);
        Assert.Contains("https://staging.example.com", all);
    }

    [Fact]
    public void Adding_On_Top_Of_A_Corrupt_File_Starts_A_Clean_History()
    {
        // The read degrades to empty, so the write starts over rather than
        // failing forever on a file nobody can repair by hand.
        WriteRaw("[ this is not json");

        Assert.True(BowireMcpTypedUrlStore.Add("https://api.example.com"));
        Assert.Equal(["https://api.example.com"], BowireMcpTypedUrlStore.LoadAll());
    }

    [Fact]
    public void The_File_Sits_Where_The_Rest_Of_The_Mcp_State_Does()
    {
        // One override redirects the whole MCP surface; a store resolving its
        // own path separately is how a test ends up writing to the
        // developer's real ~/.bowire.
        Assert.StartsWith(_home, BowireMcpTypedUrlStore.FilePath, StringComparison.Ordinal);
        Assert.EndsWith("typed-urls.json", BowireMcpTypedUrlStore.FilePath, StringComparison.Ordinal);
    }

    // ---- what --allow-invoke does with it ----

    [Fact]
    public void The_History_Widens_The_Allowlist_When_The_Operator_Asked_For_It()
    {
        // `--allow-invoke` is the middle setting between "only what
        // environments.json names" and "anywhere at all": an agent may reach
        // the places the operator has already pointed Bowire at by hand.
        BowireMcpTypedUrlStore.Add("https://typed.example.com");
        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            LoadAllowlistFromTypedUrls = true,
        };

        _ = NewTools(options);

        Assert.Contains("https://typed.example.com", options.AllowedServerUrls);
    }

    [Fact]
    public void Without_The_Flag_The_History_Grants_Nothing()
    {
        // The default. A URL somebody typed once is not consent for an agent
        // to call it.
        BowireMcpTypedUrlStore.Add("https://typed.example.com");
        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            LoadAllowlistFromTypedUrls = false,
        };

        _ = NewTools(options);

        Assert.Empty(options.AllowedServerUrls);
    }

    [Fact]
    public void Seeding_Is_Strictly_Additive_To_What_The_Host_Configured()
    {
        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            LoadAllowlistFromTypedUrls = true,
        };
        options.AllowedServerUrls.Add("https://configured.example.com");
        BowireMcpTypedUrlStore.Add("https://typed.example.com");

        _ = NewTools(options);

        Assert.Contains("https://configured.example.com", options.AllowedServerUrls);
        Assert.Contains("https://typed.example.com", options.AllowedServerUrls);
    }

    [Fact]
    public void A_Corrupt_History_Does_Not_Break_Start_Up()
    {
        // MCP start-up must survive a file nobody can repair by hand; the
        // agent simply gets the explicit list.
        WriteRaw("[ this is not json");
        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            LoadAllowlistFromTypedUrls = true,
        };

        _ = NewTools(options);

        Assert.Empty(options.AllowedServerUrls);
    }

    private BowireMcpTools NewTools(BowireMcpOptions options)
    {
        var handles = new BowireMockHandleRegistry();
        _handles.Add(handles);
        return new BowireMcpTools(
            new BowireProtocolRegistry(),
            handles,
            new BowireMcpConfirmationStore(),
            new Kuestenlogik.Bowire.Recording.BowireRecordingSession(),
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BowireMcpTools>.Instance);
    }
}

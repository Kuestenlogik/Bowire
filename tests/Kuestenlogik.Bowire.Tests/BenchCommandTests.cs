// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// <c>bowire bench</c> — the parsing it does before a single request goes out,
/// and the numbers it prints afterwards.
/// </summary>
/// <remarks>
/// A benchmark that mis-parses its input produces a plausible number for the
/// wrong request, which is worse than failing: nobody re-reads a latency they
/// have no reason to doubt. The cases below are the ones where a naive
/// implementation quietly does that.
/// </remarks>
public sealed class BenchCommandTests
{
    // ---- protocol@url ----

    [Fact]
    public void A_Protocol_Prefix_Is_Split_Off_The_Url()
    {
        var (url, id) = BenchCommand.SplitProtocolHint("grpc@localhost:5001", null);

        Assert.Equal("localhost:5001", url);
        Assert.Equal("grpc", id);
    }

    [Fact]
    public void Userinfo_In_A_Url_Is_Not_A_Protocol_Hint()
    {
        // The case the naive IndexOf('@') gets wrong: in http://user@host the
        // '@' is credentials, and treating "http://user" as a plugin id would
        // both invent a protocol and mangle the URL — while still producing a
        // benchmark number for whatever it ended up calling.
        var (url, id) = BenchCommand.SplitProtocolHint("http://user@host/api", null);

        Assert.Equal("http://user@host/api", url);
        Assert.Null(id);
    }

    [Fact]
    public void A_Hint_Before_The_Scheme_Still_Counts()
    {
        var (url, id) = BenchCommand.SplitProtocolHint("rest@http://api.example.com", null);

        Assert.Equal("http://api.example.com", url);
        Assert.Equal("rest", id);
    }

    [Fact]
    public void An_Explicit_Protocol_Flag_Wins_Over_The_Prefix()
    {
        // --protocol is the more deliberate statement of the two.
        var (url, id) = BenchCommand.SplitProtocolHint("grpc@localhost:5001", "rest");

        Assert.Equal("localhost:5001", url);
        Assert.Equal("rest", id);
    }

    [Theory]
    [InlineData("localhost:5001")]
    [InlineData("@leading")]          // nothing before the '@' to name
    [InlineData("")]
    public void A_Url_With_No_Usable_Hint_Is_Left_Alone(string url)
    {
        var (resultUrl, id) = BenchCommand.SplitProtocolHint(url, null);

        Assert.Equal(url, resultUrl);
        Assert.Null(id);
    }

    // ---- headers ----

    [Fact]
    public void Headers_Are_Split_On_The_First_Colon_Only()
    {
        // A value can contain colons — a URL, a timestamp — and splitting on
        // all of them would truncate exactly the headers that matter.
        var map = BenchCommand.ParseHeaders(["Referer: https://example.com:8443/x"]);

        Assert.NotNull(map);
        Assert.Equal("https://example.com:8443/x", map!["Referer"]);
    }

    [Fact]
    public void Header_Names_Are_Matched_Without_Regard_To_Case()
    {
        var map = BenchCommand.ParseHeaders(["Authorization: Bearer x"]);

        Assert.Equal("Bearer x", map!["authorization"]);
    }

    [Fact]
    public void Surrounding_Whitespace_Is_Trimmed_From_Both_Halves()
    {
        var map = BenchCommand.ParseHeaders(["  X-Api-Key  :   secret  "]);

        Assert.Equal("secret", map!["X-Api-Key"]);
    }

    [Theory]
    [InlineData("no-colon-here")]
    [InlineData(": value-without-a-name")]
    public void A_Header_That_Is_Not_A_Pair_Is_Skipped(string header)
    {
        // Skipped rather than fatal: one malformed -H should not lose a run
        // that is otherwise fine, and the request still carries the rest.
        Assert.Null(BenchCommand.ParseHeaders([header]));
    }

    [Fact]
    public void One_Malformed_Header_Does_Not_Lose_The_Others()
    {
        var map = BenchCommand.ParseHeaders(["bad", "X-Ok: yes"]);

        Assert.NotNull(map);
        Assert.Single(map!);
        Assert.Equal("yes", map!["X-Ok"]);
    }

    [Fact]
    public void No_Headers_Means_None_Rather_Than_An_Empty_Map()
        => Assert.Null(BenchCommand.ParseHeaders([]));

    // ---- request body ----

    [Fact]
    public async Task A_Body_Is_Used_As_Given()
    {
        Assert.Equal("""{"id":1}""",
            await BenchCommand.ReadBodyAsync("""{"id":1}""", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_At_Prefix_Reads_The_File()
    {
        var path = Path.Combine(Path.GetTempPath(), "bowire-bench-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, """{"from":"file"}""", TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal("""{"from":"file"}""",
                await BenchCommand.ReadBodyAsync("@" + path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task An_At_Prefix_Naming_No_File_Is_Sent_As_Literal_Text()
    {
        // Deliberate: a payload can legitimately start with '@', and silently
        // sending an empty body would benchmark a request nobody meant to make.
        const string literal = "@not-a-path-just-text";

        Assert.Equal(literal, await BenchCommand.ReadBodyAsync(literal, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task No_Data_Means_No_Body(string? data)
        => Assert.Null(await BenchCommand.ReadBodyAsync(data, TestContext.Current.CancellationToken));

    // ---- latency formatting ----

    [Theory]
    [InlineData(0.5, "0.5ms")]
    [InlineData(1.234, "1.23ms")]
    [InlineData(99.994, "99.99ms")]
    [InlineData(100, "100ms")]
    [InlineData(1234.6, "1235ms")]
    public void Latency_Keeps_Decimals_Only_While_They_Carry_Information(double value, string expected)
        // Two decimals below 100ms, because the difference between 1.2 and
        // 1.23 is real at that scale; none above, where it is noise.
        => Assert.Equal(expected, BenchCommand.Ms(value));

    [Fact]
    public void Latency_Is_Formatted_The_Same_In_Every_Locale()
    {
        // A comma decimal separator would make the number unparseable to the
        // scripts that read this output — and this machine's locale is German.
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1.23ms", BenchCommand.Ms(1.234));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    // ---- command surface ----

    [Fact]
    public void The_Command_Exposes_Its_Sub_Commands()
    {
        var names = BenchCommand.Build().Subcommands.Select(c => c.Name).ToList();

        Assert.Contains("run", names);
        Assert.Contains("schedule", names);
    }

    [Fact]
    public void The_Schedule_Group_Covers_The_Whole_Lifecycle()
    {
        // A schedule that can be added but not removed is a trap: it fires
        // unattended against a URL until someone edits a file by hand.
        var schedule = BenchCommand.Build().Subcommands.First(c => c.Name == "schedule");
        var names = schedule.Subcommands.Select(c => c.Name).ToList();

        Assert.Contains("list", names);
        Assert.Contains("add", names);
        Assert.Contains("pause", names);
        Assert.Contains("resume", names);
        Assert.Contains("remove", names);
    }
}

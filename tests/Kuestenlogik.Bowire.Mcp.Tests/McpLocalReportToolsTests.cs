// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Contracts;
using Kuestenlogik.Bowire.Mcp;

namespace Kuestenlogik.Bowire.Mcp.Tests;

/// <summary>
/// The two MCP tools that answer entirely from files already on disk:
/// <c>bowire.contract.matrix</c> and <c>bowire.report.rollup</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both are static and read-only by design — no discovery probe, no allowlist,
/// nothing that reaches a network. That is the property worth keeping: an
/// agent asking "what is broken?" gets an answer without contacting anyone's
/// provider, so these two are safe to call in a loop.
/// </para>
/// <para>
/// The shape is the contract. An agent parses this JSON; a renamed field or a
/// status spelled differently is a silent break for every caller, and unlike a
/// UI nobody sees it go wrong.
/// </para>
/// </remarks>
[Collection(nameof(BowireConfigFixture))]
public sealed class McpLocalReportToolsTests : IDisposable
{
    private readonly string _cwd = Directory.GetCurrentDirectory();
    private readonly string _root = SafePath.Combine(
        Path.GetTempPath(), $"bowire-mcp-reports-{Guid.NewGuid():N}");

    public McpLocalReportToolsTests()
    {
        Directory.CreateDirectory(_root);
        // Both tools resolve their inputs relative to the working directory
        // (`.bowire/…`), which is the workspace an agent is pointed at.
        Directory.SetCurrentDirectory(_root);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_cwd);
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ContractVerificationReport Report(
        string consumer, string provider, bool passed, string? error = null)
        => new()
        {
            Consumer = consumer,
            Provider = provider,
            StartedAt = new DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Utc),
            TotalAssertions = 1,
            PassedAssertions = passed ? 1 : 0,
            // `Passed` is computed from this on both types, so a report is
            // made to fail by giving it a failed interaction — and an
            // interaction fails by carrying an error.
            FailedInteractions = passed ? 0 : 1,
            Interactions =
            {
                new ContractInteractionResult
                {
                    Description = "GET /orders/42",
                    Method = "GET",
                    Error = passed ? null : error ?? "failed",
                },
            },
        };

    private async Task<JsonElement> Matrix()
    {
        var json = await BowireMcpTools.ContractMatrix(Ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ---- the contract matrix ----

    [Fact]
    public async Task With_Nothing_Verified_Yet_The_Matrix_Is_Empty_Rather_Than_An_Error()
    {
        // An agent exploring a fresh workspace calls this before anything has
        // run; an exception here would read as "contracts are broken".
        var matrix = await Matrix();

        Assert.Empty(matrix.GetProperty("consumers").EnumerateArray());
        Assert.Empty(matrix.GetProperty("cells").EnumerateArray());
        Assert.Equal(0, matrix.GetProperty("summary").GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task A_Stored_Verdict_Becomes_One_Cell_With_Its_Parties()
    {
        await ContractResultStore.SaveAsync(Report("web", "orders", passed: true), _root, Ct);

        var matrix = await Matrix();

        Assert.Equal("web", Assert.Single(matrix.GetProperty("consumers").EnumerateArray()).GetString());
        Assert.Equal("orders", Assert.Single(matrix.GetProperty("providers").EnumerateArray()).GetString());
        var cell = Assert.Single(matrix.GetProperty("cells").EnumerateArray());
        Assert.Equal("web", cell.GetProperty("consumer").GetString());
        Assert.Equal("orders", cell.GetProperty("provider").GetString());
        Assert.Equal(1, matrix.GetProperty("summary").GetProperty("passed").GetInt32());
    }

    [Fact]
    public async Task A_Status_Travels_As_A_Word_Not_An_Enum_Ordinal()
    {
        // An agent reads this; "pass" survives a reordering of the enum,
        // a number does not.
        await ContractResultStore.SaveAsync(Report("web", "orders", passed: true), _root, Ct);

        var cell = Assert.Single((await Matrix()).GetProperty("cells").EnumerateArray());

        Assert.Equal(JsonValueKind.String, cell.GetProperty("status").ValueKind);
        Assert.Equal("pass", cell.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_Failed_Cell_Carries_Only_The_Interactions_That_Failed()
    {
        // The whole point of the tool for an agent: "what broke?" answered
        // without the passing transcript it would otherwise have to read past.
        await ContractResultStore.SaveAsync(
            Report("web", "orders", passed: false, error: "expected 200, got 500"), _root, Ct);

        var cell = Assert.Single((await Matrix()).GetProperty("cells").EnumerateArray());

        Assert.Equal("fail", cell.GetProperty("status").GetString());
        var failure = Assert.Single(cell.GetProperty("failures").EnumerateArray());
        Assert.Equal("GET /orders/42", failure.GetProperty("description").GetString());
        Assert.Contains("got 500", failure.GetProperty("error").GetString()!, StringComparison.Ordinal);
        Assert.Equal(1, (await Matrix()).GetProperty("summary").GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task A_Passing_Cell_Lists_No_Failures_At_All()
    {
        await ContractResultStore.SaveAsync(Report("web", "orders", passed: true), _root, Ct);

        var cell = Assert.Single((await Matrix()).GetProperty("cells").EnumerateArray());

        Assert.Empty(cell.GetProperty("failures").EnumerateArray());
    }

    [Fact]
    public async Task Several_Pairs_Fill_The_Grid_In_Both_Directions()
    {
        await ContractResultStore.SaveAsync(Report("web", "orders", passed: true), _root, Ct);
        await ContractResultStore.SaveAsync(Report("web", "billing", passed: false), _root, Ct);
        await ContractResultStore.SaveAsync(Report("mobile", "orders", passed: true), _root, Ct);

        var matrix = await Matrix();

        Assert.Equal(2, matrix.GetProperty("consumers").GetArrayLength());
        Assert.Equal(2, matrix.GetProperty("providers").GetArrayLength());
        // Every consumer × provider pair gets a cell, including the pair
        // nobody has verified — that "notRun" is the useful part of a matrix.
        Assert.Equal(4, matrix.GetProperty("cells").GetArrayLength());
        Assert.Contains(matrix.GetProperty("cells").EnumerateArray(),
            c => c.GetProperty("status").GetString() == "notRun");
    }

    // ---- the report rollup ----

    [Fact]
    public async Task The_Rollup_Of_An_Empty_Workspace_Is_A_Document_With_No_Services()
    {
        // Same first-call story as the matrix: an agent points at a workspace
        // that has produced no reports yet.
        var json = await BowireMcpTools.ReportRollup(ct: Ct);

        using var doc = JsonDocument.Parse(json);
        Assert.Empty(doc.RootElement.GetProperty("services").EnumerateArray());
        Assert.Equal(0, doc.RootElement.GetProperty("summary").GetProperty("services").GetInt32());
    }

    [Fact]
    public async Task The_Rollup_Reads_Contract_Results_Into_A_Row_Per_Service()
    {
        await ContractResultStore.SaveAsync(Report("web", "orders", passed: false), _root, Ct);

        var json = await BowireMcpTools.ReportRollup(from: [".bowire"], ct: Ct);

        using var doc = JsonDocument.Parse(json);
        Assert.NotEmpty(doc.RootElement.GetProperty("services").EnumerateArray());
    }

    [Fact]
    public async Task A_Path_That_Does_Not_Exist_Rolls_Up_To_Nothing_Rather_Than_Throwing()
    {
        // An agent passes a path it guessed; the answer is an empty rollup,
        // not an error it has to interpret.
        var json = await BowireMcpTools.ReportRollup(from: ["no-such-directory"], ct: Ct);

        using var doc = JsonDocument.Parse(json);
        Assert.Empty(doc.RootElement.GetProperty("services").EnumerateArray());
    }

    [Fact]
    public async Task An_Explicit_Service_Name_Overrides_What_The_Reports_Say()
    {
        // For a repo whose report files carry no service name of their own.
        await ContractResultStore.SaveAsync(Report("web", "orders", passed: true), _root, Ct);

        var json = await BowireMcpTools.ReportRollup(from: [".bowire"], service: "checkout", ct: Ct);

        using var doc = JsonDocument.Parse(json);
        var services = doc.RootElement.GetProperty("services").EnumerateArray().ToList();
        Assert.All(services, s => Assert.Equal("checkout", s.GetProperty("service").GetString()));
    }
}

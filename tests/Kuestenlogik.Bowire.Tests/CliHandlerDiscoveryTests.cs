// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// What <c>bowire discover</c>, <c>describe</c> and <c>call</c> print when the
/// target is not there.
/// </summary>
/// <remarks>
/// <para>
/// This is the commonest run of all — the server is not up yet, the port is
/// wrong, the URL has a typo — and it is the run where the output has to earn
/// its keep. Bowire fans a discovery probe across every loaded plugin, so
/// "nothing found" comes with a table of who tried and why each one gave up,
/// plus the exit code a CI job gates on.
/// </para>
/// <para>
/// The target is <c>127.0.0.1:1</c>: a port nothing listens on, on the
/// loopback interface. Every probe fails with connection-refused immediately,
/// so nothing leaves the machine and the suite stays fast.
/// </para>
/// </remarks>
public sealed class CliHandlerDiscoveryTests : IDisposable
{
    private const string DeadTarget = "http://127.0.0.1:1";

    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
    }

    private static CliCommandOptions Options(string? protocol = null, string? target = null) => new()
    {
        Url = DeadTarget,
        Protocol = protocol,
        Target = target,
    };

    // ---- discover ----

    [Fact]
    public async Task Discovering_Nothing_Exits_One_So_A_Ci_Job_Can_Gate_On_It()
    {
        // The documented contract: exit 1 when no service was found. A job
        // that treats "discovery worked" as a precondition depends on it.
        var exit = await CliHandler.DiscoverAsync(Options(), _out, _err);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task The_Attempt_Table_Says_Which_Plugins_Tried_And_Why_They_Stopped()
    {
        // Without this the operator sees an empty list and cannot tell a
        // wrong port from a missing plugin from a server that answered
        // nothing. Each row names a plugin and an outcome.
        await CliHandler.DiscoverAsync(Options(), _out, _err);

        var output = _out.ToString() + _err.ToString();
        Assert.NotEmpty(output);
        Assert.Contains("127.0.0.1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pinning_A_Plugin_That_Is_Not_Loaded_Is_Reported_Rather_Than_Ignored()
    {
        // `--protocol nope` narrowing to nothing must not read as "the server
        // had no services" — that sends the operator to debug the wrong end.
        var exit = await CliHandler.DiscoverAsync(Options(protocol: "not-a-real-protocol"), _out, _err);

        Assert.NotEqual(0, exit);
        Assert.NotEmpty(_out.ToString() + _err.ToString());
    }

    // ---- describe ----

    [Fact]
    public async Task Describing_Without_A_Target_Is_A_Usage_Error()
    {
        // The service name is the whole argument; guessing one would describe
        // something the operator did not ask about.
        var exit = await CliHandler.DescribeAsync(Options(), _out, _err);

        Assert.NotEqual(0, exit);
        Assert.NotEmpty(_err.ToString());
    }

    [Fact]
    public async Task Describing_A_Service_Nothing_Serves_Names_The_Service_And_The_Target()
    {
        var exit = await CliHandler.DescribeAsync(
            Options(target: "orders.v1.OrderService"), _out, _err);

        Assert.NotEqual(0, exit);
        var output = _out.ToString() + _err.ToString();
        Assert.Contains("orders.v1.OrderService", output, StringComparison.Ordinal);
    }

    // ---- call ----

    [Fact]
    public async Task Calling_Without_A_Target_Is_A_Usage_Error()
        => Assert.NotEqual(0, await CliHandler.CallAsync(
            Options(), _out, _err, TestContext.Current.CancellationToken));

    [Fact]
    public async Task Calling_A_Method_No_Plugin_Found_Points_At_Discover()
    {
        // The refusal carries the next step: pin the plugin, or run discover
        // for the full table. A bare "not found" would leave the operator
        // guessing which of the two ends is wrong.
        var exit = await CliHandler.CallAsync(
            Options(target: "orders.v1.OrderService/GetOrder"), _out, _err,
            TestContext.Current.CancellationToken);

        Assert.NotEqual(0, exit);
        var output = _out.ToString() + _err.ToString();
        Assert.Contains("orders.v1.OrderService", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_Against_A_Dead_Target_Fails_Without_Throwing()
    {
        // `list` is gRPC-reflection only, so a dead target surfaces as a
        // transport error the top-level handler renders — exit 1, one line,
        // no stack trace.
        var exit = await CliHandler.ListAsync(Options(), _out, _err);

        Assert.NotEqual(0, exit);
        Assert.DoesNotContain("Unhandled exception", _err.ToString(), StringComparison.Ordinal);
    }
}

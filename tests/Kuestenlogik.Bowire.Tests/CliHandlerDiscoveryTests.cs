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
    public async Task The_Exit_Code_Follows_Whether_Any_Service_Was_Found()
    {
        // The documented contract is `services > 0 ? 0 : 1`, and a CI job that
        // treats "discovery worked" as a precondition gates on it.
        //
        // Only half of it is assertable from here, and the reason is worth
        // writing down: BowireProtocolRegistry.Discover() scans loaded
        // assemblies, and this test assembly carries stub protocols that
        // answer any URL. So inside the test host a probe against a dead
        // target still finds services — exit 0 is then the *correct* answer,
        // and asserting 1 would be asserting the absence of the fixtures
        // rather than the behaviour of the command.
        var exit = await CliHandler.DiscoverAsync(Options(), _out, _err);

        var foundServices = _out.ToString().Contains("method", StringComparison.Ordinal);
        Assert.Equal(foundServices ? 0 : 1, exit);
    }

    [Fact]
    public async Task The_Attempt_Table_Says_How_Many_Plugins_Tried_And_How_Many_Failed()
    {
        // Without it the operator sees an empty list and cannot tell a wrong
        // port from a missing plugin from a server that answered nothing.
        //
        // Asserted on the counts rather than on the target string: what the
        // table contains depends on which plugin assemblies are loaded in the
        // test host, and that is not what this is about.
        await CliHandler.DiscoverAsync(Options(), _out, _err);

        var output = _out.ToString() + _err.ToString();
        Assert.Contains("probed", output, StringComparison.Ordinal);
        Assert.Contains("failed", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Plugin_With_No_Name_Does_Not_Take_The_Table_Down()
    {
        // Found by this suite in CI: the table pads its first column with
        // `.Length` on a value that comes from the plugin, so a plugin whose
        // Id or Name is null used to NullReference *while rendering the very
        // table that exists to explain what plugins did*. The probe now
        // substitutes a placeholder at the boundary.
        await CliHandler.DiscoverAsync(Options(), _out, _err);

        Assert.DoesNotContain("Object reference not set",
            _out.ToString() + _err.ToString(), StringComparison.Ordinal);
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
    public async Task Describing_Against_A_Dead_Target_Fails_With_The_Transport_Reason()
    {
        // Note what this does NOT assert: that the message names the service.
        // `describe` takes the gRPC fast path when no protocol is pinned, so
        // what comes back is the transport's own status — "Unavailable", with
        // the connection error. That is the useful answer here (the server is
        // not there), and pretending otherwise would pin a message the code
        // does not produce.
        var exit = await CliHandler.DescribeAsync(
            Options(target: "orders.v1.OrderService"), _out, _err);

        Assert.NotEqual(0, exit);
        Assert.NotEmpty(_err.ToString());
        Assert.DoesNotContain("Unhandled exception", _err.ToString(), StringComparison.Ordinal);
    }

    // ---- call ----

    [Fact]
    public async Task Calling_Without_A_Target_Is_A_Usage_Error()
        => Assert.NotEqual(0, await CliHandler.CallAsync(
            Options(), _out, _err, TestContext.Current.CancellationToken));

    [Fact]
    public async Task Calling_Against_A_Dead_Target_Fails_Without_A_Stack_Trace()
    {
        // Same fast path as describe: unpinned, `call` goes straight at gRPC
        // and the transport error is the answer. What matters is that it
        // arrives as one rendered line and a non-zero exit, not as an
        // unhandled exception.
        var exit = await CliHandler.CallAsync(
            Options(target: "orders.v1.OrderService/GetOrder"), _out, _err,
            TestContext.Current.CancellationToken);

        Assert.NotEqual(0, exit);
        Assert.DoesNotContain("Unhandled exception", _err.ToString(), StringComparison.Ordinal);
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

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using ModelContextProtocol.Protocol;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tests;

/// <summary>
/// #966 — <see cref="McpWireOrder"/> delivers notifications in the order the
/// transport read them, whatever order their handlers run in.
/// </summary>
public sealed class McpWireOrderTests
{
    private const string Log = "notifications/message";

    private static JsonRpcNotification Note(string method = Log) => new() { Method = method };

    [Fact]
    public void Notifications_Handled_Out_Of_Order_Come_Out_In_Wire_Order()
    {
        var order = new McpWireOrder(Log);
        order.Start();
        var notes = Enumerable.Range(0, 4).Select(_ => Note()).ToList();
        foreach (var note in notes) order.Read(note);

        var seen = new List<int>();
        foreach (var i in new[] { 2, 0, 3, 1 }) order.Deliver(notes[i], () => seen.Add(i));

        Assert.Equal([0, 1, 2, 3], seen);
    }

    [Fact]
    public async Task Delivered_Waits_For_Every_Notification_Read_So_Far()
    {
        var order = new McpWireOrder(Log);
        order.Start();
        var first = Note();
        var second = Note();
        order.Read(first);
        order.Read(second);

        order.Deliver(second, () => { });
        var delivered = order.DeliveredAsync(TestContext.Current.CancellationToken);
        Assert.False(delivered.IsCompleted);

        order.Deliver(first, () => { });
        await delivered;
    }

    [Fact]
    public async Task Delivered_Is_Complete_At_Once_When_Nothing_Is_Outstanding()
    {
        var order = new McpWireOrder(Log);
        order.Start();
        await order.DeliveredAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_Notification_Read_Before_Start_Is_Delivered_At_Once_And_Leaves_No_Gap()
    {
        var order = new McpWireOrder(Log);
        var early = Note();
        order.Read(early);
        order.Start();

        var seen = false;
        order.Deliver(early, () => seen = true);

        Assert.True(seen);
        await order.DeliveredAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Other_Methods_Are_Not_Numbered()
    {
        var order = new McpWireOrder(Log);
        order.Start();
        var progress = Note("notifications/progress");
        order.Read(progress);

        var seen = false;
        order.Deliver(progress, () => seen = true);

        Assert.True(seen);
        await order.DeliveredAsync(TestContext.Current.CancellationToken);
    }
}

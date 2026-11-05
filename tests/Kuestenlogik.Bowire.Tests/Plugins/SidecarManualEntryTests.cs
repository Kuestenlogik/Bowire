// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.ManualEntry;
using Kuestenlogik.Bowire.ManualEntry.V1;
using Kuestenlogik.Bowire.Plugins.Sidecar;
using Xunit;

namespace Kuestenlogik.Bowire.Tests.Plugins;

/// <summary>
/// #1003 — a sidecar describes how a call to it is entered by hand in its
/// <c>initialize</c> reply, and the ManualEntry service serves that form like
/// a .NET plugin's. A sidecar cannot implement <see cref="IBowireManualEntry"/>
/// itself; <see cref="SidecarBowireProtocol"/> speaks for it.
/// </summary>
public sealed class SidecarManualEntryTests
{
    private static SidecarBowireProtocol BuildPlugin(bool advertiseManualEntry)
    {
        var exe = SidecarFake.Locate();
        var manifest = new SidecarPluginManifest(
            PackageId: "Kuestenlogik.Bowire.Tests.SidecarFake",
            Protocol: new SidecarProtocolMetadata("fake", "Fake"),
            Executable: Path.GetFileName(exe),
            Args: advertiseManualEntry ? ["--manual-entry"] : null,
            EnvPrefix: "BOWIRE_FAKE_",
            ShutdownTimeoutMs: 2000);
        var plugin = new SidecarBowireProtocol(manifest, Path.GetDirectoryName(exe)!);
        plugin.Initialize(null);
        return plugin;
    }

    [Fact]
    public async Task The_Form_In_The_Initialize_Reply_Becomes_The_Plugins_Form()
    {
        var plugin = BuildPlugin(advertiseManualEntry: true);
        try
        {
            await plugin.PrepareSettingsAsync(TestContext.Current.CancellationToken);

            var form = plugin.DescribeManualEntry();

            Assert.NotNull(form);
            Assert.Equal("fake://localhost:1", form!.UrlPlaceholder);
            // The call kind without an id is dropped; a missing label is the id.
            Assert.Collection(form.CallKinds,
                send => Assert.Equal(("send", "Send", "Unary", "{topic}", "send"),
                    (send.Id, send.Label, send.MethodType, send.Service, send.Method)),
                watch => Assert.Equal(("watch", "watch", "ServerStreaming"),
                    (watch.Id, watch.Label, watch.MethodType)));
            // The field without a key is dropped; placement and options map.
            Assert.Collection(form.Fields,
                topic =>
                {
                    Assert.Equal(BowireManualFieldPlacement.Template, topic.Placement);
                    Assert.True(topic.Required);
                },
                priority =>
                {
                    Assert.Equal(BowireManualFieldPlacement.Metadata, priority.Placement);
                    Assert.Equal("select", priority.Type);
                    Assert.Equal(["send"], priority.CallKinds);
                    Assert.Equal([("high", "High"), ("low", "low")], priority.Options.Select(o => (o.Value, o.Label)));
                });
        }
        finally
        {
            await SidecarFake.ShutdownAsync(plugin);
        }
    }

    [Fact]
    public async Task The_Service_Serves_The_Sidecars_Form()
    {
        var plugin = BuildPlugin(advertiseManualEntry: true);
        try
        {
            // What the gRPC service does before it lists: prepare, then describe.
            await plugin.PrepareSettingsAsync(TestContext.Current.CancellationToken);
            var response = ManualEntryRpc.List([plugin], new ListManualEntryFormsRequest());

            var form = Assert.Single(response.Forms);
            Assert.Equal("fake", form.Protocol);
            Assert.Equal(["send", "watch"], form.CallKinds.Select(k => k.Id));
        }
        finally
        {
            await SidecarFake.ShutdownAsync(plugin);
        }
    }

    [Fact]
    public async Task A_Sidecar_Describing_No_Form_Describes_Nothing()
    {
        var plugin = BuildPlugin(advertiseManualEntry: false);
        try
        {
            await plugin.PrepareSettingsAsync(TestContext.Current.CancellationToken);

            Assert.Null(plugin.DescribeManualEntry());
            Assert.Empty(ManualEntryRpc.List([plugin], new ListManualEntryFormsRequest()).Forms);
        }
        finally
        {
            await SidecarFake.ShutdownAsync(plugin);
        }
    }

    [Fact]
    public void Before_The_Handshake_There_Is_No_Form()
    {
        // The form lives in a process that has not been started.
        var plugin = BuildPlugin(advertiseManualEntry: true);

        Assert.Null(plugin.DescribeManualEntry());
    }

    [Fact]
    public void A_Form_Without_A_Usable_Call_Kind_Is_None()
    {
        Assert.Null(SidecarManualEntryMapper.ToHostForm(null));
        Assert.Null(SidecarManualEntryMapper.ToHostForm(new SidecarManualEntry(CallKinds: [new SidecarManualCallKind(Id: "")])));
    }
}

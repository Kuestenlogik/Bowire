// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Coverage for <see cref="BowireVersionReport"/> — the text behind
/// <c>bowire version [--plugins]</c>. The version + runtime lines and the
/// protocol-plugin table are asserted without spinning the command pipeline.
/// </summary>
public sealed class BowireVersionReportTests
{
    private sealed class FakeProtocol(string id, string name) : IBowireProtocol
    {
        public string Name => name;
        public string Id => id;
        public string IconSvg => string.Empty;
        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public void AppVersion_Is_Not_Empty()
    {
        Assert.False(string.IsNullOrWhiteSpace(BowireVersionReport.AppVersion()));
    }

    [Fact]
    public void Render_Without_Plugins_Has_Version_And_Runtime_Only()
    {
        var text = BowireVersionReport.Render(includePlugins: false, []);

        Assert.StartsWith("Bowire ", text, StringComparison.Ordinal);
        Assert.Contains(".NET", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Protocol plugins", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_With_Plugins_Lists_Protocols_Sorted_By_Id()
    {
        var text = BowireVersionReport.Render(includePlugins: true,
            [new FakeProtocol("rest", "REST"), new FakeProtocol("grpc", "gRPC")]);

        Assert.Contains("Protocol plugins (2):", text, StringComparison.Ordinal);
        // Ordinal sort → grpc before rest, regardless of input order.
        Assert.True(text.IndexOf("grpc", StringComparison.Ordinal) < text.IndexOf("rest", StringComparison.Ordinal));
        Assert.Contains("gRPC", text, StringComparison.Ordinal);
        Assert.Contains("REST", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_With_Plugins_But_None_Loaded_Shows_Zero_And_Placeholder()
    {
        var text = BowireVersionReport.Render(includePlugins: true, []);

        Assert.Contains("Protocol plugins (0):", text, StringComparison.Ordinal);
        Assert.Contains("(none)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Protocols_Dedupes_By_Id()
    {
        var protocols = BowireVersionReport.Protocols(
            [new FakeProtocol("grpc", "gRPC"), new FakeProtocol("grpc", "gRPC (dup)")]);

        var only = Assert.Single(protocols);
        Assert.Equal("grpc", only.Id);
    }

    [Fact]
    public void Protocols_Falls_Back_To_Id_When_Name_Is_Empty()
    {
        var protocols = BowireVersionReport.Protocols([new FakeProtocol("grpc", string.Empty)]);
        Assert.Equal("grpc", Assert.Single(protocols).Name);
    }
}

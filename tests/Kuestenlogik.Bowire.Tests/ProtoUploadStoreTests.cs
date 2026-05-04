// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit tests for the <see cref="ProtoUploadStore"/> — the in-memory
/// container that the upload endpoint feeds with user-supplied .proto
/// payloads and the discovery endpoint reads back as services. The store
/// is process-static, so each test starts and ends with <c>Clear()</c> to
/// avoid cross-test bleed; xunit.v3 serialises test methods within a
/// single class by default so the per-test reset is enough.
/// </summary>
public sealed class ProtoUploadStoreTests : IDisposable
{
    public ProtoUploadStoreTests() => ProtoUploadStore.Clear();

    public void Dispose()
    {
        ProtoUploadStore.Clear();
        GC.SuppressFinalize(this);
    }

    private const string SampleProto = """
        syntax = "proto3";

        package demo;

        service GreetService {
            rpc SayHello (HelloRequest) returns (HelloReply);
        }

        message HelloRequest {
            string name = 1;
        }

        message HelloReply {
            string message = 1;
        }
        """;

    [Fact]
    public void Empty_Store_Has_No_Uploads_And_No_Services()
    {
        Assert.False(ProtoUploadStore.HasUploads);
        Assert.Empty(ProtoUploadStore.GetServices());
    }

    [Fact]
    public void AddAndParse_Returns_Discovered_Services()
    {
        var services = ProtoUploadStore.AddAndParse(SampleProto);

        Assert.NotEmpty(services);
        // Service names are package-qualified by ProtoFileParser.
        Assert.Contains(services, s => s.Name == "demo.GreetService");
        Assert.True(ProtoUploadStore.HasUploads);
    }

    [Fact]
    public void GetServices_Caches_Across_Calls()
    {
        ProtoUploadStore.AddAndParse(SampleProto);

        var first = ProtoUploadStore.GetServices();
        var second = ProtoUploadStore.GetServices();

        // The second call must return the cached instance — same reference,
        // not just equal content. The discovery endpoint hits this on every
        // request and the cost of re-parsing on every miss would matter for
        // multi-megabyte proto bundles.
        Assert.Same(first, second);
    }

    [Fact]
    public void AddAndParse_Invalidates_Cache()
    {
        ProtoUploadStore.AddAndParse(SampleProto);
        var first = ProtoUploadStore.GetServices();

        const string Second = """
            syntax = "proto3";
            package other;
            service WidgetService {
                rpc GetWidget (WidgetRequest) returns (WidgetReply);
            }
            message WidgetRequest { string id = 1; }
            message WidgetReply { string name = 1; }
            """;
        var afterSecond = ProtoUploadStore.AddAndParse(Second);

        Assert.NotSame(first, afterSecond);
        Assert.Contains(afterSecond, s => s.Name == "demo.GreetService");
        Assert.Contains(afterSecond, s => s.Name == "other.WidgetService");
    }

    [Fact]
    public void Clear_Drops_Uploads_And_Cache()
    {
        ProtoUploadStore.AddAndParse(SampleProto);
        Assert.True(ProtoUploadStore.HasUploads);

        ProtoUploadStore.Clear();

        Assert.False(ProtoUploadStore.HasUploads);
        Assert.Empty(ProtoUploadStore.GetServices());
    }

    [Fact]
    public void Clear_Is_Idempotent()
    {
        // Clear() on an already-empty store must not throw — the upload
        // endpoint may invoke it defensively before re-uploading from a
        // fresh state.
        ProtoUploadStore.Clear();
        ProtoUploadStore.Clear();

        Assert.False(ProtoUploadStore.HasUploads);
    }

    [Fact]
    public void HasUploads_Tracks_Add_And_Clear_Independently()
    {
        Assert.False(ProtoUploadStore.HasUploads);

        ProtoUploadStore.AddAndParse(SampleProto);
        Assert.True(ProtoUploadStore.HasUploads);

        ProtoUploadStore.Clear();
        Assert.False(ProtoUploadStore.HasUploads);
    }
}

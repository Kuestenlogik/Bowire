// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Unit tests for the #404 per-stub CRUD on <see cref="MockHandler"/> —
/// add / get / update / remove / reset, all copy-on-write.
/// </summary>
public sealed class MockHandlerStubCrudTests
{
    private static BowireRecordingStep Stub(string id, string path, string response) => new()
    {
        Id = id, Protocol = "rest", Service = "S", Method = "M", MethodType = "Unary",
        HttpPath = path, HttpVerb = "GET", Status = "OK", Response = response,
    };

    private static MockHandler NewHandler(params BowireRecordingStep[] steps)
    {
        var rec = new BowireRecording { RecordingFormatVersion = 2 };
        foreach (var s in steps) rec.Steps.Add(s);
        return new MockHandler(rec, new MockOptions(), NullLogger.Instance);
    }

    [Fact]
    public void Add_AssignsId_WhenMissing_AndAppears_InList()
    {
        var h = NewHandler();
        var created = h.AddStub(Stub(id: "", path: "/a", response: "{}"));
        Assert.False(string.IsNullOrEmpty(created.Id));
        Assert.Single(h.ListStubs());
        Assert.Same(created, h.GetStub(created.Id));
    }

    [Fact]
    public void Update_ReplacesStep_PreservingId()
    {
        var h = NewHandler(Stub("s1", "/a", """{"v":1}"""));
        var ok = h.UpdateStub("s1", Stub("ignored", "/a", """{"v":2}"""));
        Assert.True(ok);
        var s = h.GetStub("s1");
        Assert.NotNull(s);
        Assert.Equal("s1", s!.Id); // id kept from the URL, not the body
        Assert.Equal("""{"v":2}""", s.Response);

        Assert.False(h.UpdateStub("nope", Stub("x", "/b", "{}")));
    }

    [Fact]
    public void Remove_DropsStep()
    {
        var h = NewHandler(Stub("s1", "/a", "{}"), Stub("s2", "/b", "{}"));
        Assert.True(h.RemoveStub("s1"));
        Assert.Single(h.ListStubs());
        Assert.Null(h.GetStub("s1"));
        Assert.False(h.RemoveStub("s1")); // already gone
    }

    [Fact]
    public void Reset_RestoresBaseline()
    {
        var h = NewHandler(Stub("base", "/a", "{}"));
        h.AddStub(Stub("added", "/b", "{}"));
        h.RemoveStub("base");
        Assert.Single(h.ListStubs());
        Assert.Equal("added", h.ListStubs()[0].Id);

        h.ResetStubs();
        var afterReset = h.ListStubs();
        Assert.Single(afterReset);
        Assert.Equal("base", afterReset[0].Id);
    }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.PluginLoading;

namespace Kuestenlogik.Bowire.Mock.Tests;

public class MissingPluginDetectorTests
{
    private static BowireRecording RecordingWith(params string[] protocolsByStep)
    {
        var rec = new BowireRecording { Name = "test", RecordingFormatVersion = RecordingFormatVersion.Current };
        foreach (var p in protocolsByStep)
        {
            rec.Steps.Add(new BowireRecordingStep { Protocol = p, Service = "Svc", Method = "M" });
        }
        return rec;
    }

    [Fact]
    public void Detect_AllLoaded_ReturnsEmpty()
    {
        var rec = RecordingWith("grpc", "rest");
        var missing = MissingPluginDetector.Detect(rec, ["grpc", "rest"]);
        Assert.Empty(missing);
    }

    [Fact]
    public void Detect_SomeMissing_FlagsThemWithSuggestedPackage()
    {
        var rec = RecordingWith("grpc", "surgewave", "rest");
        var missing = MissingPluginDetector.Detect(rec, ["grpc", "rest"]);

        var single = Assert.Single(missing);
        Assert.Equal("surgewave", single.ProtocolId);
        Assert.Equal("Kuestenlogik.Bowire.Protocol.Surgewave", single.SuggestedPackageId);
    }

    [Fact]
    public void Detect_DistinctOnly_DropsDuplicates()
    {
        // Same protocol in three steps shows up once.
        var rec = RecordingWith("kafka", "kafka", "kafka");
        var missing = MissingPluginDetector.Detect(rec, []);
        Assert.Single(missing);
        Assert.Equal("kafka", missing[0].ProtocolId);
    }

    [Fact]
    public void Detect_UnknownProtocol_SuggestedPackageIsNull()
    {
        // Custom / third-party plugin not in Bowire's catalogue.
        var rec = RecordingWith("acme-frob");
        var missing = MissingPluginDetector.Detect(rec, []);
        var single = Assert.Single(missing);
        Assert.Equal("acme-frob", single.ProtocolId);
        Assert.Null(single.SuggestedPackageId);
    }

    [Fact]
    public void Detect_StepWithEmptyProtocol_Ignored()
    {
        var rec = RecordingWith("grpc", "");
        var missing = MissingPluginDetector.Detect(rec, ["grpc"]);
        Assert.Empty(missing);
    }

    [Fact]
    public void Detect_PreservesFirstOccurrenceOrder()
    {
        var rec = RecordingWith("kafka", "surgewave", "rest", "kafka");
        var missing = MissingPluginDetector.Detect(rec, []);
        Assert.Equal(["kafka", "surgewave", "rest"], missing.Select(m => m.ProtocolId));
    }
}

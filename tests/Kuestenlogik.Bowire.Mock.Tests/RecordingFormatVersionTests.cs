// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Mock.Tests;

public sealed class RecordingFormatVersionTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]    // Phase 1b: gRPC-unary replay ships v2
    [InlineData(3, false)]   // future format, not yet supported
    [InlineData(0, false)]   // garbage
    [InlineData(-1, false)]  // garbage
    public void IsSupported_OnIntegerInput_MatchesSupportedVersions(int version, bool expected)
    {
        Assert.Equal(expected, RecordingFormatVersion.IsSupported(version));
    }

    [Fact]
    public void IsSupported_OnMissingVersion_RejectsAsPreRelease()
    {
        // Bowire has not shipped a release without the version field, so a
        // null version reads as "recorded by a build older than the mock
        // expects" → reject outright.
        Assert.False(RecordingFormatVersion.IsSupported(null));
    }

    [Fact]
    public void Current_EqualsV2_InPhase1b()
    {
        Assert.Equal(RecordingFormatVersion.V2, RecordingFormatVersion.Current);
    }
}

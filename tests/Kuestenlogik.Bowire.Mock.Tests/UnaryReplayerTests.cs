// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mock.Replay;

namespace Kuestenlogik.Bowire.Mock.Tests;

public sealed class UnaryReplayerTests
{
    [Theory]
    [InlineData("OK", 200)]
    [InlineData("ok", 200)]
    [InlineData("NotFound", 404)]
    [InlineData("InternalServerError", 500)]
    [InlineData("204", 204)]
    [InlineData("404", 404)]
    [InlineData(null, 200)]
    [InlineData("", 200)]
    [InlineData("garbage-not-a-status", 200)] // lenient fallback
    public void MapStatus_OnHttpNamesAndNumerics_Works(string? input, int expected)
    {
        Assert.Equal(expected, UnaryReplayer.MapStatus(input));
    }

    [Theory]
    [InlineData("Cancelled", 499)]
    [InlineData("InvalidArgument", 400)]
    [InlineData("DeadlineExceeded", 504)]
    [InlineData("PermissionDenied", 403)]
    [InlineData("ResourceExhausted", 429)]
    [InlineData("Unimplemented", 501)]
    [InlineData("Unavailable", 503)]
    [InlineData("Unauthenticated", 401)]
    public void MapStatus_OnGrpcStatusNames_MapsToHttp(string input, int expected)
    {
        Assert.Equal(expected, UnaryReplayer.MapStatus(input));
    }

    [Theory]
    [InlineData("OK", 0)]
    [InlineData("ok", 0)]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("NotFound", 5)]
    [InlineData("InvalidArgument", 3)]
    [InlineData("Unauthenticated", 16)]
    [InlineData("garbage", 0)] // lenient: unknown → OK
    public void MapToGrpcStatus_CoversSpecCodes(string? input, int expectedCode)
    {
        Assert.Equal(expectedCode, UnaryReplayer.MapToGrpcStatus(input).Code);
    }
}

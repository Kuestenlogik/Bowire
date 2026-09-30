// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Internal markers ride the same metadata bag as headers; <see cref="BowireMetadataKeys.WireHeaders"/>
/// is what every plugin puts on the wire, so a marker — the mTLS one carries a private key —
/// never reaches the target as a header.
/// </summary>
public sealed class BowireMetadataKeysTests
{
    [Theory]
    [InlineData("__bowireMtls__")]
    [InlineData("__bowireAwsSigV4__")]
    [InlineData("__bowireCookieEnv__")]
    [InlineData("__bowireQuery__api_key")]
    [InlineData("__BOWIREMTLS__")]
    [InlineData(BowireMetadataKeys.PluginHint)]
    [InlineData(BowireMetadataKeys.GrpcTransport)]
    public void IsInternal_Recognises_Every_Marker(string key) =>
        Assert.True(BowireMetadataKeys.IsInternal(key));

    [Theory]
    [InlineData("Authorization")]
    [InlineData("x-bowire-trace")]
    [InlineData("_bowire")]
    public void IsInternal_Leaves_Real_Headers_Alone(string key) =>
        Assert.False(BowireMetadataKeys.IsInternal(key));

    [Fact]
    public void WireHeaders_Drops_Markers_And_Keeps_Order()
    {
        var metadata = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer x",
            ["__bowireMtls__"] = "{\"privateKey\":\"secret\"}",
            ["X-Trace"] = "1",
        };

        var wire = BowireMetadataKeys.WireHeaders(metadata).Select(kv => kv.Key).ToArray();

        Assert.Equal(["Authorization", "X-Trace"], wire);
    }

    [Fact]
    public void WireHeaders_Of_Null_Is_Empty() =>
        Assert.Empty(BowireMetadataKeys.WireHeaders(null));
}

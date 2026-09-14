// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Mqtt;

namespace Kuestenlogik.Bowire.Protocol.Mqtt.Tests;

/// <summary>
/// #664 — MQTT names a method by its topic and routes the FullName as
/// <c>mqtt/&lt;topic&gt;/publish|subscribe</c>; the invoke paths read the
/// topic. Both forms arrive as the topic.
/// </summary>
public sealed class MethodNameContractTests
{
    [Theory]
    [InlineData("sensors", "sensors/temperature", "sensors/temperature")]
    [InlineData("sensors", "mqtt/sensors/temperature/publish", "sensors/temperature")]
    [InlineData("sensors", "mqtt/sensors/temperature/subscribe", "sensors/temperature")]
    [InlineData("sensors", "mqtt/publish", "mqtt/publish")]
    public void Name_And_FullName_Both_Resolve_To_The_Topic(string service, string sent, string expected)
    {
        var p = new BowireMqttProtocol();
        Assert.Equal(expected, p.ResolveMethodName(service, sent));
    }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.Protocol.Sse;

namespace Kuestenlogik.Bowire.Tests;

public class SseEndpointAttributeTests
{
    [Fact]
    public void Default_Constructor_Has_Null_Properties()
    {
        var attr = new SseEndpointAttribute();

        Assert.Null(attr.Description);
        Assert.Null(attr.EventType);
    }

    [Fact]
    public void Properties_Can_Be_Set_Via_Initializer()
    {
        var attr = new SseEndpointAttribute
        {
            Description = "Live ticks",
            EventType = "price,volume",
        };

        Assert.Equal("Live ticks", attr.Description);
        Assert.Equal("price,volume", attr.EventType);
    }

    [Fact]
    public void Has_AttributeUsage_Method_Only()
    {
        var usage = typeof(SseEndpointAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Method, usage!.ValidOn);
    }

    [Fact]
    public void Type_Is_Sealed()
    {
        Assert.True(typeof(SseEndpointAttribute).IsSealed);
    }
}

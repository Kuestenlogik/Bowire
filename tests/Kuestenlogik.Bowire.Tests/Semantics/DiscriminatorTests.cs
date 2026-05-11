// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Semantics;

namespace Kuestenlogik.Bowire.Tests.Semantics;

public sealed class DiscriminatorTests
{
    [Fact]
    public void Construction_Captures_Kind_And_Value()
    {
        var d = new Discriminator(DiscriminatorKinds.WirePath, "byte[1]");
        Assert.Equal("wirePath", d.Kind);
        Assert.Equal("byte[1]", d.Value);
    }

    [Fact]
    public void None_Singleton_Has_Empty_Value()
    {
        Assert.Equal(DiscriminatorKinds.None, Discriminator.None.Kind);
        Assert.Equal(string.Empty, Discriminator.None.Value);
        Assert.True(Discriminator.None.IsNone);
    }

    [Fact]
    public void IsNone_Is_False_For_Real_Discriminators()
    {
        Assert.False(new Discriminator(DiscriminatorKinds.WirePath, "byte[1]").IsNone);
        Assert.False(new Discriminator(DiscriminatorKinds.JsonPath, "$.type").IsNone);
        Assert.False(new Discriminator(DiscriminatorKinds.Oneof, "payload").IsNone);
    }

    [Fact]
    public void Kinds_Constants_Match_Spec()
    {
        // Wire format the ADR pins under "Discriminator declaration."
        Assert.Equal("wirePath", DiscriminatorKinds.WirePath);
        Assert.Equal("jsonPath", DiscriminatorKinds.JsonPath);
        Assert.Equal("oneof", DiscriminatorKinds.Oneof);
        Assert.Equal("none", DiscriminatorKinds.None);
    }
}

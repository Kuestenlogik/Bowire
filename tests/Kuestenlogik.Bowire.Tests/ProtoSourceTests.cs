// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for <see cref="ProtoSource"/>, the small DTO that distinguishes
/// inline proto content from a file-path reference. The class is a pair
/// of factory methods plus two settable string properties, but it's
/// part of the gRPC discovery surface — pin both the factory shape and
/// the property defaults so the consumer code keeps a stable contract.
/// </summary>
public sealed class ProtoSourceTests
{
    [Fact]
    public void Default_Construction_Leaves_Both_Slots_Null()
    {
        // The default ctor stays public + settable so embedded hosts
        // can deserialise it from configuration; document that shape.
        var src = new ProtoSource();

        Assert.Null(src.Content);
        Assert.Null(src.FilePath);
    }

    [Fact]
    public void FromContent_Captures_Content_And_Leaves_FilePath_Null()
    {
        const string Proto = """
            syntax = "proto3";
            package demo;
            message Ping { string text = 1; }
            """;

        var src = ProtoSource.FromContent(Proto);

        Assert.Equal(Proto, src.Content);
        Assert.Null(src.FilePath);
    }

    [Fact]
    public void FromFile_Captures_Path_And_Leaves_Content_Null()
    {
        const string Path = "/etc/bowire/protos/echo.proto";

        var src = ProtoSource.FromFile(Path);

        Assert.Equal(Path, src.FilePath);
        Assert.Null(src.Content);
    }

    [Fact]
    public void Properties_Are_Mutable_For_Configuration_Binding()
    {
        // IConfiguration / System.Text.Json binding needs the setters
        // to actually take. Pin both slots round-trip through the
        // setter / getter pair.
        var src = new ProtoSource
        {
            Content = "syntax = \"proto3\";",
            FilePath = "demo.proto",
        };

        Assert.Equal("syntax = \"proto3\";", src.Content);
        Assert.Equal("demo.proto", src.FilePath);
    }

    [Fact]
    public void FromContent_And_FromFile_Return_Distinct_Instances()
    {
        // Factories are not memoised — each call produces a new
        // mutable instance so callers can scribble on it without
        // sharing state.
        var a = ProtoSource.FromContent("syntax = \"proto3\";");
        var b = ProtoSource.FromContent("syntax = \"proto3\";");

        Assert.NotSame(a, b);
        Assert.Equal(a.Content, b.Content);
    }
}

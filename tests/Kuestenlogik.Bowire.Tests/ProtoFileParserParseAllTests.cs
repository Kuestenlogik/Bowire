// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Integration-style tests for the <see cref="ProtoFileParser.ParseAll"/>
/// path that resolves a list of <see cref="ProtoSource"/> entries —
/// content / file / unset — and forwards each non-empty result to the
/// per-source parser. The single-source <c>Parse</c> path is exercised
/// extensively in <c>ProtoFileParserTests</c>; this class fills in the
/// orchestration layer so the <c>options.ProtoSources</c> wiring on
/// <see cref="BowireOptions"/> has at least one direct unit test.
/// </summary>
public class ProtoFileParserParseAllTests
{
    private const string GreeterProto = """
        syntax = "proto3";
        package demo;
        service Greeter {
            rpc SayHello (HelloRequest) returns (HelloReply);
        }
        message HelloRequest { string name = 1; }
        message HelloReply { string message = 1; }
        """;

    private const string WidgetProto = """
        syntax = "proto3";
        package widgets;
        service WidgetService {
            rpc GetWidget (WidgetRequest) returns (WidgetReply);
        }
        message WidgetRequest { string id = 1; }
        message WidgetReply { string name = 1; }
        """;

    [Fact]
    public void ParseAll_Empty_List_Returns_Empty()
    {
        var result = ProtoFileParser.ParseAll(new List<ProtoSource>());
        Assert.Empty(result);
    }

    [Fact]
    public void ParseAll_Inline_Content_Source_Parses_Services()
    {
        var sources = new List<ProtoSource>
        {
            ProtoSource.FromContent(GreeterProto),
        };

        var result = ProtoFileParser.ParseAll(sources);

        Assert.NotEmpty(result);
        Assert.Contains(result, s => s.Name == "demo.Greeter");
    }

    [Fact]
    public void ParseAll_File_Path_Source_Reads_Disk_And_Parses()
    {
        var temp = Path.Combine(Path.GetTempPath(), "bowire-parseall-" + Guid.NewGuid().ToString("N") + ".proto");
        File.WriteAllText(temp, WidgetProto);
        try
        {
            var sources = new List<ProtoSource>
            {
                ProtoSource.FromFile(temp),
            };

            var result = ProtoFileParser.ParseAll(sources);

            Assert.NotEmpty(result);
            Assert.Contains(result, s => s.Name == "widgets.WidgetService");
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void ParseAll_Source_With_Both_Content_And_FilePath_Prefers_Content()
    {
        // ResolveContent checks Content first — when both are set the
        // inline content wins so the parser never reads the (possibly
        // stale) file. The contract pin: a non-existent FilePath is
        // safely ignored when Content is non-empty.
        var nonExistent = Path.Combine(Path.GetTempPath(), "no-such-file-" + Guid.NewGuid().ToString("N") + ".proto");
        var source = new ProtoSource
        {
            Content = GreeterProto,
            FilePath = nonExistent,
        };

        var result = ProtoFileParser.ParseAll(new List<ProtoSource> { source });

        Assert.Contains(result, s => s.Name == "demo.Greeter");
    }

    [Fact]
    public void ParseAll_Empty_Source_Is_Skipped()
    {
        // ProtoSource with neither Content nor FilePath returns empty
        // from ResolveContent, which the IsNullOrWhiteSpace guard skips
        // without raising. A second source after it still parses.
        var sources = new List<ProtoSource>
        {
            new(),
            ProtoSource.FromContent(WidgetProto),
        };

        var result = ProtoFileParser.ParseAll(sources);

        Assert.Contains(result, s => s.Name == "widgets.WidgetService");
    }

    [Fact]
    public void ParseAll_Missing_File_Path_Is_Skipped()
    {
        // FilePath that doesn't exist returns empty — the source is
        // silently skipped (rather than raising) to keep the workbench
        // up when a configured proto file disappears.
        var nonExistent = Path.Combine(Path.GetTempPath(), "ghost-" + Guid.NewGuid().ToString("N") + ".proto");
        var sources = new List<ProtoSource>
        {
            ProtoSource.FromFile(nonExistent),
            ProtoSource.FromContent(GreeterProto),
        };

        var result = ProtoFileParser.ParseAll(sources);

        Assert.Single(result);
        Assert.Equal("demo.Greeter", result[0].Name);
    }

    [Fact]
    public void ParseAll_Multiple_Sources_Concatenates_Services_In_Order()
    {
        var sources = new List<ProtoSource>
        {
            ProtoSource.FromContent(GreeterProto),
            ProtoSource.FromContent(WidgetProto),
        };

        var result = ProtoFileParser.ParseAll(sources);

        Assert.Equal(2, result.Count);
        Assert.Equal("demo.Greeter", result[0].Name);
        Assert.Equal("widgets.WidgetService", result[1].Name);
    }
}

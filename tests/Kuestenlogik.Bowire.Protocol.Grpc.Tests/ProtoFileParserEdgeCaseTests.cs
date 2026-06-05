// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.Grpc.Tests;

/// <summary>
/// Edge-case tests for <see cref="ProtoFileParser"/> — covers the
/// branches the main parser test file leaves uncovered: file-backed
/// <see cref="ProtoSource"/>, enum parsing, MapProtoType numeric
/// variants, message reference resolution against current message vs
/// package prefix, service-type resolution stubs for unresolved types,
/// keyword-skip in the field regex, and the brace-mismatch fallback.
/// </summary>
public sealed class ProtoFileParserEdgeCaseTests : IDisposable
{
    private readonly string _tempDir;

    public ProtoFileParserEdgeCaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"bowire-proto-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ParseAll_Reads_FilePath_When_Content_Is_Empty()
    {
        // ResolveContent prefers Content but falls back to FilePath +
        // File.ReadAllText. This guards the file-backed branch the
        // existing Content-only tests don't reach.
        var path = Path.Combine(_tempDir, "service.proto");
        File.WriteAllText(path, """
            syntax = "proto3";
            package edge.test;
            message Probe { int32 value = 1; }
            service ProbeService {
              rpc Ping(Probe) returns (Probe);
            }
            """);

        var services = ProtoFileParser.ParseAll([ProtoSource.FromFile(path)]);

        var svc = Assert.Single(services);
        Assert.Equal("edge.test.ProbeService", svc.Name);
        Assert.Single(svc.Methods);
    }

    [Fact]
    public void ParseAll_Returns_Empty_When_FilePath_Missing_And_No_Content()
    {
        // Both Content + FilePath empty → ResolveContent returns ""
        // → no services parsed. Defensive — the UI hands us whatever
        // BowireOptions.ProtoSources contained.
        var services = ProtoFileParser.ParseAll([new ProtoSource { Content = null, FilePath = null }]);
        Assert.Empty(services);
    }

    [Fact]
    public void ParseAll_Returns_Empty_When_FilePath_Points_At_Nonexistent_File()
    {
        var nope = Path.Combine(_tempDir, "does-not-exist.proto");
        var services = ProtoFileParser.ParseAll([ProtoSource.FromFile(nope)]);
        Assert.Empty(services);
    }

    [Fact]
    public void Parse_Recognises_Numeric_Field_Type_Variants()
    {
        // MapProtoType is a switch with 16 explicit cases plus the
        // TYPE_MESSAGE fallback. The existing suite covers the common
        // ones; this guards the long-tail integer / fixed variants.
        const string Proto = """
            syntax = "proto3";
            message NumericGrab {
              uint32 u32 = 1;
              uint64 u64 = 2;
              sint32 s32 = 3;
              sint64 s64 = 4;
              fixed32 f32 = 5;
              fixed64 f64 = 6;
              sfixed32 sf32 = 7;
              sfixed64 sf64 = 8;
              float f = 9;
              double d = 10;
              bytes raw = 11;
              bool flag = 12;
            }
            service S { rpc Get(NumericGrab) returns (NumericGrab); }
            """;

        var svc = Assert.Single(ProtoFileParser.Parse(Proto));
        var fields = svc.Methods[0].InputType.Fields;
        var byName = fields.ToDictionary(f => f.Name);
        Assert.Equal("TYPE_UINT32", byName["u32"].Type);
        Assert.Equal("TYPE_UINT64", byName["u64"].Type);
        Assert.Equal("TYPE_SINT32", byName["s32"].Type);
        Assert.Equal("TYPE_SINT64", byName["s64"].Type);
        Assert.Equal("TYPE_FIXED32", byName["f32"].Type);
        Assert.Equal("TYPE_FIXED64", byName["f64"].Type);
        Assert.Equal("TYPE_SFIXED32", byName["sf32"].Type);
        Assert.Equal("TYPE_SFIXED64", byName["sf64"].Type);
        Assert.Equal("TYPE_FLOAT", byName["f"].Type);
        Assert.Equal("TYPE_DOUBLE", byName["d"].Type);
        Assert.Equal("TYPE_BYTES", byName["raw"].Type);
        Assert.Equal("TYPE_BOOL", byName["flag"].Type);
    }

    [Fact]
    public void Parse_Returns_Stub_Message_For_Unresolved_Field_Reference()
    {
        // An unknown message type lands as a TYPE_MESSAGE field whose
        // MessageType is a stub (typeName, typeName, []). That keeps
        // the workbench rendering instead of crashing on a missing-
        // schema scenario (very common with reflection-light services).
        const string Proto = """
            syntax = "proto3";
            message Outer {
              UnknownThing thing = 1;
            }
            service S { rpc Get(Outer) returns (Outer); }
            """;

        var svc = Assert.Single(ProtoFileParser.Parse(Proto));
        var field = svc.Methods[0].InputType.Fields.Single(f => f.Name == "thing");
        Assert.Equal("TYPE_MESSAGE", field.Type);
        Assert.NotNull(field.MessageType);
        Assert.Equal("UnknownThing", field.MessageType!.Name);
        Assert.Empty(field.MessageType.Fields);
    }

    [Fact]
    public void Parse_Resolves_Sibling_Message_Via_Package_Prefix()
    {
        // ResolveMessageReference looks up "Sibling" by prefixing the
        // current message's package (derived from the dot-trimmed full
        // name). Without the package, "Sibling" wouldn't be found by
        // bare name.
        const string Proto = """
            syntax = "proto3";
            package edge.pkg;
            message Sibling { int32 v = 1; }
            message Container {
              Sibling sib = 1;
            }
            service S { rpc Get(Container) returns (Container); }
            """;

        var svc = Assert.Single(ProtoFileParser.Parse(Proto));
        var field = svc.Methods[0].InputType.Fields.Single(f => f.Name == "sib");
        Assert.Equal("Sibling", field.MessageType!.Name);
        // Stub-vs-resolved: a real resolution carries the message's
        // own fields. The stub fallback would have Fields=[].
        Assert.Single(field.MessageType.Fields);
        Assert.Equal("v", field.MessageType.Fields[0].Name);
    }

    [Fact]
    public void Parse_Service_Type_Resolves_Via_Package_Prefix()
    {
        // ResolveTypeForService tries the unqualified name first, then
        // package-prefixed. Bare "Probe" must resolve to "p.Probe"
        // when the file declares package p;.
        const string Proto = """
            syntax = "proto3";
            package p;
            message Probe { int32 v = 1; }
            service S { rpc Get(Probe) returns (Probe); }
            """;

        var svc = Assert.Single(ProtoFileParser.Parse(Proto));
        Assert.Single(svc.Methods[0].InputType.Fields);
    }

    [Fact]
    public void Parse_Service_Type_Returns_Stub_For_Unknown_Message()
    {
        // Bare type name with no matching message → stub fallback so
        // the rpc still shows up in the UI with a placeholder shape.
        const string Proto = """
            syntax = "proto3";
            service S { rpc Get(Untyped) returns (Untyped); }
            """;

        var svc = Assert.Single(ProtoFileParser.Parse(Proto));
        Assert.Equal("Untyped", svc.Methods[0].InputType.Name);
        Assert.Empty(svc.Methods[0].InputType.Fields);
    }

    [Fact]
    public void Parse_Skips_Field_Tags_That_Collide_With_Map_Field_Numbers()
    {
        // ParseFields runs the map regex first and skips regular-field
        // matches that reuse the same number. Maps decompose into the
        // hidden entry message; without the skip, both regex passes
        // would emit a row per tag.
        const string Proto = """
            syntax = "proto3";
            message Cfg {
              map<string, string> labels = 1;
            }
            service S { rpc Get(Cfg) returns (Cfg); }
            """;

        var svc = Assert.Single(ProtoFileParser.Parse(Proto));
        var fields = svc.Methods[0].InputType.Fields;
        var labels = fields.Single(f => f.Name == "labels");
        Assert.True(labels.IsMap);
        Assert.Equal(1, labels.Number);
    }

    [Fact]
    public void Parse_Empty_Content_Returns_No_Services()
    {
        Assert.Empty(ProtoFileParser.Parse(string.Empty));
    }

    [Fact]
    public void Parse_Top_Level_Enum_Body_Does_Not_Create_Service()
    {
        // Enum-only file has no services to surface. Guards
        // ParseEnums + the no-services-when-zero-methods branch.
        const string Proto = """
            syntax = "proto3";
            package e;
            enum Severity {
              UNKNOWN = 0;
              INFO = 1;
              WARN = 2;
              ERROR = 3;
            }
            """;

        Assert.Empty(ProtoFileParser.Parse(Proto));
    }

    [Fact]
    public void ParseAll_Combines_Inline_And_File_Sources()
    {
        // Mixed-source scenario — covers the loop in ParseAll over
        // multiple ProtoSource values with different resolution paths.
        var path = Path.Combine(_tempDir, "first.proto");
        File.WriteAllText(path, """
            syntax = "proto3";
            package mix.a;
            message A { int32 v = 1; }
            service ServiceA { rpc Get(A) returns (A); }
            """);

        var sources = new List<ProtoSource>
        {
            ProtoSource.FromFile(path),
            ProtoSource.FromContent("""
                syntax = "proto3";
                package mix.b;
                message B { string v = 1; }
                service ServiceB { rpc Get(B) returns (B); }
                """),
        };

        var services = ProtoFileParser.ParseAll(sources);
        Assert.Equal(2, services.Count);
        Assert.Contains(services, s => s.Name == "mix.a.ServiceA");
        Assert.Contains(services, s => s.Name == "mix.b.ServiceB");
    }
}

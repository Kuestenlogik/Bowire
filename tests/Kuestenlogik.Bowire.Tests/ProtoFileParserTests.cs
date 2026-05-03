// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests;

public class ProtoFileParserTests
{
    private const string WeatherProto = """
        syntax = "proto3";
        package weather;

        service WeatherService {
          rpc GetVessel (GetVesselRequest) returns (Vessel);
          rpc TrackVessel (TrackVesselRequest) returns (stream VesselPosition);
          rpc ReportCargo (stream CargoItem) returns (CargoSummary);
          rpc ManageBerths (stream BerthEvent) returns (stream HarborStatus);
        }

        message GetVesselRequest {
          string imo = 1;
          bool include_history = 2;
        }

        message Vessel {
          string imo = 1;
          string name = 2;
          double latitude = 3;
          double longitude = 4;
        }

        message TrackVesselRequest {
          string imo = 1;
          int32 interval_seconds = 2;
        }

        message VesselPosition {
          double latitude = 1;
          double longitude = 2;
          int64 timestamp = 3;
        }

        message CargoItem {
          string container_id = 1;
          float weight_kg = 2;
        }

        message CargoSummary {
          int32 total_items = 1;
          float total_weight_kg = 2;
        }

        message BerthEvent {
          string berth_id = 1;
          string action = 2;
        }

        message HarborStatus {
          int32 occupied_berths = 1;
          int32 available_berths = 2;
        }
        """;

    [Fact]
    public void Parse_ExtractsPackageName()
    {
        var services = ProtoFileParser.Parse(WeatherProto);

        Assert.Single(services);
        Assert.Equal("weather", services[0].Package);
    }

    [Fact]
    public void Parse_ExtractsServiceName()
    {
        var services = ProtoFileParser.Parse(WeatherProto);

        Assert.Single(services);
        Assert.Equal("weather.WeatherService", services[0].Name);
    }

    [Fact]
    public void Parse_ExtractsAllMethods()
    {
        var services = ProtoFileParser.Parse(WeatherProto);
        var methods = services[0].Methods;

        Assert.Equal(4, methods.Count);
        Assert.Equal("GetVessel", methods[0].Name);
        Assert.Equal("TrackVessel", methods[1].Name);
        Assert.Equal("ReportCargo", methods[2].Name);
        Assert.Equal("ManageBerths", methods[3].Name);
    }

    [Fact]
    public void Parse_DetectsUnaryMethod()
    {
        var services = ProtoFileParser.Parse(WeatherProto);
        var method = services[0].Methods.First(m => m.Name == "GetVessel");

        Assert.False(method.ClientStreaming);
        Assert.False(method.ServerStreaming);
        Assert.Equal("Unary", method.MethodType);
    }

    [Fact]
    public void Parse_DetectsServerStreamingMethod()
    {
        var services = ProtoFileParser.Parse(WeatherProto);
        var method = services[0].Methods.First(m => m.Name == "TrackVessel");

        Assert.False(method.ClientStreaming);
        Assert.True(method.ServerStreaming);
        Assert.Equal("ServerStreaming", method.MethodType);
    }

    [Fact]
    public void Parse_DetectsClientStreamingMethod()
    {
        var services = ProtoFileParser.Parse(WeatherProto);
        var method = services[0].Methods.First(m => m.Name == "ReportCargo");

        Assert.True(method.ClientStreaming);
        Assert.False(method.ServerStreaming);
        Assert.Equal("ClientStreaming", method.MethodType);
    }

    [Fact]
    public void Parse_DetectsDuplexMethod()
    {
        var services = ProtoFileParser.Parse(WeatherProto);
        var method = services[0].Methods.First(m => m.Name == "ManageBerths");

        Assert.True(method.ClientStreaming);
        Assert.True(method.ServerStreaming);
        Assert.Equal("Duplex", method.MethodType);
    }

    [Fact]
    public void Parse_ResolvesInputAndOutputTypes()
    {
        var services = ProtoFileParser.Parse(WeatherProto);
        var method = services[0].Methods.First(m => m.Name == "GetVessel");

        Assert.Equal("GetVesselRequest", method.InputType.Name);
        Assert.Equal("Vessel", method.OutputType.Name);
    }

    [Fact]
    public void Parse_ResolvesMessageFields()
    {
        var services = ProtoFileParser.Parse(WeatherProto);
        var method = services[0].Methods.First(m => m.Name == "GetVessel");
        var input = method.InputType;

        Assert.Equal(2, input.Fields.Count);
        Assert.Equal("imo", input.Fields[0].Name);
        Assert.Equal("TYPE_STRING", input.Fields[0].Type);
        Assert.Equal(1, input.Fields[0].Number);
        Assert.Equal("include_history", input.Fields[1].Name);
        Assert.Equal("TYPE_BOOL", input.Fields[1].Type);
        Assert.Equal(2, input.Fields[1].Number);
    }

    [Fact]
    public void Parse_SetsSourceToProto()
    {
        var services = ProtoFileParser.Parse(WeatherProto);

        Assert.Single(services);
        Assert.Equal("proto", services[0].Source);
    }

    [Fact]
    public void Parse_MethodFullName_HasServicePrefix()
    {
        var services = ProtoFileParser.Parse(WeatherProto);
        var method = services[0].Methods[0];

        Assert.Equal("weather.WeatherService/GetVessel", method.FullName);
    }

    [Fact]
    public void Parse_HandlesNoPackage()
    {
        const string proto = """
            syntax = "proto3";

            service Simple {
              rpc Ping (PingRequest) returns (PingResponse);
            }

            message PingRequest {
              string msg = 1;
            }

            message PingResponse {
              string reply = 1;
            }
            """;

        var services = ProtoFileParser.Parse(proto);

        Assert.Single(services);
        Assert.Equal("Simple", services[0].Name);
        Assert.Equal("", services[0].Package);
    }

    [Fact]
    public void Parse_StripsComments()
    {
        const string proto = """
            syntax = "proto3";
            package test;

            // This is a comment
            service Echo {
              /* Multi-line
                 comment */
              rpc Send (Msg) returns (Msg);
            }

            message Msg {
              string text = 1; // inline comment
            }
            """;

        var services = ProtoFileParser.Parse(proto);

        Assert.Single(services);
        Assert.Single(services[0].Methods);
        Assert.Equal("Send", services[0].Methods[0].Name);
    }

    [Fact]
    public void Parse_HandlesRepeatedFields()
    {
        const string proto = """
            syntax = "proto3";
            package test;

            service ListService {
              rpc GetItems (ItemRequest) returns (ItemList);
            }

            message ItemRequest {
              int32 limit = 1;
            }

            message ItemList {
              repeated string items = 1;
              int32 total_count = 2;
            }
            """;

        var services = ProtoFileParser.Parse(proto);
        var outputType = services[0].Methods[0].OutputType;
        var repeatedField = outputType.Fields.First(f => f.Name == "items");

        Assert.True(repeatedField.IsRepeated);
        Assert.Equal("LABEL_REPEATED", repeatedField.Label);
    }

    [Fact]
    public void Parse_HandlesMapFields()
    {
        const string proto = """
            syntax = "proto3";
            package test;

            service KvService {
              rpc Get (GetRequest) returns (GetResponse);
            }

            message GetRequest {
              string key = 1;
            }

            message GetResponse {
              map<string, string> data = 1;
            }
            """;

        var services = ProtoFileParser.Parse(proto);
        var outputType = services[0].Methods[0].OutputType;
        var mapField = outputType.Fields.First(f => f.Name == "data");

        Assert.True(mapField.IsMap);
        Assert.Contains("map<", mapField.Type);
    }

    [Fact]
    public void Parse_HandlesMultipleServices()
    {
        const string proto = """
            syntax = "proto3";
            package multi;

            service Alpha {
              rpc DoA (Req) returns (Res);
            }

            service Beta {
              rpc DoB (Req) returns (Res);
            }

            message Req { string id = 1; }
            message Res { string result = 1; }
            """;

        var services = ProtoFileParser.Parse(proto);

        Assert.Equal(2, services.Count);
        Assert.Equal("multi.Alpha", services[0].Name);
        Assert.Equal("multi.Beta", services[1].Name);
    }

    [Fact]
    public void ParseAll_CombinesMultipleSources()
    {
        var sources = new List<ProtoSource>
        {
            ProtoSource.FromContent("""
                syntax = "proto3";
                package svc1;
                service One { rpc Call (Req) returns (Res); }
                message Req { string id = 1; }
                message Res { string data = 1; }
                """),
            ProtoSource.FromContent("""
                syntax = "proto3";
                package svc2;
                service Two { rpc Call (Req) returns (Res); }
                message Req { string id = 1; }
                message Res { string data = 1; }
                """)
        };

        var services = ProtoFileParser.ParseAll(sources);

        Assert.Equal(2, services.Count);
        Assert.Equal("svc1.One", services[0].Name);
        Assert.Equal("svc2.Two", services[1].Name);
    }

    [Fact]
    public void ParseAll_SkipsEmptySources()
    {
        var sources = new List<ProtoSource>
        {
            ProtoSource.FromContent(""),
            ProtoSource.FromContent("""
                syntax = "proto3";
                package ok;
                service Valid { rpc Call (Req) returns (Res); }
                message Req { string id = 1; }
                message Res { string data = 1; }
                """),
            ProtoSource.FromFile("nonexistent/path.proto")
        };

        var services = ProtoFileParser.ParseAll(sources);

        Assert.Single(services);
        Assert.Equal("ok.Valid", services[0].Name);
    }

    [Fact]
    public void Parse_NumericFieldTypes()
    {
        const string proto = """
            syntax = "proto3";
            package types;

            service TypeService {
              rpc Check (TypeMsg) returns (TypeMsg);
            }

            message TypeMsg {
              double d = 1;
              float f = 2;
              int32 i32 = 3;
              int64 i64 = 4;
              uint32 u32 = 5;
              uint64 u64 = 6;
              sint32 s32 = 7;
              sint64 s64 = 8;
              fixed32 fx32 = 9;
              fixed64 fx64 = 10;
              sfixed32 sfx32 = 11;
              sfixed64 sfx64 = 12;
              bool b = 13;
              string s = 14;
              bytes by = 15;
            }
            """;

        var services = ProtoFileParser.Parse(proto);
        var fields = services[0].Methods[0].InputType.Fields;

        Assert.Equal(15, fields.Count);
        Assert.Equal("TYPE_DOUBLE", fields[0].Type);
        Assert.Equal("TYPE_FLOAT", fields[1].Type);
        Assert.Equal("TYPE_INT32", fields[2].Type);
        Assert.Equal("TYPE_INT64", fields[3].Type);
        Assert.Equal("TYPE_UINT32", fields[4].Type);
        Assert.Equal("TYPE_UINT64", fields[5].Type);
        Assert.Equal("TYPE_SINT32", fields[6].Type);
        Assert.Equal("TYPE_SINT64", fields[7].Type);
        Assert.Equal("TYPE_FIXED32", fields[8].Type);
        Assert.Equal("TYPE_FIXED64", fields[9].Type);
        Assert.Equal("TYPE_SFIXED32", fields[10].Type);
        Assert.Equal("TYPE_SFIXED64", fields[11].Type);
        Assert.Equal("TYPE_BOOL", fields[12].Type);
        Assert.Equal("TYPE_STRING", fields[13].Type);
        Assert.Equal("TYPE_BYTES", fields[14].Type);
    }

    [Fact]
    public void Parse_NestedMessageReference()
    {
        const string proto = """
            syntax = "proto3";
            package nested;

            service NestedService {
              rpc Get (OuterMsg) returns (OuterMsg);
            }

            message InnerMsg {
              string value = 1;
            }

            message OuterMsg {
              InnerMsg inner = 1;
              string name = 2;
            }
            """;

        var services = ProtoFileParser.Parse(proto);
        var outerType = services[0].Methods[0].InputType;
        var innerField = outerType.Fields.First(f => f.Name == "inner");

        Assert.Equal("TYPE_MESSAGE", innerField.Type);
        Assert.NotNull(innerField.MessageType);
        Assert.Equal("InnerMsg", innerField.MessageType.Name);
    }

    [Fact]
    public void ProtoSource_FromContent_SetsContent()
    {
        var source = ProtoSource.FromContent("test content");

        Assert.Equal("test content", source.Content);
        Assert.Null(source.FilePath);
    }

    [Fact]
    public void ProtoSource_FromFile_SetsFilePath()
    {
        var source = ProtoSource.FromFile("/path/to/file.proto");

        Assert.Equal("/path/to/file.proto", source.FilePath);
        Assert.Null(source.Content);
    }

    [Fact]
    public void BowireServiceInfo_DefaultSourceIsReflection()
    {
        var service = new Models.BowireServiceInfo("svc", "pkg", []);

        Assert.Equal("reflection", service.Source);
    }
}

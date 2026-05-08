// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.SocketIo;
using SocketIOClient;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Direct coverage for the private <c>ExtractPayload</c> helper. The
/// payload-shaping logic decides what users actually see in the
/// streaming pane (the raw <c>["event", arg1, arg2, …]</c> array vs.
/// just the unwrapped argument list), so its branches need test
/// coverage even though we can't reach them through a live server
/// connection without a full Engine.IO peer.
///
/// Reflection is the lightweight path here — the helper is tightly
/// coupled to <c>SocketIOClient.IEventContext</c>, so a fake context
/// implementing that interface drives every code path.
/// </summary>
public sealed class SocketIoExtractPayloadTests
{
    [Fact]
    public void Returns_Null_For_Null_Context()
    {
        Assert.Null(InvokeExtract(null));
    }

    [Fact]
    public void Returns_Null_For_Empty_RawText()
    {
        // The helper guards on string.IsNullOrEmpty so an empty
        // RawText collapses to null without parsing.
        Assert.Null(InvokeExtract(new FakeContext(string.Empty)));
    }

    [Fact]
    public void Non_Array_Json_Returns_Root_Element()
    {
        var result = InvokeExtract(new FakeContext("{\"foo\":\"bar\"}"));

        Assert.NotNull(result);
        var el = result!.Value;
        Assert.Equal(JsonValueKind.Object, el.ValueKind);
        Assert.Equal("bar", el.GetProperty("foo").GetString());
    }

    [Fact]
    public void Array_With_Only_Event_Name_Returns_Null()
    {
        // ["event"] — Skip(1) leaves zero elements, so the helper
        // collapses to null instead of an empty array.
        Assert.Null(InvokeExtract(new FakeContext("[\"event\"]")));
    }

    [Fact]
    public void Array_With_Single_Argument_Returns_Unwrapped_Value()
    {
        // ["event", "payload"] — Skip(1) leaves one element, which
        // the helper unwraps so the user sees the bare value, not
        // a one-element array.
        var result = InvokeExtract(new FakeContext("[\"event\",\"payload\"]"));

        Assert.NotNull(result);
        Assert.Equal("payload", result!.Value.GetString());
    }

    [Fact]
    public void Array_With_Object_Argument_Returns_Object()
    {
        var result = InvokeExtract(new FakeContext("[\"event\",{\"a\":1}]"));

        Assert.NotNull(result);
        var el = result!.Value;
        Assert.Equal(JsonValueKind.Object, el.ValueKind);
        Assert.Equal(1, el.GetProperty("a").GetInt32());
    }

    [Fact]
    public void Array_With_Multiple_Arguments_Returns_Argument_Array()
    {
        // ["event", "a", "b"] — multi-arg case keeps the array shape
        // because there's no obvious single value to unwrap.
        var result = InvokeExtract(new FakeContext("[\"event\",\"a\",\"b\"]"));

        Assert.NotNull(result);
        var el = result!.Value;
        Assert.Equal(JsonValueKind.Array, el.ValueKind);
        Assert.Equal(2, el.GetArrayLength());
        Assert.Equal("a", el[0].GetString());
        Assert.Equal("b", el[1].GetString());
    }

    [Fact]
    public void Invalid_Json_Returns_Raw_As_String()
    {
        // Malformed payloads fall back to wrapping the raw text in a
        // string element so users see *something* rather than nothing.
        var result = InvokeExtract(new FakeContext("not-json"));

        Assert.NotNull(result);
        Assert.Equal(JsonValueKind.String, result!.Value.ValueKind);
        Assert.Equal("not-json", result.Value.GetString());
    }

    private static JsonElement? InvokeExtract(IEventContext? ctx)
    {
        var method = typeof(BowireSocketIoProtocol).GetMethod(
            "ExtractPayload",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (JsonElement?)method!.Invoke(null, [ctx]);
    }

    // ---- ParseEmitPayload — InvokeAsync's payload decoder ----

    [Fact]
    public void ParseEmitPayload_Reads_Event_And_Data()
    {
        var (ev, data) = InvokeParseEmit("{\"event\":\"ping\",\"data\":\"hello\"}");

        Assert.Equal("ping", ev);
        Assert.Equal("hello", data);
    }

    [Fact]
    public void ParseEmitPayload_Defaults_To_Message_When_Event_Missing()
    {
        var (ev, data) = InvokeParseEmit("{\"data\":\"hello\"}");

        Assert.Equal("message", ev);
        Assert.Equal("hello", data);
    }

    [Fact]
    public void ParseEmitPayload_Falls_Back_To_Raw_When_Data_Missing()
    {
        var raw = "{\"event\":\"ping\"}";
        var (ev, data) = InvokeParseEmit(raw);

        Assert.Equal("ping", ev);
        // No `data` field — the raw payload itself becomes the data.
        Assert.Equal(raw, data);
    }

    [Fact]
    public void ParseEmitPayload_Handles_Object_Data_Property()
    {
        var (ev, data) = InvokeParseEmit("{\"event\":\"ping\",\"data\":{\"a\":1}}");

        Assert.Equal("ping", ev);
        // ToString on a JsonElement object round-trips the JSON shape,
        // which is what the plugin then forwards to the Socket.IO server.
        Assert.Equal("{\"a\":1}", data);
    }

    [Fact]
    public void ParseEmitPayload_Treats_Null_Event_As_Default()
    {
        var (ev, _) = InvokeParseEmit("{\"event\":null,\"data\":\"x\"}");

        Assert.Equal("message", ev);
    }

    [Fact]
    public void ParseEmitPayload_Falls_Back_On_Malformed_Json()
    {
        var (ev, data) = InvokeParseEmit("not-json");

        Assert.Equal("message", ev);
        Assert.Equal("not-json", data);
    }

    // ---- ExtractEventFilter — InvokeStreamAsync's filter parser ----

    [Fact]
    public void ExtractEventFilter_Returns_Null_For_Empty_List()
    {
        Assert.Null(InvokeExtractFilter([]));
    }

    [Fact]
    public void ExtractEventFilter_Returns_Null_For_Missing_Event_Field()
    {
        Assert.Null(InvokeExtractFilter(["{\"other\":\"value\"}"]));
    }

    [Fact]
    public void ExtractEventFilter_Returns_Null_For_Empty_String_Event()
    {
        Assert.Null(InvokeExtractFilter(["{\"event\":\"\"}"]));
    }

    [Fact]
    public void ExtractEventFilter_Returns_Null_For_Null_Event_Value()
    {
        Assert.Null(InvokeExtractFilter(["{\"event\":null}"]));
    }

    [Fact]
    public void ExtractEventFilter_Returns_Event_Name_When_Set()
    {
        Assert.Equal("port-call.created",
            InvokeExtractFilter(["{\"event\":\"port-call.created\"}"]));
    }

    [Fact]
    public void ExtractEventFilter_Returns_Null_For_Malformed_Json()
    {
        // The plugin swallows malformed JSON so a stray comma in the form
        // body never breaks the listener — it just means "no filter".
        Assert.Null(InvokeExtractFilter(["not-json,"]));
    }

    private static (string EventName, string EventData) InvokeParseEmit(string payload)
    {
        var method = typeof(BowireSocketIoProtocol).GetMethod(
            "ParseEmitPayload",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method!.Invoke(null, [payload])!;
        // The helper returns a ValueTuple<string, string>. Reflection
        // hands it back as a boxed object — destructure via ITuple.
        var tuple = (System.Runtime.CompilerServices.ITuple)result;
        return ((string)tuple[0]!, (string)tuple[1]!);
    }

    private static string? InvokeExtractFilter(List<string> jsonMessages)
    {
        var method = typeof(BowireSocketIoProtocol).GetMethod(
            "ExtractEventFilter",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string?)method!.Invoke(null, [jsonMessages]);
    }

    /// <summary>
    /// Minimal <see cref="IEventContext"/> stub — only <c>RawText</c>
    /// is read by the helper, so every other member throws on use to
    /// catch accidental coupling growth in future <c>ExtractPayload</c>
    /// tweaks.
    /// </summary>
    private sealed class FakeContext(string rawText) : IEventContext
    {
        public string RawText { get; } = rawText;

        public T GetValue<T>(int index) => throw new NotImplementedException();
        public object? GetValue(Type type, int index) => throw new NotImplementedException();
        public Task SendAckDataAsync(IEnumerable<object> data) => throw new NotImplementedException();
        public Task SendAckDataAsync(IEnumerable<object> data, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }
}

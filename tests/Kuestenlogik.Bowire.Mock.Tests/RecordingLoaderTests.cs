// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Mock.Tests;

public sealed class RecordingLoaderTests
{
    private const string SingleRecording = """
    {
      "id": "rec_1",
      "name": "sample",
      "recordingFormatVersion": 1,
      "steps": [
        {
          "id": "step_1",
          "protocol": "rest",
          "service": "Weather",
          "method": "GetForecast",
          "methodType": "Unary",
          "status": "OK",
          "httpPath": "/weather",
          "httpVerb": "GET",
          "response": "{\"temp\":21}"
        }
      ]
    }
    """;

    private const string StoreWithOne = """
    {
      "recordings": [
        {
          "id": "rec_1",
          "name": "sample",
          "recordingFormatVersion": 1,
          "steps": [
            {
              "id": "step_1",
              "protocol": "rest",
              "service": "Weather",
              "method": "GetForecast",
              "methodType": "Unary",
              "status": "OK",
              "httpPath": "/weather",
              "httpVerb": "GET",
              "response": "{}"
            }
          ]
        }
      ]
    }
    """;

    private const string StoreWithMultiple = """
    {
      "recordings": [
        {
          "id": "rec_1",
          "name": "alpha",
          "recordingFormatVersion": 1,
          "steps": [{"id":"s","protocol":"rest","service":"S","method":"M","methodType":"Unary","status":"OK","httpPath":"/a","httpVerb":"GET","response":"{}"}]
        },
        {
          "id": "rec_2",
          "name": "beta",
          "recordingFormatVersion": 1,
          "steps": [{"id":"s","protocol":"rest","service":"S","method":"M","methodType":"Unary","status":"OK","httpPath":"/b","httpVerb":"GET","response":"{}"}]
        }
      ]
    }
    """;

    [Fact]
    public void Parse_SingleRecording_ReturnsIt()
    {
        var rec = RecordingLoader.Parse(SingleRecording);
        Assert.Equal("sample", rec.Name);
        Assert.Single(rec.Steps);
    }

    [Fact]
    public void Parse_StoreWithOneRecording_ReturnsIt()
    {
        var rec = RecordingLoader.Parse(StoreWithOne);
        Assert.Equal("sample", rec.Name);
    }

    [Fact]
    public void Parse_StoreWithMultipleRecordings_ThrowsWithoutSelect()
    {
        var ex = Assert.Throws<InvalidDataException>(() => RecordingLoader.Parse(StoreWithMultiple));
        Assert.Contains("2 recordings", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_StoreWithMultipleRecordings_PicksByName()
    {
        var rec = RecordingLoader.Parse(StoreWithMultiple, select: "beta");
        Assert.Equal("rec_2", rec.Id);
    }

    [Fact]
    public void Parse_StoreWithMultipleRecordings_PicksById()
    {
        var rec = RecordingLoader.Parse(StoreWithMultiple, select: "rec_1");
        Assert.Equal("alpha", rec.Name);
    }

    [Fact]
    public void Parse_WithUnknownSelect_ThrowsWithAvailableList()
    {
        var ex = Assert.Throws<InvalidDataException>(() => RecordingLoader.Parse(StoreWithMultiple, select: "gamma"));
        Assert.Contains("gamma", ex.Message, StringComparison.Ordinal);
        Assert.Contains("alpha", ex.Message, StringComparison.Ordinal);
        Assert.Contains("beta", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_EmptyStore_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            RecordingLoader.Parse("""{"recordings":[]}"""));
        Assert.Contains("empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_WrongShape_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() => RecordingLoader.Parse("""{"foo":"bar"}"""));
        Assert.Contains("neither", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MissingVersion_IsRejected()
    {
        const string noVersion = """
        { "id":"r","name":"x","steps":[{"id":"s","protocol":"rest","service":"S","method":"M","methodType":"Unary","status":"OK","httpPath":"/x","httpVerb":"GET","response":"{}"}] }
        """;
        var ex = Assert.Throws<InvalidDataException>(() => RecordingLoader.Parse(noVersion));
        Assert.Contains("format version", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_FutureVersion_IsRejected()
    {
        const string futureVersion = """
        { "id":"r","name":"x","recordingFormatVersion":99,"steps":[{"id":"s","protocol":"rest","service":"S","method":"M","methodType":"Unary","status":"OK","httpPath":"/x","httpVerb":"GET","response":"{}"}] }
        """;
        var ex = Assert.Throws<InvalidDataException>(() => RecordingLoader.Parse(futureVersion));
        Assert.Contains("99", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NoSteps_Throws()
    {
        const string emptySteps = """
        { "id":"r","name":"x","recordingFormatVersion":1,"steps":[] }
        """;
        var ex = Assert.Throws<InvalidDataException>(() => RecordingLoader.Parse(emptySteps));
        Assert.Contains("no steps", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            RecordingLoader.Load("/tmp/does/not/exist/xyzzy-recording.json"));
    }

    [Fact]
    public void Load_EmptyPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => RecordingLoader.Load(""));
    }

    [Fact]
    public void Parse_V2Recording_WithResponseBinary_RoundTrips()
    {
        const string v2Grpc = """
        {
          "id": "rec_grpc",
          "name": "grpc",
          "recordingFormatVersion": 2,
          "steps": [
            {
              "id": "step_add",
              "protocol": "grpc",
              "service": "calc.Calculator",
              "method": "Add",
              "methodType": "Unary",
              "status": "OK",
              "response": "{\"sum\":42}",
              "responseBinary": "CCo="
            }
          ]
        }
        """;

        var rec = RecordingLoader.Parse(v2Grpc);
        Assert.Equal(2, rec.RecordingFormatVersion);
        Assert.Equal("CCo=", rec.Steps[0].ResponseBinary);
    }
}

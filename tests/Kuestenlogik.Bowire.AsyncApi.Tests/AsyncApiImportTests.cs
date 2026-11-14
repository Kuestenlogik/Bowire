// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Kuestenlogik.Bowire.AsyncApi.Tests;

/// <summary>
/// An uploaded AsyncAPI document read into the channels discovery reports
/// for a document behind a URL (#994).
/// </summary>
public sealed class AsyncApiImportTests
{
    private static ReadOnlyMemory<byte> Sample(string name)
        => File.ReadAllBytes(SafePath.Combine("TestData", name));

    private static ReadOnlyMemory<byte> Doc(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void The_Plugin_Declares_AsyncApi_As_Yaml_And_Json()
    {
        var plugin = new BowireAsyncApiProtocol();

        var kind = Assert.Single(plugin.UploadedSchemaKinds);
        Assert.Equal("asyncapi", kind.Kind);
        Assert.Equal([".yaml", ".yml", ".json"], kind.Extensions);
    }

    [Fact]
    public async Task Read_Lists_The_Channels_A_Discovery_Of_The_Same_Document_Lists()
    {
        var path = SafePath.Combine("TestData", "smart-home-mqtt.asyncapi.yaml");
        var discovered = await new BowireAsyncApiProtocol().DiscoverAsync(
            path, showInternalServices: false, TestContext.Current.CancellationToken);

        var uploaded = new BowireAsyncApiProtocol().ReadUploadedSchema(
            "asyncapi", Sample("smart-home-mqtt.asyncapi.yaml"), "smart-home.yaml");

        Assert.NotEmpty(uploaded);
        Assert.Equal(discovered.Select(s => s.Name), uploaded.Select(s => s.Name));
        Assert.Equal(
            discovered.SelectMany(s => s.Methods).Select(m => m.FullName),
            uploaded.SelectMany(s => s.Methods).Select(m => m.FullName));
    }

    [Fact]
    public void Read_Names_The_Upload_As_The_Origin_A_Call_Will_Use()
    {
        var uploaded = new BowireAsyncApiProtocol().ReadUploadedSchema(
            "asyncapi", Sample("smart-home-mqtt.asyncapi.yaml"), "smart-home.yaml");

        Assert.All(uploaded, s => Assert.Equal("smart-home.yaml", s.OriginUrl));
    }

    [Fact]
    public void Read_Takes_A_Version_2_Document_Too()
    {
        var uploaded = new BowireAsyncApiProtocol().ReadUploadedSchema(
            "asyncapi", Sample("v2-smart-home.asyncapi.yaml"), "v2.yaml");

        Assert.NotEmpty(uploaded);
        Assert.Contains(uploaded, s => s.Methods.Count > 0);
    }

    [Fact]
    public async Task A_Call_To_An_Uploaded_Document_Finds_It_Under_Its_Name()
    {
        // Reading is what the upload and the listing do. A call that names
        // the upload then resolves its channel and operation instead of
        // asking for a discovery first.
        var plugin = new BowireAsyncApiProtocol();
        var uploaded = plugin.ReadUploadedSchema(
            "asyncapi", Sample("smart-home-mqtt.asyncapi.yaml"), "smart-home.yaml");
        var service = uploaded.First(s => s.Methods.Count > 0);

        var result = await plugin.InvokeAsync(
            "smart-home.yaml", service.Name, service.Methods[0].Name, ["{}"],
            showInternalServices: false, metadata: null, TestContext.Current.CancellationToken);

        // What is left to go wrong is past the document: no wire plugin is
        // loaded here to carry the message.
        Assert.DoesNotContain(
            "has not been discovered", result.Metadata.GetValueOrDefault("error") ?? "", StringComparison.Ordinal);
        Assert.NotEmpty(result.Metadata.GetValueOrDefault("error") ?? "");
    }

    [Theory]
    [InlineData("openapi: 3.0.0\ninfo:\n  title: x\n  version: '1'\npaths: {}\n")]
    [InlineData("""{"openrpc":"1.3.2","methods":[]}""")]
    [InlineData("type Query { ping: String }")]
    public void Read_Refuses_A_Document_That_Does_Not_Name_Itself_AsyncApi(string text)
    {
        var ex = Assert.Throws<FormatException>(() =>
            new BowireAsyncApiProtocol().ReadUploadedSchema("asyncapi", Doc(text), "x.yaml"));

        Assert.StartsWith("Not an AsyncAPI document:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("asyncapi", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("asyncapi: 3.0.0\ninfo:\n  title: x\n", true)]
    [InlineData("# events of the harbor\n\ninfo:\n  title: x\n\"asyncapi\": '2.6.0'\n", true)]
    [InlineData("""{"info":{},"asyncapi":"3.0.0"}""", true)]
    [InlineData("openapi: 3.0.0\npaths: {}\n", false)]
    // Indented is not top level: an OpenAPI document that mentions the word.
    [InlineData("openapi: 3.0.0\ninfo:\n  asyncapi: no\n", false)]
    [InlineData("""{"openapi":"3.0.0"}""", false)]
    public void LooksLike_Goes_By_The_Top_Level_Version_Key(string text, bool expected)
    {
        Assert.Equal(expected, new BowireAsyncApiProtocol().LooksLikeUploadedSchema("asyncapi", Doc(text)));
    }
}

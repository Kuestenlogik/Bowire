// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// <c>@file.rsp</c> response files (#976): only that form expands, one
/// argument per line, and every other <c>@</c> token stays as written (#974).
/// </summary>
public sealed class ResponseFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bowire-rsp-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void RspFile_ExpandsOneArgumentPerLine_AsItStands()
    {
        var rsp = Write("ci.rsp", """
            # a comment
            call
              --url
            rest@https://api.example.com

            -d
            {"name": "ada lovelace"}
            """);

        var args = Expanded("@" + rsp, "--compact");

        Assert.Equal(["call", "--url", "rest@https://api.example.com", "-d", """{"name": "ada lovelace"}""", "--compact"], args);
    }

    private static string[] Expanded(params string[] args)
    {
        var expanded = ResponseFiles.Expand(args, out var error);
        Assert.Null(error);
        return expanded!;
    }

    [Theory]
    [InlineData("@request.json")]
    [InlineData("@body.txt")]
    [InlineData("field@file.rsp")]
    [InlineData("@.rsp")]
    public void OtherAtTokens_StayAsWritten(string token)
        => Assert.Equal(["call", "-d", token], Expanded("call", "-d", token));

    [Fact]
    public void Extension_IsCaseInsensitive()
    {
        var rsp = Write("CI.RSP", "list");
        Assert.Equal(["list"], Expanded("@" + rsp));
    }

    [Fact]
    public void AfterDoubleDash_NothingExpands()
    {
        var rsp = Write("x.rsp", "list");
        Assert.Equal(["--", "@" + rsp], Expanded("--", "@" + rsp));
    }

    [Fact]
    public void ResponseFile_DoesNotExpandFurtherResponseFiles()
    {
        var inner = Write("inner.rsp", "list");
        var outer = Write("outer.rsp", "@" + inner);
        Assert.Equal(["@" + inner], Expanded("@" + outer));
    }

    [Fact]
    public void MissingFile_IsAnError_NamingIt()
    {
        var missing = Path.Combine(_dir, "nope.rsp");
        Assert.Null(ResponseFiles.Expand(["@" + missing], out var error));
        Assert.Equal($"Response file not found: {missing}", error);
    }
}

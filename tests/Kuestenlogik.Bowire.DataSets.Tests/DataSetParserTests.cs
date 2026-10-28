// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;
using Kuestenlogik.Bowire.DataSets.V1;

namespace Kuestenlogik.Bowire.DataSets.Tests;

/// <summary>
/// The data-set parser (#675) through the rpc handler every surface calls:
/// rows and columns from CSV and JSON, and a malformed file refused with the
/// line or item that is wrong, before any request would be sent.
/// </summary>
public sealed class DataSetParserTests
{
    private static ParseDataSetResponse Parse(string fileName, string content, string format = "") =>
        DataSetRpc.ParseDataSet(new ParseDataSetRequest { FileName = fileName, Content = content, Format = format });

    private static string Refused(string fileName, string content)
    {
        var ex = Assert.Throws<RpcException>(() => Parse(fileName, content));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        return ex.Status.Detail;
    }

    [Fact]
    public void Csv_Rows_Are_Bound_By_The_Header()
    {
        var r = Parse("users.csv", "id,name\n1,Ada\n2,Grace\n");

        Assert.Equal("csv", r.Format);
        Assert.Equal(["id", "name"], r.Columns);
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal(1, r.Rows[0].Iteration);
        Assert.Equal(2, r.Rows[0].SourceLine);
        Assert.Equal("Grace", r.Rows[1].Values["name"]);
        Assert.Equal(3, r.Rows[1].SourceLine);
    }

    [Fact]
    public void Csv_Quotes_Doubled_Quotes_And_Line_Breaks_Inside_A_Value()
    {
        var r = Parse("notes.csv", "id,note\r\n1,\"Hello, \"\"world\"\"\"\r\n2,\"two\nlines\"\r\n3,plain\r\n");

        Assert.Equal("Hello, \"world\"", r.Rows[0].Values["note"]);
        Assert.Equal("two\nlines", r.Rows[1].Values["note"]);
        // The row after a multi-line value names the line it starts on.
        Assert.Equal(5, r.Rows[2].SourceLine);
    }

    [Fact]
    public void Csv_Semicolon_And_Tab_Separators_Come_From_The_Header()
    {
        Assert.Equal("1,5", Parse("de.csv", "id;price\n7;1,5\n").Rows[0].Values["price"]);
        Assert.Equal("b", Parse("t.tsv", "a\tb\nx\tb\n").Rows[0].Values["b"]);
    }

    [Fact]
    public void Csv_Skips_Blank_Lines_And_A_Byte_Order_Mark()
    {
        var r = Parse("bom.csv", "﻿id\n1\n\n2\n");
        Assert.Equal(["id"], r.Columns);
        Assert.Equal(["1", "2"], r.Rows.Select(x => x.Values["id"]));
    }

    [Fact]
    public void Json_An_Array_Of_Objects_Each_A_Row()
    {
        var r = Parse("rows.json", """[{"id":1,"name":"Ada","admin":true,"tags":["a"],"note":null},{"id":2,"extra":"x"}]""");

        Assert.Equal("json", r.Format);
        Assert.Equal(["id", "name", "admin", "tags", "note", "extra"], r.Columns);
        Assert.Equal("1", r.Rows[0].Values["id"]);
        Assert.Equal("true", r.Rows[0].Values["admin"]);
        Assert.Equal("[\"a\"]", r.Rows[0].Values["tags"]);
        Assert.Equal("", r.Rows[0].Values["note"]);
        // A key a row lacks is absent, so the variable falls through to the environment.
        Assert.False(r.Rows[1].Values.ContainsKey("name"));
        Assert.Equal(2, r.Rows[1].SourceLine);
    }

    [Fact]
    public void The_Format_Is_Sniffed_From_The_Content_When_The_Name_Says_Nothing()
    {
        Assert.Equal("json", Parse("data", """[{"a":1}]""").Format);
        Assert.Equal("csv", Parse("data", "a\n1").Format);
        Assert.Equal("csv", Parse("rows.txt", "a\n1", format: "CSV").Format);
    }

    [Theory]
    [InlineData("empty.csv", "", "empty.csv is empty")]
    [InlineData("header.csv", "id,name\n", "header.csv has a header but no rows")]
    [InlineData("short.csv", "id,name\n1,Ada\n2\n", "short.csv line 3: 1 value(s) where the header has 2 column(s)")]
    [InlineData("dup.csv", "id,id\n1,2\n", "dup.csv line 1: the column 'id' appears twice")]
    [InlineData("blank.csv", "id,,name\n1,2,3\n", "blank.csv line 1: column 2 of the header has no name")]
    [InlineData("open.csv", "id,note\n1,\"never closed\n", "open.csv line 2: a quoted value is never closed")]
    [InlineData("object.json", """{"id":1}""", "object.json: expected an array of objects")]
    [InlineData("items.json", """[{"id":1},2]""", "items.json item 2: expected an object, found a number")]
    [InlineData("none.json", "[]", "none.json is an empty array")]
    [InlineData("broken.json", "[{\"id\":1,}]", "broken.json line 1: not valid JSON")]
    public void A_Malformed_File_Is_Refused_With_Where(string fileName, string content, string expectedStart)
    {
        Assert.StartsWith(expectedStart, Refused(fileName, content), StringComparison.Ordinal);
    }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.DataSets.V1;

namespace Kuestenlogik.Bowire.DataSets;

/// <summary>A data file that does not parse; the message names the line or item.</summary>
public sealed class DataSetFormatException : Exception
{
    public DataSetFormatException() { }

    public DataSetFormatException(string message) : base(message) { }

    public DataSetFormatException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// CSV (RFC 4180: a header row names the columns; quotes, doubled quotes and
/// line breaks inside quotes; comma, semicolon or tab, taken from the header)
/// and JSON (an array of objects; strings as they are, other scalars as their
/// JSON text, objects and arrays as JSON, null as empty).
/// </summary>
internal static class DataSetParser
{
    public static ParseDataSetResponse Parse(string fileName, string content, string? format)
    {
        var text = content.TrimStart('﻿');
        var kind = string.IsNullOrWhiteSpace(format) ? Sniff(fileName, text) : format.Trim().ToUpperInvariant();
        return kind switch
        {
            "CSV" => ParseCsv(fileName, text),
            "JSON" => ParseJson(fileName, text),
            _ => throw new DataSetFormatException($"{fileName}: unknown format '{format}'; use csv or json"),
        };
    }

    private static string Sniff(string fileName, string text)
    {
        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return "JSON";
        if (fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase)) return "CSV";
        return text.TrimStart().StartsWith('[') ? "JSON" : "CSV";
    }

    // ---- CSV ----

    private static ParseDataSetResponse ParseCsv(string fileName, string text)
    {
        var records = ReadRecords(fileName, text);
        if (records.Count == 0) throw new DataSetFormatException($"{fileName} is empty: expected a header row naming the columns, then one row per iteration");

        var (headerLine, header) = records[0];
        for (var i = 0; i < header.Count; i++)
        {
            header[i] = header[i].Trim();
            if (header[i].Length == 0) throw new DataSetFormatException($"{fileName} line {headerLine}: column {i + 1} of the header has no name");
        }
        var duplicate = header.GroupBy(h => h, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new DataSetFormatException($"{fileName} line {headerLine}: the column '{duplicate.Key}' appears twice in the header");

        var response = new ParseDataSetResponse { Format = "csv" };
        response.Columns.Add(header);
        foreach (var (line, fields) in records.Skip(1))
        {
            // A blank line between rows is not a row.
            if (fields.Count == 1 && fields[0].Length == 0) continue;
            if (fields.Count != header.Count)
                throw new DataSetFormatException($"{fileName} line {line}: {fields.Count} value(s) where the header has {header.Count} column(s)");
            var row = new DataRow { Iteration = response.Rows.Count + 1, SourceLine = line };
            for (var i = 0; i < header.Count; i++) row.Values[header[i]] = fields[i];
            response.Rows.Add(row);
        }
        if (response.Rows.Count == 0) throw new DataSetFormatException($"{fileName} has a header but no rows");
        return response;
    }

    // Each record with the line it starts on.
    private static List<(int Line, List<string> Fields)> ReadRecords(string fileName, string text)
    {
        var records = new List<(int, List<string>)>();
        if (text.Length == 0) return records;
        var delimiter = Delimiter(text);
        var fields = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var recordLine = 1;
        var inQuotes = false;
        var quoteLine = 0;
        var fieldStarted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (c == '\n') line++;
                    field.Append(c);
                }
                continue;
            }

            if (c == '"' && !fieldStarted)
            {
                inQuotes = true;
                quoteLine = line;
                fieldStarted = true;
            }
            else if (c == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(field.ToString());
                records.Add((recordLine, fields));
                fields = [];
                field.Clear();
                fieldStarted = false;
                line++;
                recordLine = line;
            }
            else
            {
                field.Append(c);
                fieldStarted = true;
            }
        }
        if (inQuotes) throw new DataSetFormatException($"{fileName} line {quoteLine}: a quoted value is never closed");
        if (fieldStarted || field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add((recordLine, fields));
        }
        return records;
    }

    private static readonly char[] s_delimiters = [',', ';', '\t'];

    // The header line's separator: the most frequent of comma, semicolon and tab.
    private static char Delimiter(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        var header = end < 0 ? text : text[..end];
        return s_delimiters.OrderByDescending(d => header.Count(c => c == d)).ThenBy(d => d == ',' ? 0 : 1).First();
    }

    // ---- JSON ----

    private static string KindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "something else",
    };

    private static ParseDataSetResponse ParseJson(string fileName, string text)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } l ? $" line {l + 1}" : "";
            throw new DataSetFormatException($"{fileName}{where}: not valid JSON ({ex.Message})", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new DataSetFormatException($"{fileName}: expected an array of objects, one per iteration");
            var response = new ParseDataSetResponse { Format = "json" };
            var columns = new List<string>();
            var item = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                item++;
                if (element.ValueKind != JsonValueKind.Object)
                    throw new DataSetFormatException($"{fileName} item {item}: expected an object, found {KindName(element.ValueKind)}");
                var row = new DataRow { Iteration = item, SourceLine = item };
                foreach (var property in element.EnumerateObject())
                {
                    if (!columns.Contains(property.Name)) columns.Add(property.Name);
                    row.Values[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString()!,
                        JsonValueKind.Null => "",
                        _ => property.Value.GetRawText(),
                    };
                }
                response.Rows.Add(row);
            }
            if (response.Rows.Count == 0) throw new DataSetFormatException($"{fileName} is an empty array: no rows to run");
            response.Columns.Add(columns);
            return response;
        }
    }
}

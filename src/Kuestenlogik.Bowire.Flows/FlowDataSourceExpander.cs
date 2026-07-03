// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Kuestenlogik.Bowire.Flows;

/// <summary>
/// One expanded data row — the column values join the <c>{{var}}</c>
/// resolver scope for a single step execution, the label names the row in
/// reports (<c>step[label]</c>).
/// </summary>
public sealed class FlowDataRow
{
    /// <summary>Create a row from its column map and report label.</summary>
    public FlowDataRow(IReadOnlyDictionary<string, string> values, string label)
    {
        Values = values;
        Label = label;
    }

    /// <summary>Column name → stringified value, ordinal-cased like the resolver scope.</summary>
    public IReadOnlyDictionary<string, string> Values { get; }

    /// <summary>Report label — the <see cref="FlowDataSource.LabelColumn"/> value or the zero-based row index.</summary>
    public string Label { get; }
}

/// <summary>
/// Materialises a <see cref="FlowDataSource"/> into concrete rows — #174.
/// The runner calls this once per data-driven step and executes the step
/// once per returned row.
/// </summary>
public static class FlowDataSourceExpander
{
    /// <summary>
    /// Upper bound on expanded rows. A typo'd generator range
    /// (<c>to: 2000000000</c>) must fail loudly instead of hanging CI.
    /// </summary>
    public const int MaxRows = 100_000;

    /// <summary>
    /// Expand <paramref name="data"/> into rows. CSV paths resolve
    /// relative to <paramref name="baseDirectory"/> (the flow file's
    /// directory). Throws <see cref="InvalidDataException"/> on config
    /// errors (none / several sources set, inverted range, zero rows) and
    /// lets file-system exceptions from a missing / unreadable CSV
    /// propagate — the runner maps both onto a step error.
    /// </summary>
    public static IReadOnlyList<FlowDataRow> Expand(FlowDataSource data, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(baseDirectory);

        var sourceCount = (data.Inline is not null ? 1 : 0)
            + (!string.IsNullOrEmpty(data.Csv) ? 1 : 0)
            + (data.Generator is not null ? 1 : 0);
        if (sourceCount == 0)
        {
            throw new InvalidDataException("data source is empty — set exactly one of inline / csv / generator.");
        }
        if (sourceCount > 1)
        {
            throw new InvalidDataException("data source is ambiguous — set exactly one of inline / csv / generator.");
        }

        var rows = data.Inline is not null
            ? ExpandInline(data.Inline)
            : !string.IsNullOrEmpty(data.Csv)
                ? ExpandCsv(Path.Combine(baseDirectory, data.Csv))
                : ExpandGenerator(data.Generator!);

        if (rows.Count == 0)
        {
            throw new InvalidDataException("data source produced no rows — a data-driven step with zero rows would pass vacuously.");
        }
        if (rows.Count > MaxRows)
        {
            throw new InvalidDataException($"data source produced {rows.Count} rows — the runner caps at {MaxRows}.");
        }

        return Label(rows, data.LabelColumn);
    }

    private static List<Dictionary<string, string>> ExpandInline(IReadOnlyList<JsonObject> inline)
    {
        var rows = new List<Dictionary<string, string>>(inline.Count);
        foreach (var obj in inline)
        {
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in obj)
            {
                row[prop.Key] = Stringify(prop.Value);
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Scalars substitute as their literal text (<c>42</c>, <c>true</c>,
    /// <c>Ada</c> — no JSON quotes); nested arrays / objects substitute as
    /// compact JSON so a row can carry a structured fragment into a body
    /// template.
    /// </summary>
    private static string Stringify(JsonNode? value) => value switch
    {
        null => string.Empty,
        JsonValue v => v.ToString(),
        _ => value.ToJsonString(),
    };

    private static List<Dictionary<string, string>> ExpandCsv(string path)
    {
        var records = ParseCsv(File.ReadAllText(path));
        if (records.Count == 0)
        {
            throw new InvalidDataException($"CSV '{path}' is empty — expected a header row.");
        }

        var header = records[0];
        var rows = new List<Dictionary<string, string>>(records.Count - 1);
        for (var i = 1; i < records.Count; i++)
        {
            var record = records[i];
            if (record.Count != header.Count)
            {
                throw new InvalidDataException(
                    $"CSV '{path}' row {i} has {record.Count} fields, header has {header.Count}.");
            }
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var c = 0; c < header.Count; c++)
            {
                row[header[c]] = record[c];
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// RFC-4180-shaped parser: comma-separated, <c>"</c>-quoted fields with
    /// <c>""</c> escaping, quoted fields may span lines, accepts LF and
    /// CRLF, ignores a trailing newline. No external dependency — flow
    /// fixtures are small and hand-written.
    /// </summary>
    private static List<List<string>> ParseCsv(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = 0;

        void EndField()
        {
            record.Add(field.ToString());
            field.Clear();
        }
        void EndRecord()
        {
            EndField();
            records.Add(record);
            record = new List<string>();
        }

        while (i < text.Length)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    inQuotes = false;
                    i++;
                    continue;
                }
                field.Append(ch);
                i++;
                continue;
            }
            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    i++;
                    break;
                case ',':
                    EndField();
                    i++;
                    break;
                case '\r':
                    i++; // swallowed; the '\n' that follows ends the record
                    break;
                case '\n':
                    EndRecord();
                    i++;
                    break;
                default:
                    field.Append(ch);
                    i++;
                    break;
            }
        }
        if (inQuotes)
        {
            throw new InvalidDataException("CSV ends inside a quoted field.");
        }
        // Final record without a trailing newline; a lone trailing newline
        // leaves nothing pending.
        if (field.Length > 0 || record.Count > 0)
        {
            EndRecord();
        }
        return records;
    }

    private static List<Dictionary<string, string>> ExpandGenerator(FlowDataGenerator gen)
    {
        if (string.IsNullOrEmpty(gen.Var))
        {
            throw new InvalidDataException("generator: 'var' must name the variable each value binds to.");
        }

        if (string.Equals(gen.Kind, "range", StringComparison.OrdinalIgnoreCase))
        {
            if (gen.To < gen.From)
            {
                throw new InvalidDataException($"generator: range {gen.From}..{gen.To} is inverted.");
            }
            var count = gen.To - gen.From + 1;
            if (count > MaxRows)
            {
                throw new InvalidDataException($"generator: range spans {count} rows — the runner caps at {MaxRows}.");
            }
            var rows = new List<Dictionary<string, string>>((int)count);
            for (var v = gen.From; v <= gen.To; v++)
            {
                rows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [gen.Var] = v.ToString(CultureInfo.InvariantCulture),
                });
            }
            return rows;
        }

        if (string.Equals(gen.Kind, "random", StringComparison.OrdinalIgnoreCase))
        {
            if (gen.Count <= 0)
            {
                throw new InvalidDataException("generator: random needs count > 0.");
            }
            if (gen.Max < gen.Min)
            {
                throw new InvalidDataException($"generator: random bounds {gen.Min}..{gen.Max} are inverted.");
            }
            var rows = new List<Dictionary<string, string>>(gen.Count);
            // splitmix64 instead of System.Random: the sequence is pinned
            // by this code, not by the runtime — the same seed yields the
            // same rows on every .NET version, which is the whole point of
            // a seeded CI data source.
            var state = unchecked((ulong)gen.Seed);
            var span = (ulong)(gen.Max - gen.Min) + 1;
            for (var n = 0; n < gen.Count; n++)
            {
                state += 0x9E3779B97F4A7C15UL;
                var z = state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
                var value = gen.Min + (long)(z % span);
                rows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [gen.Var] = value.ToString(CultureInfo.InvariantCulture),
                });
            }
            return rows;
        }

        throw new InvalidDataException($"generator: unknown kind '{gen.Kind}' — expected 'range' or 'random'.");
    }

    private static List<FlowDataRow> Label(List<Dictionary<string, string>> rows, string? labelColumn)
    {
        var labelled = new List<FlowDataRow>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var label = labelColumn is not null
                && rows[i].TryGetValue(labelColumn, out var v)
                && !string.IsNullOrEmpty(v)
                    ? v
                    : i.ToString(CultureInfo.InvariantCulture);
            labelled.Add(new FlowDataRow(rows[i], label));
        }
        return labelled;
    }
}

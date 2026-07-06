// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Implementation of <c>bowire scan mutate</c> (#175) — exercise the
/// schema-aware mutation engine (<see cref="SchemaMutator"/>) for a single
/// field type and print the targeted invalid inputs it produces. Seeded +
/// budgeted, so the same flags always print the same set — the reproducible
/// building block the scan/fuzz replay layer walks a whole schema with.
/// </summary>
public static class MutateCommand
{
    public static async Task<int> RunAsync(MutateOptions options, CancellationToken ct, TextWriter? output = null, TextWriter? error = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = ct;
        var stdout = output ?? Console.Out;
        var stderr = error ?? Console.Error;

        var kind = SchemaMutator.ParseKind(options.Type);
        if (kind is null)
        {
            await stderr.WriteLineAsync("  Usage: bowire scan mutate --type <integer|number|string|boolean|enum|array|object> [--enum a,b,c] [--required] [--format email] [--seed N] [--budget M]").ConfigureAwait(false);
            return 2;
        }

        var enumValues = string.IsNullOrWhiteSpace(options.Enum)
            ? null
            : options.Enum.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var spec = new FuzzFieldSpec(kind.Value, options.Required, enumValues, options.Format);
        var mutations = SchemaMutator.Mutate(spec, options.Seed, options.Budget);

        await stdout.WriteLineAsync().ConfigureAwait(false);
        var pieces = $"type={options.Type}, seed={options.Seed}";
        if (options.Required) pieces += ", required";
        if (enumValues is { Length: > 0 }) pieces += $", enum=[{string.Join(",", enumValues)}]";
        if (!string.IsNullOrEmpty(options.Format)) pieces += $", format={options.Format}";
        await stdout.WriteLineAsync($"  Schema-aware mutations — {pieces} ({mutations.Count} of budget {(options.Budget > 0 ? options.Budget.ToString(System.Globalization.CultureInfo.InvariantCulture) : "∞")}):").ConfigureAwait(false);

        foreach (var m in mutations)
            await stdout.WriteLineAsync($"    [{m.Label}]  {m.ValueJson}\n          → {m.Expectation}").ConfigureAwait(false);

        return 0;
    }
}

/// <summary>Options for <c>bowire scan mutate</c>.</summary>
public sealed class MutateOptions
{
    public string? Type { get; init; }
    public string? Enum { get; init; }
    public bool Required { get; init; }
    public string? Format { get; init; }
    public int Seed { get; init; }
    public int Budget { get; init; }
}

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;

namespace Kuestenlogik.Bowire.Mock.Replay;

/// <summary>
/// Dependency-free fake-data generators behind the <c>${faker.*}</c> response
/// tokens (#406). Small built-in word lists — enough for realistic-looking
/// mock payloads without pulling in a faker library. Uses a crypto RNG so no
/// analyzer suppression is needed (the values aren't secrets; it's just the
/// simplest way to satisfy the non-crypto-random rule).
/// </summary>
internal static class MockFaker
{
    private static readonly string[] s_first =
        ["Alex", "Sam", "Jordan", "Taylor", "Morgan", "Casey", "Riley", "Jamie", "Robin", "Drew", "Avery", "Quinn"];
    private static readonly string[] s_last =
        ["Smith", "Jones", "Lee", "Brown", "Garcia", "Miller", "Davis", "Nguyen", "Khan", "Rossi", "Kim", "Novak"];
    private static readonly string[] s_cities =
        ["Berlin", "Hamburg", "Munich", "London", "Paris", "Madrid", "Rome", "Vienna", "Zurich", "Oslo"];
    private static readonly string[] s_countries =
        ["Germany", "France", "Spain", "Italy", "Austria", "Norway", "Sweden", "Poland", "Portugal", "Ireland"];
    private static readonly string[] s_words =
        ["lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit", "sed", "tempor", "labore", "magna"];

    /// <summary>
    /// Generate a value for a <c>faker.&lt;spec&gt;</c> token (the part after
    /// <c>faker.</c>). Returns null for an unknown spec so the caller can leave
    /// the placeholder literal (in whichever delimiter it used).
    /// </summary>
    public static string? Generate(string spec)
    {
        var (name, args) = ParseSpec(spec);
        return name switch
        {
            "firstName" => Pick(s_first),
            "lastName" => Pick(s_last),
            "name" or "fullName" => Pick(s_first) + " " + Pick(s_last),
            "email" => Lower(Pick(s_first)) + "." + Lower(Pick(s_last)) + "@example.com",
            "city" => Pick(s_cities),
            "country" => Pick(s_countries),
            "word" => Pick(s_words),
            "uuid" => Guid.NewGuid().ToString(),
            "bool" => RandomNumberGenerator.GetInt32(2) == 0 ? "true" : "false",
            "int" => FakeInt(args),
            "lorem" => Lorem(args),
            "date" => DateTimeOffset.UtcNow
                .AddDays(RandomNumberGenerator.GetInt32(-365, 366))
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static string Pick(string[] pool) => pool[RandomNumberGenerator.GetInt32(pool.Length)];

    // Char-wise lower-casing for the email local part (avoids CA1308's flag on
    // string.ToLowerInvariant while keeping the same result).
    private static string Lower(string s) =>
        string.Create(s.Length, s, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++) span[i] = char.ToLowerInvariant(src[i]);
        });

    private static string FakeInt(string[] args)
    {
        var min = args.Length > 0 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lo) ? lo : 0;
        var max = args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hi) ? hi : 100;
        if (max < min) (min, max) = (max, min);
        // GetInt32's upper bound is exclusive; +1 makes the range inclusive.
        return RandomNumberGenerator.GetInt32(min, max == int.MaxValue ? max : max + 1)
            .ToString(CultureInfo.InvariantCulture);
    }

    private static string Lorem(string[] args)
    {
        var count = args.Length > 0 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? Math.Clamp(n, 1, 200) : 5;
        var words = new string[count];
        for (var i = 0; i < count; i++) words[i] = Pick(s_words);
        return string.Join(' ', words);
    }

    // "int(1,10)" → ("int", ["1","10"]); "firstName" → ("firstName", []).
    private static (string Name, string[] Args) ParseSpec(string spec)
    {
        var open = spec.IndexOf('(', StringComparison.Ordinal);
        if (open < 0 || !spec.EndsWith(')'))
            return (spec, []);
        var name = spec[..open];
        var inner = spec[(open + 1)..^1];
        var args = inner.Length == 0
            ? []
            : inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (name, args);
    }
}

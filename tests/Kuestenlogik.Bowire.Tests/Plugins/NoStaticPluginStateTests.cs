// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Plugins;
using Xunit;

namespace Kuestenlogik.Bowire.Tests.Plugins;

/// <summary>
/// Acceptance criterion 3 of #546 — the duplicate ledger is gone — in a
/// form a rename cannot satisfy.
/// </summary>
/// <remarks>
/// <para>
/// Grepping for <c>s_loadedSubdirs</c> proves only that the name is gone.
/// The failure mode this guards against is real and was the main criticism
/// of two of the three candidate designs for this ticket: delete the
/// static set, then reintroduce the same global dedupe under another name
/// one level up, and the grep still passes.
/// </para>
/// <para>
/// So the rule is structural. No writable static field may exist on plugin
/// management, and every static that survives has to be named here with a
/// reason. Adding one means editing this list, which is the point — it
/// turns a silent regression into a decision someone has to write down.
/// </para>
/// </remarks>
public sealed class NoStaticPluginStateTests
{
    /// <summary>
    /// Statics that are allowed to exist, keyed <c>Type.Field</c>, each
    /// with the reason it cannot move.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedStatics = new(StringComparer.Ordinal)
    {
        // Immutable configuration for the JSON writer used by the install
        // verbs. No lifetime, no resource, no observable state.
        ["PluginManager.IndentedJson"] =
            "Immutable JsonSerializerOptions shared by the install verbs.",
    };

    private static IEnumerable<Type> PluginManagementTypes()
    {
        var assembly = typeof(PluginManager).Assembly;
        return assembly.GetTypes().Where(t =>
            (t.Namespace == "Kuestenlogik.Bowire.App.Plugins" || t == typeof(PluginManager))
            // Skip the compiler's own machinery. Every lambda that closes
            // over nothing gets a cached delegate in a `<>c` display class
            // — a writable static field the author never wrote and cannot
            // remove. Judging those would make the gate unpassable and say
            // nothing about the design.
            && !IsCompilerGenerated(t));
    }

    private static bool IsCompilerGenerated(Type type)
    {
        for (var t = type; t is not null; t = t.DeclaringType!)
        {
            if (t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)) return true;
            if (!t.IsNested) break;
        }
        return false;
    }

    [Fact]
    public void PluginManagement_HasNoWritableStaticFields()
    {
        var offenders = new List<string>();

        foreach (var type in PluginManagementTypes())
        {
            foreach (var field in type.GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                // const: a compile-time literal, not state.
                if (field.IsLiteral) continue;
                // readonly: assigned once at type initialisation. Whether
                // what it points AT is mutable is a separate question, and
                // the allow-list below is where that gets argued.
                if (field.IsInitOnly) continue;

                var name = $"{DeclaringName(type)}.{Clean(field.Name)}";
                if (AllowedStatics.ContainsKey(name)) continue;
                offenders.Add(name);
            }
        }

        Assert.True(offenders.Count == 0,
            "Writable static state reappeared in plugin management. Either move it onto "
            + "BowirePluginLoader / BowirePluginOptions, or add it to AllowedStatics with "
            + "the reason it cannot: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheDuplicateLedger_IsGoneByName_Too()
    {
        // Belt and braces for the specific fields the ticket names. If one
        // of these comes back the structural test above catches it, but a
        // by-name assertion says which regression happened.
        var declared = PluginManagementTypes()
            .SelectMany(t => t.GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Select(f => Clean(f.Name))
            .ToList();

        Assert.DoesNotContain("s_loadedSubdirs", declared, StringComparer.Ordinal);
        Assert.DoesNotContain("s_pluginContexts", declared, StringComparer.Ordinal);
        Assert.DoesNotContain("s_lastLoadResults", declared, StringComparer.Ordinal);
    }

    [Fact]
    public void EveryAllowedStatic_StillExists()
    {
        // An allow-list that outlives what it allows is a lie about the
        // codebase. If a static named here is gone, delete the entry.
        var declared = PluginManagementTypes()
            .SelectMany(t => t.GetFields(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Select(f => $"{DeclaringName(t)}.{Clean(f.Name)}"))
            .ToHashSet(StringComparer.Ordinal);

        var stale = AllowedStatics.Keys.Where(k => !declared.Contains(k)).ToList();

        Assert.True(stale.Count == 0,
            "AllowedStatics names statics that no longer exist — remove them: "
            + string.Join(", ", stale));
    }

    // Compiler-generated backing fields read as "<Prop>k__BackingField";
    // report the property name so the message points at real source.
    private static string Clean(string fieldName)
    {
        var open = fieldName.IndexOf('<', StringComparison.Ordinal);
        var close = fieldName.IndexOf('>', StringComparison.Ordinal);
        return open == 0 && close > 1 ? fieldName[1..close] : fieldName;
    }

    private static string DeclaringName(Type type)
        => type.IsNested ? $"{type.DeclaringType!.Name}.{type.Name}" : type.Name;
}

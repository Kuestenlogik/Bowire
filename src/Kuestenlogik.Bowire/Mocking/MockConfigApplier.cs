// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kuestenlogik.Bowire.Mocking;

/// <summary>
/// Applies a <see cref="MockConfiguration"/>'s per-field overrides onto a
/// (schema-synthesised or recorded) <see cref="BowireRecording"/> at
/// generation time (#558). For every step whose <c>service</c>/<c>method</c>
/// an override targets, the step's JSON response is parsed, the value at the
/// override's path is set, and the response is written back.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Apply"/> evaluates <see cref="MockConfiguration.FieldOverrides"/>
/// at generation time (#558). <see cref="ApplyToStubs"/> (#561) additionally
/// compiles <see cref="MockConfiguration.ConditionalRules"/> into extra
/// higher-priority stubs so the existing mock matcher serves a response
/// variant when the request predicate matches — no new serve-time path.
/// </para>
/// <para>
/// The applier lives in the shared <c>Mocking</c> namespace so the standalone
/// <c>MockServer</c>, the workbench mock host, and the runtime config-apply
/// endpoint all reuse one copy.
/// </para>
/// </remarks>
public static class MockConfigApplier
{
    /// <summary>Priority given to a compiled conditional-rule stub so it outranks the base (priority-0) stub.</summary>
    private const int RuleStubPriority = 10;

    /// <summary>
    /// Return <paramref name="recording"/> with every applicable per-field
    /// override applied to its steps' responses. Mutates the passed
    /// recording in place (and returns it) so callers can inline the call.
    /// A null / override-free config, an unparseable step response, or a
    /// path that does not resolve leaves the step untouched.
    /// </summary>
    public static BowireRecording Apply(BowireRecording recording, MockConfiguration? config)
    {
        ArgumentNullException.ThrowIfNull(recording);
        // Guard null config, and a null-or-empty override list — a
        // MockConfiguration deserialised outside Parse (or a
        // `"fieldOverrides": null` token) can carry a null collection.
        if (config?.FieldOverrides is not { Count: > 0 }) return recording;

        foreach (var step in recording.Steps)
        {
            var applicable = config.FieldOverrides.Where(o => Matches(o, step)).ToList();
            if (applicable.Count > 0) ApplyFieldOverridesToStep(step, applicable);
        }

        return recording;
    }

    /// <summary>
    /// #561: apply a configuration onto a fresh copy of a mock's baseline
    /// stubs and return the new stub set — used to re-apply the config to a
    /// RUNNING mock. Never mutates <paramref name="baseline"/> (the caller's
    /// stubs share instances with the mock's restore baseline): every step is
    /// cloned first. Field overrides mutate the clones' responses; each
    /// conditional rule is compiled into an additional stub that rides the
    /// matching base stub's route, carries the rule's request predicate as a
    /// higher-priority match, and serves the rule's response variant.
    /// </summary>
    public static IReadOnlyList<BowireRecordingStep> ApplyToStubs(
        IReadOnlyList<BowireRecordingStep> baseline, MockConfiguration? config)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var result = baseline.Select(CloneStep).ToList();
        if (config is null) return result;

        if (config.FieldOverrides is { Count: > 0 } overrides)
        {
            foreach (var step in result)
            {
                var applicable = overrides.Where(o => Matches(o, step)).ToList();
                if (applicable.Count > 0) ApplyFieldOverridesToStep(step, applicable);
            }
        }

        if (config.ConditionalRules is { Count: > 0 } rules)
        {
            // Snapshot the base clones before appending any rule stubs so a rule
            // rides the BASE routes only (never another rule stub).
            var baseClones = result.ToList();
            foreach (var rule in rules)
            {
                // A conditional rule needs a discriminating predicate AND a
                // response variant. A null/empty predicate would match every
                // request and — at priority 10 — permanently shadow the base
                // response, so it is skipped (consistent with the null case).
                if (rule.When is null
                    || !HasEffectivePredicate(rule.When)
                    || rule.Response is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } response)
                {
                    continue;
                }
                // Fan out over EVERY matching base stub (a `*` rule spans all its
                // routes, matching how field overrides fan out).
                foreach (var target in baseClones.Where(s => RuleTargets(rule, s)))
                {
                    result.Add(CompileRuleStub(rule.When, response, target));
                }
            }
        }

        return result;
    }

    // Apply the matching field overrides onto one step's JSON response,
    // in place. Returns whether anything changed. A null / absent / undefined
    // override value is a no-op (a JSON `null` round-trips to a CLR null
    // through JsonElement?, so absent and "value": null are indistinguishable).
    private static bool ApplyFieldOverridesToStep(BowireRecordingStep step, IReadOnlyList<MockFieldOverride> overrides)
    {
        if (string.IsNullOrEmpty(step.Response)) return false;

        JsonNode? root;
        try { root = JsonNode.Parse(step.Response); }
        catch (JsonException) { return false; }
        if (root is null) return false;

        var changed = false;
        foreach (var ov in overrides)
        {
            if (ov.Value is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value
                || string.IsNullOrWhiteSpace(ov.JsonPath))
            {
                continue;
            }
            if (SetAtPath(root, ov.JsonPath, value)) changed = true;
        }

        if (changed) step.Response = root.ToJsonString();
        return changed;
    }

    // Compile one conditional rule into a REST stub that rides the base stub's
    // verb+path, carries the rule's request-body predicate at higher priority,
    // and serves the rule's response variant.
    private static BowireRecordingStep CompileRuleStub(
        MockRulePredicate when, JsonElement response, BowireRecordingStep baseStep)
        => new()
        {
            Id = "rule_" + Guid.NewGuid().ToString("N")[..8],
            Protocol = baseStep.Protocol,
            Service = baseStep.Service,
            Method = baseStep.Method,
            MethodType = baseStep.MethodType,
            HttpVerb = baseStep.HttpVerb,
            HttpPath = baseStep.HttpPath,
            Status = baseStep.Status,
            Response = response.GetRawText(),
            Match = new BowireStepMatch
            {
                Priority = RuleStubPriority,
                Body = new List<BowireBodyMatcher>
                {
                    new()
                    {
                        JsonPath = when.JsonPath,
                        // Keep EqualTo verbatim ("" is a real "equals empty"
                        // constraint); drop an empty Contains/Matches — an empty
                        // one matches every request. With a JsonPath still set,
                        // an emptied op degrades to a path-present check.
                        EqualTo = when.EqualTo,
                        Contains = string.IsNullOrEmpty(when.Contains) ? null : when.Contains,
                        Matches = string.IsNullOrEmpty(when.Matches) ? null : when.Matches,
                    },
                },
            },
        };

    // A rule's predicate is effective (discriminating) when it carries a
    // JsonPath (a path/presence check), or an EqualTo (incl. the empty string —
    // matches only an empty value), or a NON-empty Contains / Matches. An empty
    // Contains / Matches without a JsonPath matches every request, so such a
    // rule is skipped rather than compiled into a route-shadowing stub.
    private static bool HasEffectivePredicate(MockRulePredicate when)
        => !string.IsNullOrWhiteSpace(when.JsonPath)
           || when.EqualTo is not null
           || !string.IsNullOrEmpty(when.Contains)
           || !string.IsNullOrEmpty(when.Matches);

    // Deep clone via a JSON round-trip so a step's Response / Match / headers
    // are independent copies — the baseline must never be mutated.
    private static BowireRecordingStep CloneStep(BowireRecordingStep step)
        => JsonSerializer.Deserialize<BowireRecordingStep>(JsonSerializer.Serialize(step))!;

    private static bool RuleTargets(MockConditionalRule rule, BowireRecordingStep step)
        => WildcardEquals(rule.Service, step.Service) && WildcardEquals(rule.Method, step.Method);

    private static bool Matches(MockFieldOverride ov, BowireRecordingStep step)
        => WildcardEquals(ov.Service, step.Service) && WildcardEquals(ov.Method, step.Method);

    // Null / empty / "*" is a wildcard; otherwise a case-insensitive equal.
    private static bool WildcardEquals(string? rule, string actual)
        => string.IsNullOrEmpty(rule)
           || rule == "*"
           || string.Equals(rule, actual, StringComparison.OrdinalIgnoreCase);

    // Walk the path segment-by-segment, creating missing intermediate
    // objects, and set the leaf. Missing array indices are NOT grown (a
    // surprising side effect); an out-of-range index leaves the node
    // untouched and returns false.
    private static bool SetAtPath(JsonNode root, string jsonPath, JsonElement value)
    {
        var segments = NormalizeJsonPath(jsonPath);
        if (segments.Length == 0) return false;

        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var seg = segments[i];
            if (current is JsonObject obj)
            {
                if (!obj.TryGetPropertyValue(seg, out var next) || next is null)
                {
                    var created = new JsonObject();
                    obj[seg] = created;
                    current = created;
                }
                else
                {
                    current = next;
                }
            }
            else if (current is JsonArray arr && TryIndex(seg, arr.Count, out var idx) && arr[idx] is { } elem)
            {
                current = elem;
            }
            else
            {
                return false;
            }
        }

        var leaf = segments[^1];
        // Convert the JsonElement (object / array / scalar / null) to a fresh
        // JsonNode tree. A JSON-null value serializes to a null node, which
        // assigns as a JSON null leaf.
        var node = JsonSerializer.SerializeToNode(value);
        if (current is JsonObject leafObj)
        {
            leafObj[leaf] = node;
            return true;
        }
        if (current is JsonArray leafArr && TryIndex(leaf, leafArr.Count, out var li))
        {
            leafArr[li] = node;
            return true;
        }
        return false;
    }

    private static bool TryIndex(string segment, int count, out int index)
        => int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
           && index >= 0 && index < count;

    // Mirrors MockMatchPredicates.NormalizeJsonPath so override paths use the
    // exact syntax the mock body matchers already accept: split
    // "$.user.items[0].id" (or "user.items.0.id") into ["user","items","0","id"].
    private static string[] NormalizeJsonPath(string path)
    {
        var p = path;
        if (p.StartsWith('$')) p = p[1..];
        p = p.Replace("[", ".", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal);
        return p.Split('.', StringSplitOptions.RemoveEmptyEntries);
    }
}

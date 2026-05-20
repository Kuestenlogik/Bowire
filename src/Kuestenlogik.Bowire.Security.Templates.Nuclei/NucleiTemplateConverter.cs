// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.Security.Templates.Nuclei;

/// <summary>
/// Translate a parsed <see cref="NucleiTemplate"/> into Bowire's
/// vulnerability-template shape (<see cref="BowireRecording"/> with
/// <see cref="BowireRecording.Attack"/> set + a populated
/// <see cref="AttackVulnerability"/> + a built
/// <see cref="AttackPredicate"/>). The scanner already knows how to
/// execute that shape — no new probe-runner needed, the Nuclei
/// corpus just shows up alongside Bowire's own templates.
///
/// Phase 2a — skeleton + identity + info mapping. The matcher → predicate
/// translation lives behind a <see cref="NotImplementedException"/> guard
/// so consumers see a clear "this part hasn't landed yet" error rather
/// than silent under-coverage. Phase 2b implements status / word /
/// regex matchers; multi-matcher composition + part-selection arrive
/// alongside.
/// </summary>
public static class NucleiTemplateConverter
{
    /// <summary>
    /// Produce a <see cref="BowireRecording"/> the security scanner
    /// can load alongside its native templates. When
    /// <paramref name="variableContext"/> is supplied, every Nuclei
    /// <c>{{...}}</c> placeholder in path / body strings gets
    /// resolved against the target URL at conversion time so the
    /// scanner receives ready-to-send probes. When the context is
    /// <c>null</c> (corpus pre-load before target binding),
    /// placeholders pass through literally; callers can re-convert
    /// once the target is known or invoke
    /// <see cref="ResolveVariables"/> on the resulting recording.
    /// </summary>
    /// <summary>
    /// Plural counterpart that fully unfolds a Nuclei template: one
    /// <see cref="BowireRecording"/> per (path × payload-row)
    /// combination. Single-path single-payload templates collapse to
    /// a list of one. The id of each emitted recording carries the
    /// path-index + payload-row in a stable suffix
    /// (<c>{templateId}#p{N}#r{M}</c>) so SARIF + dashboards can
    /// group findings by combination without losing the source
    /// template's identity.
    /// </summary>
    public static IReadOnlyList<BowireRecording> ToBowireRecordings(
        NucleiTemplate template,
        NucleiVariableContext? variableContext = null)
    {
        ArgumentNullException.ThrowIfNull(template);

        var firstHttp = template.Http.FirstOrDefault();
        if (firstHttp is null)
        {
            // Nothing to fire — return a single placeholder recording
            // so the operator sees the template was loaded but had
            // no http block. Matches the single-shape contract.
            return [ToBowireRecording(template, variableContext)];
        }

        var paths = firstHttp.Path.Count > 0 ? firstHttp.Path : new List<string> { "" };
        var payloadRows = ExpandPayloadCrossProduct(firstHttp.Payloads);

        var result = new List<BowireRecording>(paths.Count * Math.Max(1, payloadRows.Count));
        for (var pathIdx = 0; pathIdx < paths.Count; pathIdx++)
        {
            for (var rowIdx = 0; rowIdx < payloadRows.Count; rowIdx++)
            {
                var clone = ClonePathAndApplyPayload(firstHttp, paths[pathIdx], payloadRows[rowIdx]);
                var clonedTemplate = WithSingleHttp(template, clone);

                var recording = ToBowireRecording(clonedTemplate, variableContext);
                // Tag the id only when the unfolding produced more
                // than one — single-path single-payload templates
                // keep their original id intact.
                if (paths.Count > 1 || payloadRows.Count > 1)
                {
                    recording.Id = $"{template.Id}#p{pathIdx}#r{rowIdx}";
                }
                result.Add(recording);
            }
        }
        return result;
    }

    /// <summary>
    /// Expand a payload map (variable → list of values) into rows of
    /// (variable → single value) by cross-product. Empty map yields
    /// one empty row (so the caller emits one recording with no
    /// payload substitutions). Single-variable + multi-variable both
    /// flow through the same loop.
    /// </summary>
    private static List<Dictionary<string, string>> ExpandPayloadCrossProduct(
        Dictionary<string, List<string>> payloads)
    {
        if (payloads.Count == 0) return [new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)];

        var rows = new List<Dictionary<string, string>> { new(StringComparer.OrdinalIgnoreCase) };
        foreach (var (variableName, values) in payloads)
        {
            var next = new List<Dictionary<string, string>>(rows.Count * values.Count);
            foreach (var row in rows)
            {
                foreach (var value in values)
                {
                    var clone = new Dictionary<string, string>(row, StringComparer.OrdinalIgnoreCase)
                    {
                        [variableName] = value
                    };
                    next.Add(clone);
                }
            }
            rows = next;
        }
        return rows;
    }

    /// <summary>
    /// Build a single-path single-payload-row copy of the request that
    /// downstream code can treat as a plain "one path, one body"
    /// template. Substitutes payload-variable placeholders inline so
    /// the variable resolver doesn't need to know about Nuclei's
    /// payload semantics.
    /// </summary>
    private static NucleiHttpRequest ClonePathAndApplyPayload(
        NucleiHttpRequest source, string singlePath, IReadOnlyDictionary<string, string> payloadRow)
    {
        var clone = new NucleiHttpRequest
        {
            Method = source.Method,
            Body = ApplyPayload(source.Body, payloadRow),
            MatchersCondition = source.MatchersCondition,
        };
        clone.Path.Add(ApplyPayload(singlePath, payloadRow));
        clone.Matchers.AddRange(source.Matchers);
        return clone;
    }

    /// <summary>
    /// Replace every <c>{{varName}}</c> in <paramref name="input"/>
    /// with the matching payload-row value. Payload placeholders use
    /// the same <c>{{...}}</c> syntax as Nuclei's built-in variables;
    /// matched payload-names take precedence over the variable
    /// resolver because they're template-scoped, not target-scoped.
    /// </summary>
    private static string ApplyPayload(string input, IReadOnlyDictionary<string, string> payloadRow)
    {
        if (string.IsNullOrEmpty(input) || payloadRow.Count == 0) return input;
        var result = input;
        foreach (var (name, value) in payloadRow)
        {
            result = result.Replace("{{" + name + "}}", value, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>
    /// Produce a shallow template clone whose <c>Http</c> is exactly
    /// one entry — the singular <see cref="ToBowireRecording"/> path
    /// already handles that shape, so the plural unfolder just hands
    /// it pre-collapsed slices.
    /// </summary>
    private static NucleiTemplate WithSingleHttp(NucleiTemplate original, NucleiHttpRequest single)
    {
        var clone = new NucleiTemplate
        {
            Id = original.Id,
            Info = original.Info,
        };
        clone.Http.Add(single);
        return clone;
    }

    public static BowireRecording ToBowireRecording(
        NucleiTemplate template,
        NucleiVariableContext? variableContext = null)
    {
        ArgumentNullException.ThrowIfNull(template);

        var recording = new BowireRecording
        {
            Id = string.IsNullOrWhiteSpace(template.Id) ? "nuclei-untitled" : template.Id,
            Name = template.Info.Name,
            Description = BuildDescription(template),
            Attack = true,
            Vulnerability = new AttackVulnerability
            {
                Id = template.Id,
                Severity = NormaliseSeverity(template.Info.Severity),
                Authors = string.IsNullOrWhiteSpace(template.Info.Author)
                    ? new List<string>()
                    : new List<string>(template.Info.Author.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                References = new List<string>(template.Info.Reference),
                Protocols = new List<string> { "rest", "http" },
            },
            // VulnerableWhen translated by NucleiMatcherTranslator —
            // status / word / regex on the body get full coverage.
            // Header / response-line matchers + custom matcher types
            // arrive in Phase 2b+; until then they drop out of the
            // tree (the surrounding predicate still fires on the
            // matchers we did translate). Templates with zero
            // translated matchers end up with VulnerableWhen = null
            // and the scanner reports them as "no actionable
            // predicate" — visible, non-silent.
            VulnerableWhen = template.Http.FirstOrDefault() is { } firstReq
                ? NucleiMatcherTranslator.Translate(firstReq)
                : null,
        };

        // Phase 2a captures the first http-block's first path as a
        // single Bowire recording step. Multi-path + multi-step
        // arrive in Phase 2d.
        var firstHttp = template.Http.FirstOrDefault();
        if (firstHttp is not null && firstHttp.Path.Count > 0)
        {
            var rawPath = firstHttp.Path[0];
            var rawBody = firstHttp.Body;
            var path = variableContext is null
                ? rawPath
                : NucleiVariableResolver.Resolve(rawPath, variableContext);
            var body = variableContext is null
                ? rawBody
                : NucleiVariableResolver.Resolve(rawBody, variableContext);

            recording.Steps.Add(new BowireRecordingStep
            {
                Id = "probe-1",
                Protocol = "http",
                Service = "root",
                Method = $"{firstHttp.Method} {path}",
                MethodType = "Unary",
                HttpVerb = firstHttp.Method,
                HttpPath = path,
                Body = body,
                Status = "OK",
            });
        }

        return recording;
    }

    /// <summary>
    /// Walk an already-built recording and substitute Nuclei
    /// placeholders into its step paths + bodies. Useful when the
    /// corpus is loaded ahead of target binding (Phase 2e flow) and
    /// the scanner needs to resolve placeholders right before the
    /// probe goes out.
    /// </summary>
    public static void ResolveVariables(BowireRecording recording, NucleiVariableContext context)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var step in recording.Steps)
        {
            if (!string.IsNullOrEmpty(step.HttpPath))
            {
                step.HttpPath = NucleiVariableResolver.Resolve(step.HttpPath, context);
            }
            if (!string.IsNullOrEmpty(step.Body))
            {
                step.Body = NucleiVariableResolver.Resolve(step.Body!, context);
            }
            // Method is `${verb} ${path}` — keep the two in sync so
            // the sidebar / SARIF location-uri reflect the resolved
            // path rather than the placeholder.
            if (!string.IsNullOrEmpty(step.HttpVerb) && !string.IsNullOrEmpty(step.HttpPath))
            {
                step.Method = $"{step.HttpVerb} {step.HttpPath}";
            }
        }
    }

    private static string BuildDescription(NucleiTemplate template)
    {
        // Keep the Nuclei description as the body; append the tag set
        // so operators can grep `--severity` + tag-filter combinations
        // without losing the corpus's categorisation.
        var desc = template.Info.Description;
        if (template.Info.Tags.Count > 0)
        {
            var tags = string.Join(", ", template.Info.Tags);
            desc = string.IsNullOrEmpty(desc) ? $"Tags: {tags}" : $"{desc}\n\nTags: {tags}";
        }
        return desc;
    }

    /// <summary>
    /// Nuclei uses lowercase severity strings (<c>info</c>, <c>low</c>,
    /// <c>medium</c>, <c>high</c>, <c>critical</c>, <c>unknown</c>).
    /// Bowire's <see cref="AttackVulnerability.Severity"/> is also
    /// lowercase; pass-through with an explicit <c>unknown</c> →
    /// <c>info</c> rewrite so the scanner's severity-floor filter
    /// (<c>--severity medium</c>) behaves predictably.
    /// </summary>
    private static string NormaliseSeverity(string nucleiSeverity)
    {
        // CA1308 nudges against ToLowerInvariant for normalisation;
        // explicit case-match covers Nuclei's three observed casings
        // (lowercase is the documented form, but the corpus has a
        // mix of Title and ALL-CAPS in the wild).
        return nucleiSeverity.Trim() switch
        {
            "critical" or "Critical" or "CRITICAL" => "critical",
            "high" or "High" or "HIGH" => "high",
            "medium" or "Medium" or "MEDIUM" => "medium",
            "low" or "Low" or "LOW" => "low",
            "info" or "Info" or "INFO" => "info",
            _ => "info",
        };
    }
}

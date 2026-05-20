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

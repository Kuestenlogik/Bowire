#!/usr/bin/env node
// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// Drops the findings that describe how CI runs Bowire, so what reaches the
// Security tab is what Bowire can fix (#614).
//
//   node scripts/ci/filter-harness-findings.mjs <sarif...> [--rules path.json]
//
// Why this exists at all. `scan-dogfood` refuses to upload its SARIF, and it
// is right to: its target is a sample we start ourselves, so the findings
// describe the harness. This workflow *does* want the upload — the target is
// the product — but the harness problem does not disappear just because the
// target changed. A runner has no certificate, so every scan of a local
// Bowire reports "target serves plaintext http://" at high severity, and an
// operator reading Bowire's Security tab would reasonably conclude the
// product ships without TLS.
//
// So the exclusion is explicit, named, and reasoned in
// scripts/ci/harness-scan-rules.json rather than expressed as a severity
// floor. A floor would not work here anyway, which is the part worth
// remembering: the only high-severity finding against a local Bowire is the
// harness's plaintext one, while the findings that are genuinely Bowire's —
// missing security headers, a version-disclosing Server banner — are both
// low. `--severity medium` would upload exactly the wrong half.
//
// Every drop is printed. An exclusion nobody sees is an exclusion that
// quietly grows.

import { readFileSync, writeFileSync } from 'node:fs';

const args = process.argv.slice(2);
const rulesFlag = args.indexOf('--rules');
const rulesPath = rulesFlag >= 0 ? args[rulesFlag + 1] : 'scripts/ci/harness-scan-rules.json';
// Guarded on rulesFlag >= 0: indexOf returns -1 when the flag is absent,
// and -1 + 1 is index 0 — which silently dropped the first SARIF from the
// list whenever the default rules path was used.
const files = args.filter(
    (a, i) => !a.startsWith('--') && !(rulesFlag >= 0 && i === rulesFlag + 1));

if (files.length === 0) {
    process.stderr.write('usage: filter-harness-findings.mjs <sarif...> [--rules path.json]\n');
    process.exit(2);
}

/** @returns {Record<string, string>} rule id → why it is the harness's */
function loadRules(path) {
    const parsed = JSON.parse(readFileSync(path, 'utf8'));
    const rules = parsed.rules;
    if (!rules || typeof rules !== 'object') {
        process.stderr.write(`${path} has no "rules" object.\n`);
        process.exit(2);
    }
    return rules;
}

const harness = loadRules(rulesPath);
let dropped = 0;
let kept = 0;

for (const file of files) {
    const sarif = JSON.parse(readFileSync(file, 'utf8'));

    for (const run of sarif.runs ?? []) {
        const before = run.results ?? [];
        const after = before.filter((r) => !Object.hasOwn(harness, r.ruleId));

        for (const result of before) {
            if (Object.hasOwn(harness, result.ruleId)) {
                const where = result.locations?.[0]?.logicalLocations?.[0]?.fullyQualifiedName ?? '?';
                process.stdout.write(
                    `  drop ${result.ruleId} (${where}) — ${harness[result.ruleId]}\n`);
                dropped++;
            } else {
                kept++;
            }
        }

        run.results = after;

        // The rule metadata goes too. A rule declared but never referenced is
        // legal SARIF, but it puts the excluded finding's full description
        // into the Security tab's rule list, where it reads as something
        // Bowire reports and nobody ever sees fire.
        const declared = run.tool?.driver?.rules;
        if (Array.isArray(declared)) {
            run.tool.driver.rules = declared.filter((rule) => !Object.hasOwn(harness, rule.id));
        }
    }

    writeFileSync(file, JSON.stringify(sarif, null, 2));
}

process.stdout.write(`${kept} finding(s) kept, ${dropped} dropped as harness-owned.\n`);

// Zero findings is not an error — a Bowire with nothing to report is the
// point — but it is worth saying out loud, because it is also what a broken
// scan looks like.
if (kept === 0) {
    process.stdout.write(
        '::notice::No findings left to upload. Either Bowire is clean or the scan found nothing at all;'
        + ' the dogfood job is what tells those two apart.\n');
}

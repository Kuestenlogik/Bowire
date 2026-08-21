#!/usr/bin/env node
// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// Turns a `bowire scan` run into an assertion instead of a report.
//
//   node scripts/ci/assert-scan-findings.mjs findings.sarif expected-scan-findings.json
//
// Why not upload the SARIF to Code Scanning, which is what a scanner
// normally does with its output? Because the target is a sample we start
// ourselves, and its findings say things about the harness — "serves
// plaintext http://" is true of any dev server on localhost and says nothing
// about Bowire. Uploaded, those land in Bowire's own Security tab as
// high-severity alerts against Bowire. Anyone reading that tab would
// reasonably conclude the product has a TLS problem. It does not; the CI
// scaffolding does, by design.
//
// An assertion is also the stricter test. An uploaded report is satisfied by
// any output at all, so a scanner that quietly stopped finding ANYTHING would
// keep the job green — the exact regression this job exists to catch. Pinning
// the expected set fails in both directions:
//
//   * a finding disappears  → a probe broke, or a rule stopped matching
//   * a finding appears     → the sample regressed, or a new probe fires
//
// Both are worth a red build and a human look. Neither is worth an alert in
// a security dashboard.

import { readFileSync } from 'node:fs';

const [, , sarifPath, expectedPath] = process.argv;
if (!sarifPath || !expectedPath) {
    process.stderr.write('usage: assert-scan-findings.mjs <findings.sarif> <expected.json>\n');
    process.exit(2);
}

function read(path, what) {
    try {
        return JSON.parse(readFileSync(path, 'utf8'));
    } catch (err) {
        process.stderr.write(`Could not read ${what} at ${path}: ${err.message}\n`);
        process.exit(2);
    }
}

const sarif = read(sarifPath, 'the SARIF report');
const expectation = read(expectedPath, 'the expectation file');

/** `ruleId@level`, the pair that has to stay stable. */
const key = (ruleId, level) => `${ruleId}@${level}`;

const actual = new Map();
for (const run of sarif.runs ?? []) {
    for (const result of run.results ?? []) {
        // A rule firing twice is one fact, not two — the count depends on how
        // many endpoints the sample happens to expose, which is not what this
        // job is pinning.
        actual.set(key(result.ruleId, result.level), result.message?.text ?? '');
    }
}

const expected = new Map(
    (expectation.expected ?? []).map(e => [key(e.ruleId, e.level), e]));

const missing = [...expected.keys()].filter(k => !actual.has(k));
const unexpected = [...actual.keys()].filter(k => !expected.has(k));

const lines = [];
lines.push(`target:   ${expectation.target ?? '(unspecified)'}`);
lines.push(`expected: ${expected.size} finding(s)`);
lines.push(`actual:   ${actual.size} finding(s)`);
lines.push('');

for (const [k, e] of expected) {
    lines.push(actual.has(k) ? `  ok       ${k}` : `  MISSING  ${k}  — ${e.why ?? ''}`);
}
for (const k of unexpected) {
    lines.push(`  NEW      ${k}  — ${actual.get(k)}`);
}

process.stdout.write(lines.join('\n') + '\n');

if (missing.length === 0 && unexpected.length === 0) {
    process.stdout.write('\nscan assertion: PASS\n');
    process.exit(0);
}

// Say what to DO about it. A diff without a next step gets rubber-stamped by
// whoever is unlucky enough to read it first.
process.stdout.write('\n');
if (missing.length) {
    process.stdout.write(
        `::error::${missing.length} expected finding(s) did not appear. Either a probe regressed, `
        + 'or the sample was fixed — if the latter, remove them from the expectation file.\n');
}
if (unexpected.length) {
    process.stdout.write(
        `::error::${unexpected.length} unexpected finding(s) appeared. Either the sample regressed, `
        + 'or a new probe fires — if the latter, add them to the expectation file with a reason.\n');
}
process.stdout.write('\nscan assertion: FAIL\n');
process.exit(1);

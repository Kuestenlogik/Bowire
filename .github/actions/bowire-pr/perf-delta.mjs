#!/usr/bin/env node
// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #183 — the PR report's Perf section.
//
// `bowire test` already writes a JUnit report whose every <testcase>
// carries a `time` attribute (JUnitReport.cs). Comparing the head run's
// timings against a base-branch run is therefore a pure diff over two
// files we already produce — no benchmark engine, no second runner. The
// action supplies both paths the same way it supplies a base schema
// snapshot and a base scan SARIF.
//
// Usage:
//   node perf-delta.mjs --head test.xml [--base base-test.xml]
//                       [--threshold-pct 20] [--min-ms 5] [--out perf.md]
//
// Writes a markdown section to --out (default stdout) and prints
// `regressions=<n>` on the last line for the action to read.

import { readFileSync, writeFileSync, realpathSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

// ---- args ----------------------------------------------------------------

function parseArgs(argv) {
    const args = { thresholdPct: 20, minMs: 5 };
    for (let i = 0; i < argv.length; i++) {
        const flag = argv[i];
        const value = argv[i + 1];
        if (flag === '--head') { args.head = value; i++; }
        else if (flag === '--base') { args.base = value; i++; }
        else if (flag === '--out') { args.out = value; i++; }
        else if (flag === '--threshold-pct') { args.thresholdPct = Number(value); i++; }
        else if (flag === '--min-ms') { args.minMs = Number(value); i++; }
    }
    return args;
}

// ---- JUnit parsing -------------------------------------------------------

// Pull (key, milliseconds) out of every <testcase>. Attribute order is not
// fixed across writers, so each attribute is matched independently rather
// than assuming `name` precedes `time`. A testcase without a parseable
// `time` is skipped: it carries no timing to compare.
export function parseJUnitTimings(xml) {
    const timings = new Map();
    if (!xml) return timings;
    const cases = xml.match(/<testcase\b[^>]*>/g) || [];
    for (const tag of cases) {
        const name = (tag.match(/\bname="([^"]*)"/) || [])[1];
        const classname = (tag.match(/\bclassname="([^"]*)"/) || [])[1];
        const time = Number((tag.match(/\btime="([^"]*)"/) || [])[1]);
        if (!name || !Number.isFinite(time)) continue;
        // classname is the service/suite: two suites may hold a same-named
        // test, so the key needs both.
        timings.set(classname ? `${classname} › ${name}` : name, time * 1000);
    }
    return timings;
}

// ---- diff ----------------------------------------------------------------

// A test counts as a regression when it is BOTH relatively slower than
// thresholdPct AND absolutely slower than minMs. The absolute floor is what
// keeps a 2ms test that drifted to 4ms (+100%) out of the report — CI
// runners are noisy at that scale and such a row is pure alarm fatigue.
export function diffTimings(baseTimings, headTimings, { thresholdPct = 20, minMs = 5 } = {}) {
    const regressions = [];
    const improvements = [];
    let compared = 0;

    for (const [key, headMs] of headTimings) {
        if (!baseTimings.has(key)) continue; // new test — nothing to compare
        const baseMs = baseTimings.get(key);
        compared++;
        const deltaMs = headMs - baseMs;
        if (Math.abs(deltaMs) < minMs) continue;
        // A base of 0ms has no meaningful percentage; treat any absolute
        // move past the floor as reportable rather than dividing by zero.
        const deltaPct = baseMs > 0 ? (deltaMs / baseMs) * 100 : Infinity;
        if (deltaPct >= thresholdPct) regressions.push({ key, baseMs, headMs, deltaMs, deltaPct });
        else if (-deltaPct >= thresholdPct) improvements.push({ key, baseMs, headMs, deltaMs, deltaPct });
    }

    regressions.sort((a, b) => b.deltaMs - a.deltaMs);
    improvements.sort((a, b) => a.deltaMs - b.deltaMs);
    return { regressions, improvements, compared };
}

// ---- rendering -----------------------------------------------------------

function fmtMs(ms) {
    return ms >= 100 ? `${Math.round(ms)}ms` : `${ms.toFixed(1)}ms`;
}

function fmtPct(pct) {
    if (!Number.isFinite(pct)) return 'new baseline';
    const sign = pct > 0 ? '+' : '';
    return `${sign}${pct.toFixed(0)}%`;
}

export function renderMarkdown({ regressions, improvements, compared }, { thresholdPct = 20 } = {}) {
    const lines = ['## Perf', ''];

    if (compared === 0) {
        // No shared tests: either the base report is absent or the suite was
        // renamed wholesale. Say so rather than implying "nothing changed".
        lines.push('_No test in this run also ran on the base branch — no latency comparison available._', '');
        return lines.join('\n');
    }

    if (regressions.length === 0 && improvements.length === 0) {
        lines.push(`**Perf:** no test moved more than ${thresholdPct}% across ${compared} compared test(s).`, '');
        return lines.join('\n');
    }

    const headline = regressions.length > 0
        ? `**Perf:** ${regressions.length} test(s) slower by more than ${thresholdPct}%`
        : `**Perf:** ${improvements.length} test(s) faster by more than ${thresholdPct}%`;
    lines.push(headline + ` (of ${compared} compared).`, '');

    lines.push('| Test | Base | Head | Delta |', '|------|------|------|-------|');
    // Regressions first — they are why anyone reads this section — then a
    // few improvements so a genuine speed-up is visible too.
    for (const row of regressions.slice(0, 10)) {
        lines.push(`| ${row.key} | ${fmtMs(row.baseMs)} | ${fmtMs(row.headMs)} | 🔴 +${fmtMs(row.deltaMs)} (${fmtPct(row.deltaPct)}) |`);
    }
    for (const row of improvements.slice(0, 5)) {
        lines.push(`| ${row.key} | ${fmtMs(row.baseMs)} | ${fmtMs(row.headMs)} | 🟢 ${fmtMs(row.deltaMs)} (${fmtPct(row.deltaPct)}) |`);
    }
    lines.push('');

    if (regressions.length > 10) {
        lines.push(`_… and ${regressions.length - 10} more regression(s)._`, '');
    }
    return lines.join('\n');
}

// ---- main ----------------------------------------------------------------

function readOrEmpty(path) {
    if (!path) return '';
    try { return readFileSync(path, 'utf8'); }
    catch { return ''; }
}

function main(argv) {
    const args = parseArgs(argv);
    const headTimings = parseJUnitTimings(readOrEmpty(args.head));
    const baseTimings = parseJUnitTimings(readOrEmpty(args.base));
    const result = diffTimings(baseTimings, headTimings, {
        thresholdPct: args.thresholdPct,
        minMs: args.minMs,
    });
    const md = renderMarkdown(result, { thresholdPct: args.thresholdPct });

    if (args.out) writeFileSync(args.out, md, 'utf8');
    else process.stdout.write(md + '\n');

    // Last line is what the action's step parses.
    process.stdout.write(`regressions=${result.regressions.length}\n`);
    return 0;
}

// Only run when invoked directly, so the test can import the helpers.
// Compared as real filesystem paths rather than by string-building a
// file:// URL: on Windows that yields `file://C:/…` against Node's
// `file:///C:/…` and the script silently does nothing when run as a CLI.
function isDirectInvocation() {
    if (!process.argv[1]) return false;
    try {
        return realpathSync(resolve(process.argv[1])) === realpathSync(fileURLToPath(import.meta.url));
    } catch {
        return false;
    }
}

if (isDirectInvocation()) {
    process.exit(main(process.argv.slice(2)));
}

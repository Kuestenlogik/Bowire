// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #183 — the PR bot's Perf section.
//
// perf-delta.mjs diffs two JUnit reports (`bowire test --junit`) to find
// tests that got slower on the PR head versus the base branch. These cover
// the parsing, the regression rule (relative AND absolute, so runner noise
// on a 2ms test doesn't fill the report), and the rendered markdown.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const script = resolve(__dirname, '../../../.github/actions/bowire-pr/perf-delta.mjs');
const { parseJUnitTimings, diffTimings, renderMarkdown } = await import(`file://${script}`);

function junit(cases) {
    const body = cases
        .map(c => `<testcase classname="${c.suite ?? 'svc'}" name="${c.name}" time="${c.seconds}" />`)
        .join('\n');
    return `<?xml version="1.0"?><testsuites><testsuite name="s">\n${body}\n</testsuite></testsuites>`;
}

describe('parseJUnitTimings', () => {
    it('reads every testcase as milliseconds, keyed by suite and name', () => {
        const timings = parseJUnitTimings(junit([
            { name: 'GetUser', seconds: '0.120' },
            { name: 'ListUsers', seconds: '1.500' },
        ]));
        assert.equal(timings.size, 2);
        assert.equal(timings.get('svc › GetUser'), 120);
        assert.equal(timings.get('svc › ListUsers'), 1500);
    });

    it('keeps same-named tests from different suites apart', () => {
        // Two suites legitimately hold a "Get" test; collapsing them would
        // compare unrelated numbers.
        const timings = parseJUnitTimings(junit([
            { suite: 'orders', name: 'Get', seconds: '0.100' },
            { suite: 'billing', name: 'Get', seconds: '0.900' },
        ]));
        assert.equal(timings.get('orders › Get'), 100);
        assert.equal(timings.get('billing › Get'), 900);
    });

    it('tolerates attribute order and skips cases without a usable time', () => {
        const xml = `<testsuites>
            <testcase time="0.250" name="Reordered" classname="svc" />
            <testcase classname="svc" name="NoTime" />
            <testcase classname="svc" name="BadTime" time="not-a-number" />
        </testsuites>`;
        const timings = parseJUnitTimings(xml);
        assert.equal(timings.get('svc › Reordered'), 250);
        assert.equal(timings.has('svc › NoTime'), false);
        assert.equal(timings.has('svc › BadTime'), false);
    });

    it('returns empty for missing or empty input rather than throwing', () => {
        assert.equal(parseJUnitTimings('').size, 0);
        assert.equal(parseJUnitTimings(undefined).size, 0);
    });
});

describe('diffTimings', () => {
    const base = parseJUnitTimings(junit([
        { name: 'Slower', seconds: '0.100' },
        { name: 'Faster', seconds: '0.500' },
        { name: 'Flat', seconds: '0.200' },
        { name: 'Tiny', seconds: '0.002' },
        { name: 'Removed', seconds: '0.300' },
    ]));
    const head = parseJUnitTimings(junit([
        { name: 'Slower', seconds: '0.180' },   // +80ms, +80%
        { name: 'Faster', seconds: '0.200' },   // -300ms, -60%
        { name: 'Flat', seconds: '0.205' },     // +5ms, +2.5% — under threshold
        { name: 'Tiny', seconds: '0.004' },     // +2ms, +100% — under the ms floor
        { name: 'Added', seconds: '0.900' },    // no base — not comparable
    ]));

    it('flags a test that is both relatively and absolutely slower', () => {
        const { regressions } = diffTimings(base, head);
        assert.equal(regressions.length, 1);
        assert.equal(regressions[0].key, 'svc › Slower');
        assert.equal(Math.round(regressions[0].deltaMs), 80);
        assert.equal(Math.round(regressions[0].deltaPct), 80);
    });

    it('ignores a large percentage move that is only a couple of milliseconds', () => {
        // 'Tiny' doubled but moved 2ms: runner noise, not a regression.
        const { regressions } = diffTimings(base, head);
        assert.equal(regressions.some(r => r.key.endsWith('Tiny')), false);
    });

    it('ignores a small percentage move even when absolutely large enough', () => {
        const { regressions } = diffTimings(base, head);
        assert.equal(regressions.some(r => r.key.endsWith('Flat')), false);
    });

    it('reports improvements separately from regressions', () => {
        const { improvements } = diffTimings(base, head);
        assert.equal(improvements.length, 1);
        assert.equal(improvements[0].key, 'svc › Faster');
        assert.ok(improvements[0].deltaMs < 0);
    });

    it('only counts tests present in both runs', () => {
        const { compared } = diffTimings(base, head);
        assert.equal(compared, 4); // Slower, Faster, Flat, Tiny — not Added/Removed
    });

    it('honours a custom threshold', () => {
        const { regressions } = diffTimings(base, head, { thresholdPct: 200 });
        assert.equal(regressions.length, 0);
    });

    it('treats a zero-millisecond baseline as reportable instead of dividing by zero', () => {
        const zeroBase = parseJUnitTimings(junit([{ name: 'Cold', seconds: '0' }]));
        const warmHead = parseJUnitTimings(junit([{ name: 'Cold', seconds: '0.050' }]));
        const { regressions } = diffTimings(zeroBase, warmHead);
        assert.equal(regressions.length, 1);
        assert.equal(Number.isFinite(regressions[0].deltaPct), false);
    });

    it('yields nothing when there is no base report at all', () => {
        const { regressions, compared } = diffTimings(new Map(), head);
        assert.equal(regressions.length, 0);
        assert.equal(compared, 0);
    });
});

describe('renderMarkdown', () => {
    it('says so plainly when no test overlapped the base run', () => {
        const md = renderMarkdown({ regressions: [], improvements: [], compared: 0 });
        assert.match(md, /## Perf/);
        assert.match(md, /no latency comparison available/i);
    });

    it('reports a clean run without a table', () => {
        const md = renderMarkdown({ regressions: [], improvements: [], compared: 7 });
        assert.match(md, /no test moved more than 20%/);
        assert.match(md, /7 compared/);
        assert.doesNotMatch(md, /\| Test \|/);
    });

    it('tables regressions with base, head and delta', () => {
        const md = renderMarkdown({
            regressions: [{ key: 'svc › GetUser', baseMs: 100, headMs: 180, deltaMs: 80, deltaPct: 80 }],
            improvements: [],
            compared: 3,
        });
        assert.match(md, /svc › GetUser/);
        assert.match(md, /🔴/);
        assert.match(md, /\+80/);
        assert.match(md, /\+80%/);
    });

    it('caps the table and says how many regressions were left out', () => {
        const regressions = Array.from({ length: 12 }, (_, i) => ({
            key: `svc › T${i}`, baseMs: 100, headMs: 200, deltaMs: 100, deltaPct: 100,
        }));
        const md = renderMarkdown({ regressions, improvements: [], compared: 12 });
        assert.match(md, /2 more regression/);
        assert.equal(md.includes('svc › T11'), false);
    });
});

// The action invokes the script as a CLI (`node perf-delta.mjs …`), which
// the unit tests above bypass by importing its helpers. A regression guard
// for that path: the direct-invocation check used to string-build a
// `file://` URL, which on Windows produced `file://C:/…` against Node's
// `file:///C:/…` — the script then ran as a CLI and silently did nothing,
// writing no report and reporting no regressions.
describe('CLI invocation', () => {
    function run(args, cwd) {
        return spawnSync(process.execPath, [script, ...args], { cwd, encoding: 'utf8' });
    }

    it('writes the report and prints the regression count', () => {
        const dir = mkdtempSync(join(tmpdir(), 'bowire-perf-'));
        try {
            writeFileSync(join(dir, 'base.xml'), junit([
                { name: 'getCurrent', seconds: '0.100' },
                { name: 'getForecast', seconds: '0.400' },
            ]));
            writeFileSync(join(dir, 'head.xml'), junit([
                { name: 'getCurrent', seconds: '0.180' },   // +80ms → regression
                { name: 'getForecast', seconds: '0.150' },  // −250ms → improvement
            ]));

            const res = run(['--head', 'head.xml', '--base', 'base.xml', '--out', 'perf.md'], dir);

            assert.equal(res.status, 0, res.stderr);
            assert.match(res.stdout, /regressions=1/);
            const md = readFileSync(join(dir, 'perf.md'), 'utf8');
            assert.match(md, /## Perf/);
            assert.match(md, /svc › getCurrent/);
            assert.match(md, /🔴/);
            assert.match(md, /🟢/);
        } finally {
            rmSync(dir, { recursive: true, force: true });
        }
    });

    it('reports zero regressions when no base report exists', () => {
        // The action skips the section in this case, but the script must
        // still exit cleanly rather than throwing on the missing file.
        const dir = mkdtempSync(join(tmpdir(), 'bowire-perf-'));
        try {
            writeFileSync(join(dir, 'head.xml'), junit([{ name: 'only', seconds: '0.100' }]));
            const res = run(['--head', 'head.xml', '--base', 'missing.xml', '--out', 'perf.md'], dir);

            assert.equal(res.status, 0, res.stderr);
            assert.match(res.stdout, /regressions=0/);
            assert.match(readFileSync(join(dir, 'perf.md'), 'utf8'), /no latency comparison available/i);
        } finally {
            rmSync(dir, { recursive: true, force: true });
        }
    });
});

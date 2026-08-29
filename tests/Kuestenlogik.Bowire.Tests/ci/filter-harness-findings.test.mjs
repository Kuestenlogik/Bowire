// #614 — the filter that decides which of Bowire's own scan findings are
// Bowire's problem and which describe the CI runner.
//
// It is worth testing rather than eyeballing because both failure directions
// are quiet. Drop too much and a real finding never reaches the Security tab.
// Drop too little and Bowire's own tab carries a high-severity TLS alert
// against a product that does not have a TLS problem — which is worse, because
// somebody will act on it.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const script = resolve(here, '../../../scripts/ci/filter-harness-findings.mjs');

/** A SARIF run shaped the way `bowire scan` writes one. */
function sarif(ruleIds) {
    return {
        version: '2.1.0',
        runs: [{
            tool: {
                driver: {
                    name: 'bowire-scan',
                    rules: ruleIds.map((id) => ({ id, shortDescription: { text: id } })),
                },
            },
            results: ruleIds.map((id) => ({
                ruleId: id,
                level: id.includes('TLS') ? 'error' : 'note',
                message: { text: id },
                locations: [{
                    physicalLocation: { artifactLocation: { uri: 'bowire-scan' } },
                    logicalLocations: [{ fullyQualifiedName: 'http://localhost:5190/' }],
                }],
                partialFingerprints: { bowireRuleAndTarget: `${id}@http://localhost:5190/` },
            })),
        }],
    };
}

function withFixture(run) {
    const dir = mkdtempSync(join(tmpdir(), 'bowire-filter-'));
    try {
        return run(dir);
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
}

function writeRules(dir, rules) {
    const path = join(dir, 'rules.json');
    writeFileSync(path, JSON.stringify({ rules }));
    return path;
}

function writeSarif(dir, name, ruleIds) {
    const path = join(dir, name);
    writeFileSync(path, JSON.stringify(sarif(ruleIds)));
    return path;
}

function run(args) {
    return execFileSync(process.execPath, [script, ...args], { encoding: 'utf8' });
}

function read(path) {
    return JSON.parse(readFileSync(path, 'utf8')).runs[0];
}

// ---- what it drops ----

test('a harness finding is removed and the real ones stay', () => {
    withFixture((dir) => {
        const rules = writeRules(dir, { 'BWR-BUILTIN-TLS-001': 'no certificate on a runner' });
        const file = writeSarif(dir, 'f.sarif',
            ['BWR-REST-001', 'BWR-BUILTIN-TLS-001', 'BWR-BUILTIN-BANNER-SERVER']);

        run([file, '--rules', rules]);

        assert.deepEqual(read(file).results.map((r) => r.ruleId),
            ['BWR-REST-001', 'BWR-BUILTIN-BANNER-SERVER']);
    });
});

test('the dropped rule takes its description with it', () => {
    // A rule declared but never referenced is legal SARIF and shows up in the
    // Security tab's rule list, where it reads as something Bowire reports and
    // nobody ever sees fire.
    withFixture((dir) => {
        const rules = writeRules(dir, { 'BWR-BUILTIN-TLS-001': 'harness' });
        const file = writeSarif(dir, 'f.sarif', ['BWR-REST-001', 'BWR-BUILTIN-TLS-001']);

        run([file, '--rules', rules]);

        assert.deepEqual(read(file).tool.driver.rules.map((r) => r.id), ['BWR-REST-001']);
    });
});

test('a finding nobody excluded survives untouched', () => {
    // The severity is deliberately not part of the decision: the findings that
    // are genuinely Bowire's are the low ones, and a severity floor would drop
    // exactly those.
    withFixture((dir) => {
        const rules = writeRules(dir, { 'BWR-BUILTIN-TLS-001': 'harness' });
        const file = writeSarif(dir, 'f.sarif', ['BWR-BUILTIN-BANNER-SERVER']);

        run([file, '--rules', rules]);

        const kept = read(file).results;
        assert.equal(kept.length, 1);
        assert.equal(kept[0].level, 'note');
    });
});

test('every file named on the command line is filtered', () => {
    // The workflow scans three surfaces separately, so a filter that quietly
    // handled only the first would upload the harness finding twice.
    withFixture((dir) => {
        const rules = writeRules(dir, { 'BWR-BUILTIN-TLS-001': 'harness' });
        const a = writeSarif(dir, 'a.sarif', ['BWR-REST-001', 'BWR-BUILTIN-TLS-001']);
        const b = writeSarif(dir, 'b.sarif', ['BWR-BUILTIN-TLS-001']);

        run([a, b, '--rules', rules]);

        assert.deepEqual(read(a).results.map((r) => r.ruleId), ['BWR-REST-001']);
        assert.deepEqual(read(b).results, []);
    });
});

// ---- what it says ----

test('each drop is printed with the reason it was excluded', () => {
    // An exclusion nobody sees is an exclusion that quietly grows.
    withFixture((dir) => {
        const rules = writeRules(dir, { 'BWR-BUILTIN-TLS-001': 'no certificate on a runner' });
        const file = writeSarif(dir, 'f.sarif', ['BWR-REST-001', 'BWR-BUILTIN-TLS-001']);

        const output = run([file, '--rules', rules]);

        assert.match(output, /drop BWR-BUILTIN-TLS-001/);
        assert.match(output, /no certificate on a runner/);
        assert.match(output, /localhost:5190/);
        assert.match(output, /1 finding\(s\) kept, 1 dropped/);
    });
});

test('an empty result set is called out rather than passed over', () => {
    // Nothing left to upload is either a clean Bowire or a scan that found
    // nothing at all, and those look identical from here.
    withFixture((dir) => {
        const rules = writeRules(dir, { 'BWR-BUILTIN-TLS-001': 'harness' });
        const file = writeSarif(dir, 'f.sarif', ['BWR-BUILTIN-TLS-001']);

        const output = run([file, '--rules', rules]);

        assert.match(output, /::notice::No findings left to upload/);
    });
});

// ---- refusals ----

test('no arguments is a usage error, not a silent success', () => {
    assert.throws(() => run([]), (e) => e.status === 2);
});

test('a rules file without a rules object is refused', () => {
    // Rather than treated as "exclude nothing", which would upload the
    // harness finding while looking like it worked.
    withFixture((dir) => {
        const path = join(dir, 'rules.json');
        writeFileSync(path, JSON.stringify({ $comment: 'no rules key' }));
        const file = writeSarif(dir, 'f.sarif', ['BWR-REST-001']);

        assert.throws(() => run([file, '--rules', path]), (e) => e.status === 2);
    });
});

// ---- the default rules path ----

test('without --rules it uses the checked-in list and still filters every file', () => {
    // The regression this exists for: every other test passes --rules, and the
    // argument parser skipped the FIRST file whenever the flag was absent —
    // indexOf returns -1, and -1 + 1 is index 0. The default path is the one
    // the workflow actually uses.
    withFixture((dir) => {
        const a = writeSarif(dir, 'a.sarif', ['BWR-REST-001', 'BWR-BUILTIN-TLS-001']);
        const b = writeSarif(dir, 'b.sarif', ['BWR-BUILTIN-TLS-001']);

        execFileSync(process.execPath, [script, a, b], {
            encoding: 'utf8',
            cwd: resolve(here, '../../..'),
        });

        assert.deepEqual(read(a).results.map((r) => r.ruleId), ['BWR-REST-001']);
        assert.deepEqual(read(b).results, []);
    });
});

// ---- the checked-in list ----

test('the repository rules file is loadable and reasoned', () => {
    const path = resolve(here, '../../../scripts/ci/harness-scan-rules.json');
    const parsed = JSON.parse(readFileSync(path, 'utf8'));

    assert.ok(parsed.rules, 'no rules object');
    for (const [id, reason] of Object.entries(parsed.rules)) {
        assert.match(id, /^BWR-/, `${id} does not look like a Bowire rule id`);
        // The reason is what makes the exclusion arguable rather than a list
        // somebody appends to when a build goes red.
        assert.ok(reason.length > 30, `${id} has no real reason attached`);
    }
});

test('the Server banner is not excluded', () => {
    // It is emitted because Bowire does not set AddServerHeader = false, which
    // is true behind any ingress. Excluding it would hide something Bowire can
    // actually fix.
    const path = resolve(here, '../../../scripts/ci/harness-scan-rules.json');
    const parsed = JSON.parse(readFileSync(path, 'utf8'));

    assert.ok(!Object.hasOwn(parsed.rules, 'BWR-BUILTIN-BANNER-SERVER'));
});

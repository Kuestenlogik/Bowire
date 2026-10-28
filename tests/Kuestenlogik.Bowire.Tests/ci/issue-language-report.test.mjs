// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// scripts/ci/report-issue-language.mjs — #778. The open issues and milestones are read with the
// English-only guard's rules and reported, never failed on; an issue is asked once for English.
// Also: the guard itself leaves the generated ROADMAP.md alone. No network: every input is a
// literal here.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, join, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const scripts = resolve(__dirname, '../../../scripts/ci');
const report = await import(pathToFileURL(join(scripts, 'report-issue-language.mjs')).href);
const guard = await import(pathToFileURL(join(scripts, 'check-english.mjs')).href);
const { MARKER, prose, commentBodies, flag, hasAsked, toAsk, commentBody, annotation, cell } = report;

const germanTitle = 'Verbindung bricht ab, wenn der Proxy nicht erreichbar ist';
const germanBody = 'Das passiert immer dann, wenn man die Einstellungen nicht speichert und das Fenster schliesst.';

describe('the English-only guard and ROADMAP.md', () => {
    it('allowlists ROADMAP.md with a reason', () => {
        const entry = guard.loadAllowlist().find(e => e.path === 'ROADMAP.md');
        assert.ok(entry, 'ROADMAP.md is generated from issue titles and must not fail the guard');
        assert.ok(entry.reason.trim().length > 10);
    });

    it('does not report German in ROADMAP.md, and still does in any other file', () => {
        const root = mkdtempSync(join(tmpdir(), 'bowire-lang-'));
        try {
            writeFileSync(join(root, 'ROADMAP.md'), `# Roadmap\n\n- #900 ${germanTitle}\n`);
            writeFileSync(join(root, 'NOTES.md'), `# Notes\n\n- ${germanTitle}\n`);
            const findings = guard.scan(['ROADMAP.md', 'NOTES.md'], { root });
            assert.deepEqual(findings.map(f => `${f.file}:${f.line}`), ['NOTES.md:3']);
        } finally {
            rmSync(root, { recursive: true, force: true });
        }
    });
});

describe('flag', () => {
    const issues = [
        { number: 1, title: germanTitle, body: 'Steps: open the workbench.', url: 'u/1' },
        { number: 2, title: 'Proxy connection drops when unreachable', body: germanBody, url: 'u/2' },
        { number: 3, title: 'An English title', body: 'An English body that says what to do.', url: 'u/3' },
        { number: 4, title: 'Scaffold from a German prompt', body: '```\n' + germanBody + '\n```\nThe parser reads the `' + germanBody + '` input.', url: 'u/4' },
        { number: 5, title: 'Null body', body: null, url: 'u/5' },
    ];
    const milestones = [
        { number: 25, title: 'v2.10 — Discoverability', description: 'Scope set on 2026-09-19. ' + germanBody, html_url: 'm/25' },
        { number: 26, title: 'v2.13 — Benchmark', description: '', html_url: 'm/26' },
    ];
    const flagged = flag({ issues, milestones });

    it('finds a German title and a German body', () => {
        const issue1 = flagged.find(f => f.number === 1 && f.kind === 'issue');
        assert.deepEqual(issue1.hits.map(h => h.field), ['title']);
        const issue2 = flagged.find(f => f.number === 2 && f.kind === 'issue');
        assert.deepEqual(issue2.hits.map(h => `${h.field}:${h.line}`), ['body:1']);
    });

    it('leaves English issues, fenced code, inline code and empty bodies alone', () => {
        const numbers = flagged.filter(f => f.kind === 'issue').map(f => f.number);
        assert.deepEqual(numbers, [1, 2]);
    });

    it('reports a milestone by its description', () => {
        const m = flagged.filter(f => f.kind === 'milestone');
        assert.deepEqual(m.map(x => [x.number, x.url, x.hits[0].field]), [[25, 'm/25', 'description']]);
    });

    it('reports nothing for nothing', () => {
        assert.deepEqual(flag(), []);
    });
});

describe('prose', () => {
    it('drops fenced code, inline code and HTML comments and keeps the line numbers', () => {
        const text = ['first', '~~~json', '{ "a": 1 }', '~~~', 'see `code` here', '<!-- hidden', 'note -->', 'last'].join('\n');
        assert.deepEqual(prose(text).split('\n'), ['first', '', '', '', 'see  here', '', '', 'last']);
    });
});

describe('asking once', () => {
    it('recognises its own comment by the marker', () => {
        assert.ok(hasAsked(['Thanks!', `${MARKER}\nThe title of this issue read as German.`]));
        assert.ok(!hasAsked(['Thanks!', null, 'Please write this in English.']));
        assert.ok(!hasAsked([]));
    });

    it('reads the comment bodies gh prints one JSON string per line', () => {
        const out = `"Thanks!"\r\n${JSON.stringify(`${MARKER}\nline two`)}\n\n`;
        assert.deepEqual(commentBodies(out), ['Thanks!', `${MARKER}\nline two`]);
        assert.ok(hasAsked(commentBodies(out)));
        assert.deepEqual(commentBodies(''), []);
    });

    it('asks flagged issues that have not been asked, never a milestone', () => {
        const flagged = [
            { kind: 'issue', number: 1, hits: [{ field: 'title' }] },
            { kind: 'issue', number: 2, hits: [{ field: 'body' }] },
            { kind: 'milestone', number: 3, hits: [{ field: 'description' }] },
        ];
        assert.deepEqual(toAsk(flagged, new Set([2])).map(f => f.number), [1]);
        assert.deepEqual(toAsk(flagged, new Set([1, 2])), []);
    });

    it('a re-run over the same issues asks nobody again', () => {
        const issues = [{ number: 7, title: germanTitle, body: germanBody, url: 'u/7' }];
        const first = flag({ issues });
        const posted = new Map(toAsk(first, new Set()).map(f => [f.number, [commentBody(f)]]));
        assert.deepEqual([...posted.keys()], [7]);
        const asked = new Set([...posted].filter(([, bodies]) => hasAsked(bodies)).map(([n]) => n));
        assert.deepEqual(toAsk(flag({ issues }), asked), []);
    });
});

describe('the messages', () => {
    const item = flag({ issues: [{ number: 9, title: germanTitle, body: germanBody, url: 'https://github.com/o/r/issues/9' }] })[0];

    it('asks in English, names the fields, carries the marker', () => {
        const body = commentBody(item);
        assert.ok(body.startsWith(MARKER));
        assert.match(body, /The title and body of this issue read as German/);
        assert.deepEqual(guard.findGerman(body), []);
    });

    it('is a single-line warning annotation with escaped properties', () => {
        const line = annotation(item);
        assert.match(line, /^::warning title=Issue language%3A Issue #9::Issue #9 reads as German in its title and body \(/);
        assert.ok(line.endsWith('https://github.com/o/r/issues/9'));
        assert.ok(!line.includes('\n'));
        const milestone = annotation({ kind: 'milestone', number: 1, title: 'v1, a: b', url: 'm', hits: [{ field: 'description', reason: 'r\nx' }] });
        assert.match(milestone, /^::warning title=Issue language%3A Milestone "v1%2C a%3A b"::/);
        assert.ok(milestone.includes('(r%0Ax)'));
    });
});

describe('the summary table', () => {
    it('escapes backslashes before pipes, so a cell cannot break out of its column', () => {
        // A title ending in a backslash right before a pipe: escaping only the
        // pipe would give \\| — an escaped backslash and a live column break.
        assert.equal(cell('a\\|b'), 'a\\\\\\|b');
        assert.equal(cell('plain'), 'plain');
        assert.equal(cell('x|y'), 'x\\|y');
    });
});

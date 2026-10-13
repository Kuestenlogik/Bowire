// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// scripts/ci/check-english.mjs — #777, the repository is English.
//
// The detector has to find a German sentence quoted inside an English comment, leave English
// prose, names such as Küstenlogik and place names alone, and honour the two ways of saying that
// German is right somewhere: an allowlist entry with a reason, or `lang-exempt:` at the line.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const guard = await import(pathToFileURL(resolve(__dirname, '../../../scripts/ci/check-english.mjs')).href);
const { germanReason, findGerman, isAllowed, loadAllowlist, scan, trackedFiles } = guard;

describe('germanReason', () => {
    it('finds a German comment', () => {
        assert.ok(germanReason('// Deckt auch die Extension ab, deren Logik wird von dort geprueft.'));
    });

    it('finds German quoted inside an English comment', () => {
        assert.ok(germanReason("// parity. Operator feedback: 'das man auch alternativ einfach"));
        assert.ok(germanReason("   the SVG entirely. Operator feedback: 'die icon-buttons unten in der"));
    });

    it('finds a word with an umlaut beside a German word', () => {
        assert.ok(germanReason("   feedback: 'typisch würde man einen nicht klickbaren"));
    });

    it('leaves English prose alone', () => {
        assert.equal(germanReason('// The parser reads the sentence and fills the spec with what it found.'), null);
        assert.equal(germanReason('Below 1400 px the right-hand drawer family becomes an overlay.'), null);
    });

    it('leaves names and place names alone', () => {
        assert.equal(germanReason("alt: 'Bowire — the multi-protocol API workbench. By Küstenlogik.'"), null);
        assert.equal(germanReason('new Location("munich-hbf", "München Hauptbahnhof", "station")'), null);
    });
});

describe('findGerman', () => {
    it('reports the line number', () => {
        const hits = findGerman('first line\n// das ist nicht richtig und fehlt\nlast line');
        assert.deepEqual(hits.map(h => h.line), [2]);
    });

    it('honours lang-exempt on the line and on the line above', () => {
        const text = [
            'Parse("REST-API für Kunden mit Name und Größe"); // lang-exempt: the input under test',
            '// lang-exempt: a German catalogue fixture',
            "de: { greet: 'Sie sehen den Arbeitsplatz von {name}. Ihre Änderungen bleiben Ihre.' },",
        ].join('\n');
        assert.deepEqual(findGerman(text), []);
    });

    it('needs a reason after lang-exempt', () => {
        assert.equal(findGerman('// das ist nicht richtig und fehlt // lang-exempt:').length, 1);
    });
});

describe('allowlist', () => {
    it('gives every entry a reason', () => {
        for (const e of loadAllowlist()) assert.ok(e.reason.trim().length > 10, `${e.path} needs a reason`);
    });

    it('matches a file exactly and a folder by its trailing slash', () => {
        const list = [{ path: 'a/de.json', reason: 'x' }, { path: 'fixtures/', reason: 'y' }];
        assert.ok(isAllowed('a/de.json', list));
        assert.ok(!isAllowed('a/de.json.bak', list));
        assert.ok(isAllowed('fixtures/deep/file.txt', list));
        assert.ok(!isAllowed('fixturesX/file.txt', list));
    });
});

describe('the repository', () => {
    it('reads as English everywhere outside the allowlist', () => {
        const findings = scan(trackedFiles());
        assert.deepEqual(findings.map(f => `${f.file}:${f.line}: ${f.text.slice(0, 120)}`), []);
    });
});

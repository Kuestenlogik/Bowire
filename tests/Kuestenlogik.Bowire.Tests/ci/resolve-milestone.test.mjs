// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// scripts/ci/resolve-milestone.mjs — which section a tag delivers from.
//
// A version number cannot find its milestone: the number is chosen at the cut, the section was
// named months earlier. So the release says which section it ships, and this reads that in three
// places. The order between them is the whole design — what the tag states beats what the script
// can infer — and rule 3 has to hold for a delivery made *from* a section as well as one that
// finishes it, because a section is delivered from once or several times.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve as resolvePath } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const script = resolvePath(__dirname, '../../../scripts/ci/resolve-milestone.mjs');
const { resolve, outputs, theme, order } = await import(pathToFileURL(script).href);

const ms = (title, { state = 'open', open_issues = 0 } = {}) => ({ title, state, open_issues });
const titles = r => r.chosen.map(m => m.title);

const M1 = ms('M1 — Localisation, layout and the test pillar', { open_issues: 9 });
const M2 = ms('M2 — MCP completion + agent hub', { open_issues: 6 });
const M3 = ms('M3 — Discoverability', { open_issues: 5 });
const SHIPPED = ms('M0 — Groundwork', { state: 'closed' });
const OLD = ms('v2.7 — Geospatial map');

describe('rule 1 — the tag message names the sections', () => {
    it('reads an M-token out of the annotated tag body', () => {
        const r = resolve('v2.8.0', [M1, M2], 'Bowire v2.8.0 — M1 — Localisation, layout and the test pillar');
        assert.deepEqual(titles(r), [M1.title]);
        assert.match(r.how, /tag message names M1/);
    });

    it('carries several sections when one cut ships them', () => {
        // A second section that finished at the same time rides along.
        const r = resolve('v2.8.0', [M1, M2, M3], 'v2.8.0 — M1 + M2');
        assert.deepEqual(titles(r), [M1.title, M2.title]);
    });

    it('counts a repeated token once', () => {
        const r = resolve('v2.8.0', [M1, M2], 'M1 — … (M1 again)');
        assert.deepEqual(titles(r), [M1.title]);
    });

    it('drops a token that names no milestone rather than failing', () => {
        const r = resolve('v2.8.0', [M1], 'M1 and M7');
        assert.deepEqual(titles(r), [M1.title]);
    });

    it('beats what the later rules would have inferred', () => {
        // The tag is a statement; rules 2 and 3 are guesses. A cut that ships M2 out of order
        // must not be relabelled M1 just because M1 sits in front.
        const r = resolve('v2.8.0', [M1, M2], 'v2.8.0 — M2');
        assert.deepEqual(titles(r), [M2.title]);
    });
});

describe('rule 2 — the old convention, version in the title', () => {
    it('matches a bare version title', () => {
        const r = resolve('v2.7.0', [ms('v2.7'), M1], '');
        assert.deepEqual(titles(r), ['v2.7']);
        assert.match(r.how, /title starts with v2\.7/);
    });

    it('matches a version title with a theme tail', () => {
        const r = resolve('v2.7.0', [OLD, M1], '');
        assert.deepEqual(titles(r), [OLD.title]);
    });

    it('prefers the exact version over the minor line', () => {
        const exact = ms('v2.7.1 — a patch of its own');
        const r = resolve('v2.7.1', [OLD, exact], '');
        assert.deepEqual(titles(r), [exact.title]);
    });

    it('does not match a title that merely starts with the digits', () => {
        const r = resolve('v2.7.0', [ms('v2.70 — something else')], '');
        assert.deepEqual(titles(r), []);
    });
});

describe('rule 3 — the frontmost open section', () => {
    it('resolves a section that is still in progress, and says it inferred that', () => {
        // The case the old code refused: an in-between delivery ships from a section with open
        // tickets. Falling through here handed the release no theme at all.
        const r = resolve('v2.8.0', [M2, M1, M3], '');
        assert.deepEqual(titles(r), [M1.title]);
        assert.match(r.how, /M1 is the one in progress .* 9 ticket\(s\) still open/);
    });

    it('still calls a finished section complete', () => {
        const done = ms('M1 — Localisation', { open_issues: 0 });
        const r = resolve('v2.8.0', [M2, done], '');
        assert.match(r.how, /M1 is complete/);
    });

    it('orders by M-number, not by the list it was given', () => {
        const r = resolve('v2.8.0', [M3, M2, M1], '');
        assert.deepEqual(titles(r), [M1.title]);
    });

    it('ignores a section that already shipped', () => {
        const r = resolve('v2.8.0', [SHIPPED, M2], '');
        assert.deepEqual(titles(r), [M2.title]);
    });

    it('ignores a milestone that is not a numbered section', () => {
        // An old `v2.7 — …` milestone is open too; it is not the next section.
        const r = resolve('v2.8.0', [OLD, M2], '');
        assert.deepEqual(titles(r), [M2.title]);
    });

    it('resolves nothing when there is no open section to deliver from', () => {
        const r = resolve('v2.8.0', [SHIPPED], '');
        assert.deepEqual(titles(r), []);
        assert.equal(r.how, '');
    });
});

describe('outputs', () => {
    it('gives the workflow its four values', () => {
        const out = outputs([M1, M2]);
        assert.equal(out.milestones, `${M1.title}|${M2.title}`);
        assert.equal(out.milestone, M1.title);
        assert.equal(out.theme, 'Localisation, layout and the test pillar + MCP completion + agent hub');
        assert.equal(out.numbers, 'M1 M2');
    });

    it('is empty rather than absent when nothing resolved', () => {
        // release.yml reads these with `|| ''`; the keys still have to be there.
        assert.deepEqual(outputs([]), { milestones: '', milestone: '', theme: '', numbers: '' });
    });
});

describe('theme and order', () => {
    it('strips the section prefix off a title', () => {
        assert.equal(theme('M1 — Localisation, layout and the test pillar'), 'Localisation, layout and the test pillar');
        assert.equal(theme('v2.7 — Geospatial map'), 'Geospatial map');
        assert.equal(theme('M1'), '');
    });

    it('sorts numbered sections and parks everything else at the end', () => {
        assert.equal(order('M10 — late'), 10);
        assert.equal(order('v2.7 — old'), Number.POSITIVE_INFINITY);
    });
});

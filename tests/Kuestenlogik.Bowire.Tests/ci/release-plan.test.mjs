// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// scripts/ci/release-plan.mjs — the rules every release script shares.
//
// A ticket names three things: the Product that ships it (and so the version line it counts in),
// the Release it is planned for in that line, and the milestone of that release. The rules below
// decide what a release contains, when it is due and when a ticket's three fields disagree; the
// scripts around them only read and write the board.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const plan = await import(pathToFileURL(resolve(__dirname, '../../../scripts/ci/release-plan.mjs')).href);
const check = await import(pathToFileURL(resolve(__dirname, '../../../scripts/ci/check-release-plan.mjs')).href);
const { parseMilestone, releaseOfVersion, releaseMatches, compareVersions, planFor, problems, releases, productOfRepo, view } = plan;

const CUT = '2026-09-07T20:39:34Z';
let seq = 0;
const it_ = (over = {}) => ({
    id: 'I' + (++seq), repo: 'Kuestenlogik/Bowire', number: seq, title: 't' + seq, state: 'OPEN', notPlanned: false,
    closedAt: null, milestone: 'v2.8 — Theme', product: 'Bowire', release: '2.8', ...over,
});

describe('parseMilestone', () => {
    it('reads the version and the theme', () => {
        assert.deepEqual(parseMilestone('v2.8 — Localisation and the agent hub'), { product: null, version: '2.8', theme: 'Localisation and the agent hub' });
        assert.deepEqual(parseMilestone('v2.8.1 — Fixes'), { product: null, version: '2.8.1', theme: 'Fixes' });
    });
    it('reads a second product in the same repository', () => {
        assert.deepEqual(parseMilestone('VS Code v1.1 — Marketplace publish'), { product: 'VS Code', version: '1.1', theme: 'Marketplace publish' });
    });
    it('is nothing for a title that names no version', () => {
        // The old work sections: a milestone without a version cannot say when it ships.
        assert.equal(parseMilestone('M3 — Discoverability'), null);
        assert.equal(parseMilestone('Backlog'), null);
    });
});

describe('releaseOfVersion and releaseMatches', () => {
    it('plans a minor release as major.minor and a patch as itself', () => {
        assert.equal(releaseOfVersion('v2.8.0'), '2.8');
        assert.equal(releaseOfVersion('2.8.1'), '2.8.1');
        assert.equal(releaseOfVersion('v3.0.0-rc.1'), '3.0');
    });
    it('matches 2.8 and 2.8.0 in 2.8.0, but not in the patch after it', () => {
        assert.ok(releaseMatches('2.8', 'v2.8.0'));
        assert.ok(releaseMatches('2.8.0', '2.8.0'));
        assert.ok(!releaseMatches('2.8', '2.8.1'));
        assert.ok(releaseMatches('2.8.1', 'v2.8.1'));
        assert.ok(!releaseMatches(null, '2.8.0'));
    });
    it('orders 2.10 after 2.9', () => {
        assert.ok(compareVersions('2.10', '2.9') > 0);
        assert.ok(compareVersions('3.0', '2.13') > 0);
    });
});

describe('planFor', () => {
    it('ships the done tickets of this product and release, and names the open ones', () => {
        const items = [
            it_({ state: 'CLOSED', closedAt: '2026-09-20T00:00:00Z' }),
            it_(),                                                                    // still open
            it_({ state: 'CLOSED', release: '2.9' }),                                 // another release
            it_({ state: 'CLOSED', product: 'Protocol.Akka', release: '2.8' }),       // another product's 2.8
            it_({ state: 'CLOSED', notPlanned: true }),                               // never shipped
        ];
        const r = planFor(items, 'Bowire', '2.8.0', CUT);
        assert.deepEqual(r.shipped.map(x => x.number), [items[0].number]);
        assert.deepEqual(r.stillOpen.map(x => x.number), [items[1].number]);
        assert.deepEqual(r.unplanned, []);
    });
    it('claims what closed since the previous release without a plan — and only that', () => {
        const before = it_({ state: 'CLOSED', release: null, closedAt: '2026-09-01T00:00:00Z' });
        const after = it_({ state: 'CLOSED', release: null, closedAt: '2026-09-20T00:00:00Z' });
        const dropped = it_({ state: 'CLOSED', release: null, closedAt: '2026-09-20T00:00:00Z', notPlanned: true });
        const r = planFor([before, after, dropped], 'Bowire', '2.8.0', CUT);
        assert.deepEqual(r.unplanned.map(x => x.number), [after.number]);
    });
});

describe('problems', () => {
    it('is nothing when product, release and milestone agree', () => {
        assert.deepEqual(problems(it_()), []);
        assert.deepEqual(problems(it_({ product: 'VS Code', release: '1.1', milestone: 'VS Code v1.1 — X' })), []);
        assert.deepEqual(problems(it_({ repo: 'Kuestenlogik/Bowire.Protocol.Akka', product: 'Protocol.Akka', release: '1.2', milestone: 'v1.2 — X' })), []);
    });
    it('names what is missing or disagrees', () => {
        assert.deepEqual(problems(it_({ release: null })), ['no Release']);
        assert.deepEqual(problems(it_({ milestone: null })), ['no milestone']);
        assert.deepEqual(problems(it_({ release: '2.9' })), ['milestone is v2.8, Release is 2.9']);
        assert.deepEqual(problems(it_({ product: 'VS Code' })), ['milestone is Bowire, Product is VS Code']);
        assert.deepEqual(problems(it_({ milestone: 'M3 — Discoverability' })), ["milestone 'M3 — Discoverability' names no version"]);
    });
    it('leaves closed tickets alone', () => {
        assert.deepEqual(problems(it_({ state: 'CLOSED', release: null, milestone: null })), []);
    });
});

describe('releases', () => {
    it('lists the open releases of the product, lowest first, and shows which is due', () => {
        const ms = [
            { title: 'v2.10 — C', state: 'open', open_issues: 1 },
            { title: 'v2.9 — B', state: 'open', open_issues: 1 },
            { title: 'v2.8 — A', state: 'open', open_issues: 0 },
            { title: 'v2.7 — old', state: 'closed', open_issues: 0 },
            { title: 'VS Code v1.1 — D', state: 'open', open_issues: 1 },
        ];
        const items = [
            it_({ state: 'CLOSED' }),
            it_({ release: '2.9', milestone: 'v2.9 — B' }),
            it_({ release: '2.10', milestone: 'v2.10 — C' }),
        ];
        const list = releases(ms, 'Kuestenlogik/Bowire', 'Bowire', items);
        assert.deepEqual(list.map(r => r.version), ['2.8', '2.9', '2.10']);
        assert.deepEqual(list.map(r => r.open), [0, 1, 1]);
        assert.equal(list[0].done, 1);
    });
});

describe('products', () => {
    it('knows the product of each repository', () => {
        assert.equal(productOfRepo('Kuestenlogik/Bowire'), 'Bowire');
        assert.equal(productOfRepo('Bowire.Protocol.Akka'), 'Protocol.Akka');
        assert.equal(productOfRepo('Kuestenlogik/Unrelated'), null);
    });
    it('reads a board item, taking the repository product when the field is empty', () => {
        const v = view({ id: 'X', content: { number: 1, state: 'OPEN', repository: { nameWithOwner: 'Kuestenlogik/Bowire.Sdk.Go' }, milestone: { title: 'v0.1 — P' } }, release: { text: '0.1' } });
        assert.equal(v.product, 'Sdk.Go');
        assert.equal(v.release, '0.1');
        assert.deepEqual(problems(v), []);
    });
});

describe('check-release-plan previousCut', () => {
    const rel = [
        { tag: 'v2.6.2', at: '2026-09-04T12:48:27Z' },
        { tag: 'v2.7.0', at: CUT },
        { tag: 'v2.8.0-rc.1', at: '2026-09-20T00:00:00Z' },
    ];
    it('is the release before this one, skipping prereleases', () => {
        assert.equal(check.previousCut('v2.8.0', rel).tag, 'v2.7.0');
        assert.equal(check.previousCut('v2.7.0', rel).tag, 'v2.6.2');
        assert.equal(check.previousCut('v2.6.2', rel), null);
    });
});

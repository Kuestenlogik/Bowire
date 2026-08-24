// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// scripts/ci/generate-changelog.mjs — the change list under a release body.
//
// The rules worth pinning are the ones that decide what a reader sees
// first: the Conventional Commit prefix is consumed and removed rather than
// printed, chores and CI plumbing go below the fold whatever their type
// claims, and dependency bumps collapse to a count. Getting these wrong is
// how v2.4.0 shipped a "What's Changed" list of 41 Dependabot entries and
// nothing else.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const script = resolve(__dirname, '../../../scripts/ci/generate-changelog.mjs');
const { classify, render } = await import(`file://${script}`);

const c = (subject, { sha = 'abcdef1234', author = 'Dev', body = '' } = {}) =>
    ({ sha, author, subject, body });

const opts = { from: 'v1.0.0', to: 'v1.1.0', repoUrl: 'https://example.com/o/r' };

describe('classify', () => {
    it('routes the conventional types to their reading sections', () => {
        const { buckets } = classify([
            c('feat(mock): start a schema mock'),
            c('fix(scan): stop a false positive'),
            c('refactor(core): split the resolver'),
            c('docs(sse): document Last-Event-ID'),
        ]);
        assert.equal(buckets.get('added').length, 1);
        assert.equal(buckets.get('fixed').length, 1);
        assert.equal(buckets.get('changed').length, 1);
        assert.equal(buckets.get('docs').length, 1);
    });

    it('drops the type prefix and keeps the scope as the lead-in', () => {
        const { buckets } = classify([c('feat(mock): start a schema mock')]);
        const [entry] = buckets.get('added');
        assert.equal(entry.text, '**mock** — start a schema mock');
        assert.ok(!entry.text.includes('feat'), 'the type must not survive into the line');
    });

    it('keeps a scopeless subject intact', () => {
        const { buckets } = classify([c('feat: add a thing')]);
        assert.equal(buckets.get('added')[0].text, 'add a thing');
    });

    it('sends chores below the fold', () => {
        const { maintenance, buckets } = classify([c('chore(roadmap): sync from the board')]);
        assert.equal(maintenance.length, 1);
        assert.equal([...buckets.values()].flat().length, 0);
    });

    it('demotes a fix whose SCOPE is plumbing, despite the type', () => {
        // The regression this guards: `fix(test): …` reads as a user-facing
        // fix by type alone, and filled the Fixed section with port races.
        const { maintenance, buckets } = classify([
            c('fix(test): retry the wire-server bind'),
            c('fix(ci): pin the runner image'),
            c('fix(mock): honour declared examples'),
        ]);
        assert.equal(maintenance.length, 2);
        assert.equal(buckets.get('fixed').length, 1);
    });

    it('collapses dependency bumps, by scope and by bot author', () => {
        const { deps, maintenance } = classify([
            c('chore(deps): Bump xunit', { author: 'dependabot[bot]' }),
            c('chore(npm): Bump playwright'),
            c('Bump something with no convention at all', { author: 'dependabot[bot]' }),
        ]);
        assert.equal(deps.length, 3);
        assert.equal(maintenance.length, 0);
    });

    it('lifts a breaking change out, even from a plumbing scope', () => {
        const { breaking, maintenance } = classify([
            c('feat(api)!: drop the v1 endpoint'),
            c('chore(build): retire net8', { body: 'BREAKING CHANGE: net8 is gone' }),
        ]);
        assert.equal(breaking.length, 2);
        assert.equal(maintenance.length, 0);
    });

    it('keeps an unparseable subject rather than dropping it', () => {
        const { other } = classify([c('Merge branch weirdness')]);
        assert.equal(other.length, 1);
        assert.equal(other[0].text, 'Merge branch weirdness');
    });
});

describe('render', () => {
    it('puts real work above the fold and noise inside the details block', () => {
        const md = render([
            c('feat(mock): start a schema mock'),
            c('chore(deps): Bump xunit', { author: 'dependabot[bot]' }),
            c('chore(roadmap): sync'),
        ], opts);

        const detailsAt = md.indexOf('<details>');
        assert.ok(detailsAt > -1, 'a details block is expected');
        assert.ok(md.indexOf('start a schema mock') < detailsAt, 'the feature must precede the fold');
        assert.ok(md.indexOf('Bump xunit') > detailsAt, 'the bump must sit inside the fold');
        assert.match(md, /<summary>Maintenance — 1 maintenance commit · 1 dependency update<\/summary>/);
    });

    it('omits the details block when there is nothing to hide', () => {
        const md = render([c('feat: only real work here')], opts);
        assert.ok(!md.includes('<details>'));
    });

    it('credits people and not bots', () => {
        const md = render([
            c('feat: a thing', { author: 'Thomas Stegemann' }),
            c('chore(deps): Bump x', { author: 'dependabot[bot]' }),
            c('chore: regenerate', { author: 'github-actions[bot]' }),
        ], opts);
        assert.match(md, /\*\*Contributors:\*\* Thomas Stegemann\n/);
        assert.ok(!md.includes('dependabot'), 'bots must not be credited');
    });

    it('ends on a compare link for the range', () => {
        const md = render([c('feat: x')], opts);
        assert.match(md, /\*\*Full diff:\*\* https:\/\/example\.com\/o\/r\/compare\/v1\.0\.0\.\.\.v1\.1\.0/);
    });
});

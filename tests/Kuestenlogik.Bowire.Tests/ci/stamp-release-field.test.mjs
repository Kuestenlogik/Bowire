// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// scripts/ci/stamp-release-field.mjs — which tickets a cut records as shipped.
//
// The board's Release field only earns its place because a milestone holds more than one
// release: a section is delivered at its end and may be delivered from in between. Every rule
// pinned here follows from that, and each one collapses the field back into a copy of the
// milestone if it goes wrong — which is a quiet failure, since the field is written by the
// pipeline and read by people.

import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const script = resolve(__dirname, '../../../scripts/ci/stamp-release-field.mjs');
const { decide, versionOf } = await import(pathToFileURL(script).href);

const M1 = 'M1 — Localisation, layout and the test pillar';
const M2 = 'M2 — MCP completion + agent hub';

// Issue numbers restart per test, so each case reads on its own.
let seq = 0;
beforeEach(() => { seq = 0; });
const item = (milestone, state, release = null) => ({
    id: 'PVTI_' + (++seq),
    content: { __typename: 'Issue', number: seq, state, milestone: milestone ? { title: milestone } : null },
    fieldValueByName: release ? { name: release } : null,
});
const numbers = list => list.map(i => i.content.number);

describe('versionOf', () => {
    it('names the delivery, not the minor line', () => {
        // Release notes are written per cut. Truncating to v2.8 would give the two deliveries
        // one label and make the field unable to say which notes describe a ticket.
        assert.equal(versionOf('v2.8.0'), 'v2.8.0');
        assert.equal(versionOf('v2.8.1'), 'v2.8.1');
        assert.notEqual(versionOf('v2.8.0'), versionOf('v2.8.1'));
    });

    it('stamps a prerelease as the version it previews', () => {
        // The ticket ships in the v2.8.0 line; the first-stamp rule then leaves the GA cut alone
        // rather than the board carrying "v2.8.0-rc.1" forever.
        assert.equal(versionOf('v2.8.0-rc.1'), 'v2.8.0');
        assert.equal(versionOf('v3.0.0-preview.2'), 'v3.0.0');
    });

    it('tolerates a tag given without its v', () => {
        assert.equal(versionOf('2.8.0'), 'v2.8.0');
    });
});

describe('decide', () => {
    it('stamps the closed tickets of the shipped section', () => {
        const items = [item(M1, 'CLOSED'), item(M1, 'CLOSED')];
        const { toStamp } = decide(items, [M1], 'v2.8.0');
        assert.deepEqual(numbers(toStamp), [1, 2]);
    });

    it('holds back a ticket that is still open', () => {
        // The reason the field needs a state filter at all: an in-between cut happens while the
        // section is unfinished, so its open tickets are on the board and must not be claimed.
        const items = [item(M1, 'CLOSED'), item(M1, 'OPEN')];
        const { toStamp, stillOpen } = decide(items, [M1], 'v2.8.0');
        assert.deepEqual(numbers(toStamp), [1]);
        assert.deepEqual(numbers(stillOpen), [2]);
    });

    it('never overwrites an earlier release', () => {
        // A ticket ships once. Overwriting would drag everything the section ever closed onto
        // the newest cut, leaving the field saying no more than the milestone already says.
        const items = [item(M1, 'CLOSED', 'v2.8.0'), item(M1, 'CLOSED')];
        const { toStamp, alreadyShipped } = decide(items, [M1], 'v2.8.1');
        assert.deepEqual(numbers(toStamp), [2]);
        assert.deepEqual(numbers(alreadyShipped), [1]);
    });

    it('re-stamping the same version is a no-op, not a repeat', () => {
        // A rerun of the release job must not edit items it already edited.
        const items = [item(M1, 'CLOSED', 'v2.8.0')];
        const { toStamp, alreadyShipped } = decide(items, [M1], 'v2.8.0');
        assert.deepEqual(toStamp, []);
        assert.deepEqual(alreadyShipped, []);
    });

    it('leaves other sections alone', () => {
        const items = [item(M1, 'CLOSED'), item(M2, 'CLOSED'), item(null, 'CLOSED')];
        const { toStamp, stillOpen, alreadyShipped } = decide(items, [M1], 'v2.8.0');
        assert.deepEqual(numbers(toStamp), [1]);
        assert.deepEqual(stillOpen, []);
        assert.deepEqual(alreadyShipped, []);
    });

    it('stamps every section a tag ships', () => {
        // A second section that finished at the same time rides along on the same cut.
        const items = [item(M1, 'CLOSED'), item(M2, 'CLOSED')];
        const { toStamp } = decide(items, [M1, M2], 'v2.8.0');
        assert.deepEqual(numbers(toStamp), [1, 2]);
    });

    it('ignores a draft item, which has no content at all', () => {
        const { toStamp } = decide([{ id: 'PVTI_draft', content: null, fieldValueByName: null }], [M1], 'v2.8.0');
        assert.deepEqual(toStamp, []);
    });

    it('delivers a section twice without disturbing the first delivery', () => {
        // The whole model in one case: cut v2.8.0 from an unfinished section, close two more
        // tickets, cut v2.8.1. Each ticket keeps the delivery it actually went out in.
        const items = [item(M1, 'CLOSED'), item(M1, 'CLOSED'), item(M1, 'OPEN'), item(M1, 'OPEN')];

        const first = decide(items, [M1], 'v2.8.0');
        assert.deepEqual(numbers(first.toStamp), [1, 2]);
        for (const it of first.toStamp) it.fieldValueByName = { name: 'v2.8.0' };

        items[2].content.state = 'CLOSED';
        const second = decide(items, [M1], 'v2.8.1');
        assert.deepEqual(numbers(second.toStamp), [3]);
        assert.deepEqual(numbers(second.alreadyShipped), [1, 2]);
        assert.deepEqual(numbers(second.stillOpen), [4]);
    });
});

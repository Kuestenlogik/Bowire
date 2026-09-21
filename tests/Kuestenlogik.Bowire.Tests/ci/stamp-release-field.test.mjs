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
//
// What decides the scope is the previous cut, not the milestone. A release ships what was
// merged since the one before it, whatever section the ticket belongs to; scoping by milestone
// left every ticket from another section unstamped although it went out in the same cut.

import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const script = resolve(__dirname, '../../../scripts/ci/stamp-release-field.mjs');
const { decide, versionOf, previousCut } = await import(pathToFileURL(script).href);

// The cut this suite stamps from, and dates either side of it.
const LAST_CUT = '2026-09-07T20:39:34Z';
const BEFORE = '2026-09-01T10:00:00Z';
const AFTER = '2026-09-15T10:00:00Z';

// Issue numbers restart per test, so each case reads on its own.
let seq = 0;
beforeEach(() => { seq = 0; });
const item = (state, closedAt = AFTER, release = null) => ({
    id: 'PVTI_' + (++seq),
    content: { __typename: 'Issue', number: seq, state, closedAt: state === 'CLOSED' ? closedAt : null },
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

describe('previousCut', () => {
    // All in the past, so the suite does not depend on the day it runs: an unpublished tag is
    // "now", and now is after every date here.
    const releases = [
        { tag: 'v2.6.2', at: '2026-09-04T12:48:27Z' },
        { tag: 'v2.7.0', at: LAST_CUT },
        { tag: 'v2.7.1', at: '2026-09-10T08:00:00Z' },
    ];

    it('is the delivery before this one', () => {
        assert.equal(previousCut('v2.7.1', releases).tag, 'v2.7.0');
        assert.equal(previousCut('v2.7.0', releases).tag, 'v2.6.2');
    });

    it('is nothing for the very first delivery', () => {
        // Then everything closed counts, which is the right answer for a first release.
        assert.equal(previousCut('v2.6.2', releases), null);
    });

    it('takes a tag that has not been published yet as happening now', () => {
        // The job can run before the release is published, and it is the ordinary case: the
        // newest published cut is then the previous one, which is what a fresh tag ships against.
        assert.equal(previousCut('v2.8.0', releases).tag, 'v2.7.1');
    });
});

describe('decide', () => {
    it('stamps what closed since the previous cut', () => {
        const items = [item('CLOSED'), item('CLOSED')];
        const { toStamp } = decide(items, LAST_CUT, 'v2.8.0');
        assert.deepEqual(numbers(toStamp), [1, 2]);
    });

    it('does not care which section the ticket sits in', () => {
        // The defect this replaces. Two tickets, closed in the same window, from different
        // sections: both went out in this cut, so both are recorded. Under the old rule the one
        // from the other section shipped unrecorded, and the release notes built from the field
        // would not have mentioned it.
        const items = [item('CLOSED'), item('CLOSED')];
        items[0].content.milestone = { title: 'M1 — Localisation, layout and the test pillar' };
        items[1].content.milestone = { title: 'M5 — Cleanups + breaking-change cuts' };
        const { toStamp } = decide(items, LAST_CUT, 'v2.8.0');
        assert.deepEqual(numbers(toStamp), [1, 2]);
    });

    it('holds back a ticket that is still open', () => {
        // The reason the field needs a state filter at all: an in-between cut happens while the
        // section is unfinished, so its open tickets are on the board and must not be claimed.
        const items = [item('CLOSED'), item('OPEN')];
        const { toStamp, stillOpen } = decide(items, LAST_CUT, 'v2.8.0');
        assert.deepEqual(numbers(toStamp), [1]);
        assert.deepEqual(numbers(stillOpen), [2]);
    });

    it('leaves a ticket that closed before this cut alone', () => {
        // History the board still carries, from a delivery that predates the field. Claiming it
        // for this cut would be exactly the lie the field exists to prevent.
        const items = [item('CLOSED', BEFORE), item('CLOSED', AFTER)];
        const { toStamp, beforeThisCut } = decide(items, LAST_CUT, 'v2.8.0');
        assert.deepEqual(numbers(toStamp), [2]);
        assert.deepEqual(numbers(beforeThisCut), [1]);
    });

    it('never overwrites an earlier release', () => {
        // A ticket ships once. Overwriting would drag everything onto the newest cut, leaving
        // the field saying no more than the tag already says.
        const items = [item('CLOSED', AFTER, 'v2.8.0'), item('CLOSED')];
        const { toStamp, alreadyShipped } = decide(items, LAST_CUT, 'v2.8.1');
        assert.deepEqual(numbers(toStamp), [2]);
        assert.deepEqual(numbers(alreadyShipped), [1]);
    });

    it('re-stamping the same version is a no-op, not a repeat', () => {
        // A rerun of the release job must not edit items it already edited.
        const items = [item('CLOSED', AFTER, 'v2.8.0')];
        const { toStamp, alreadyShipped } = decide(items, LAST_CUT, 'v2.8.0');
        assert.deepEqual(toStamp, []);
        assert.deepEqual(alreadyShipped, []);
    });

    it('ignores a draft item, which has no content at all', () => {
        const { toStamp } = decide([{ id: 'PVTI_draft', content: null, fieldValueByName: null }],
            LAST_CUT, 'v2.8.0');
        assert.deepEqual(toStamp, []);
    });

    it('counts a closed ticket with no date as history, not as this cut', () => {
        // A board item whose content carries no closedAt — an older API shape, or a draft that
        // grew into an issue. Unknown is not "just now", so it is left alone.
        const items = [item('CLOSED')];
        items[0].content.closedAt = null;
        const { toStamp, beforeThisCut } = decide(items, LAST_CUT, 'v2.8.0');
        assert.deepEqual(toStamp, []);
        assert.deepEqual(numbers(beforeThisCut), [1]);
    });

    it('takes everything closed when there is no previous cut', () => {
        // The first release of a repository ships all of it.
        const items = [item('CLOSED', BEFORE), item('CLOSED', AFTER)];
        const { toStamp } = decide(items, null, 'v1.0.0');
        assert.deepEqual(numbers(toStamp), [1, 2]);
    });

    it('delivers twice without disturbing the first delivery', () => {
        // The whole model in one case: cut v2.8.0, close another ticket, cut v2.8.1. Each ticket
        // keeps the delivery it actually went out in.
        const items = [
            item('CLOSED', '2026-09-10T00:00:00Z'),
            item('CLOSED', '2026-09-10T00:00:00Z'),
            item('OPEN'),
            item('OPEN'),
        ];

        const first = decide(items, LAST_CUT, 'v2.8.0');
        assert.deepEqual(numbers(first.toStamp), [1, 2]);
        for (const it of first.toStamp) it.fieldValueByName = { name: 'v2.8.0' };

        // The second cut's window opens where the first one closed.
        const firstCutAt = '2026-09-12T00:00:00Z';
        items[2].content.state = 'CLOSED';
        items[2].content.closedAt = '2026-09-14T00:00:00Z';
        const second = decide(items, firstCutAt, 'v2.8.1');
        assert.deepEqual(numbers(second.toStamp), [3]);
        assert.deepEqual(numbers(second.alreadyShipped), [1, 2]);
        assert.deepEqual(numbers(second.stillOpen), [4]);
    });
});

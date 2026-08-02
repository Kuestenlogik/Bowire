// #552 — the SSE panel must not render on someone else's schedule.
//
// EventSource reconnects by itself after a drop and fires `onerror` on
// every attempt, at a cadence the REMOTE server's `retry:` directive
// chooses. The panel logged an event and called render() each time, so a
// dead endpoint with `retry: 100` drove ten full-app rebuilds a second
// until the tab was closed. Its MQTT sibling twenty lines up closed the
// source, so this was an omission rather than a design.
//
// These tests drive the handler shape directly rather than loading the
// whole fragment, which pulls in the entire request-builder surface. The
// contract under test is the error policy, and it is small enough to
// state exactly.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/request-builder-protocols.js'),
    'utf8'
);

const CONNECTING = 0;
const CLOSED = 2;

// Rebuild the handler from the shipped source so the test tracks the real
// policy: pull the onerror body and the retry ceiling out of the file.
function loadPolicy() {
    const ceiling = /var SSE_MAX_RETRIES = (\d+);/.exec(SRC);
    assert.ok(ceiling, 'SSE_MAX_RETRIES not found — the ceiling was renamed or removed');

    const start = SRC.indexOf('src.onerror = function () {\n                rbConnState.sseRetries');
    assert.ok(start > 0, 'the SSE onerror handler was not found in its expected shape');
    const END = '\n            };';
    const body = SRC.slice(start, SRC.indexOf(END, start) + END.length);

    return { ceiling: Number(ceiling[1]), body };
}

function harness(readyStateSeq) {
    const { ceiling, body } = loadPolicy();
    const state = { sseEvents: [], sseSource: {}, sseRetries: 0 };
    let renders = 0;
    let closed = false;
    let i = 0;

    const src = {
        get readyState() { return readyStateSeq[Math.min(i, readyStateSeq.length - 1)]; },
        close() { closed = true; },
    };

    const fn = new Function('rbConnState', 'src', 'render', 'SSE_MAX_RETRIES', `
        var Date = { now: function () { return 0; } };
        ${body}
        return src.onerror;
    `)(state, src, () => { renders++; }, ceiling);

    return {
        fire(n) { for (let k = 0; k < n; k++) { fn(); i++; } },
        get renders() { return renders; },
        get closed() { return closed; },
        get events() { return state.sseEvents.map(e => e.data); },
        ceiling,
    };
}

test('a transient drop reports once, not once per reconnect attempt', () => {
    const h = harness([CONNECTING]);
    h.fire(1);
    const afterFirst = h.renders;
    h.fire(3);   // three more retries, still below the ceiling

    assert.equal(afterFirst, 1, 'the first failure is reported');
    assert.equal(h.renders, 1, 'the retries are silent — this is the render loop that is gone');
    assert.equal(h.events.length, 1);
    assert.match(h.events[0], /reconnecting/);
});

test('the source is closed once the retry budget runs out', () => {
    const h = harness([CONNECTING]);
    h.fire(h.ceiling);

    assert.ok(h.closed, 'the panel gives up instead of reconnecting forever');
    assert.match(h.events[h.events.length - 1], /gave up after/);
});

test('giving up is said out loud, not swallowed', () => {
    // An operator watching a stream needs to know it is gone. Closing
    // silently would look identical to a stream that simply went quiet.
    const h = harness([CONNECTING]);
    h.fire(h.ceiling);
    assert.equal(h.events.length, 2, 'one "reconnecting", one "gave up"');
});

test('a browser-side CLOSED state closes immediately, without burning the budget', () => {
    // readyState CLOSED means the browser will not retry — waiting for
    // five attempts that never come would leave the panel wedged.
    const h = harness([CLOSED]);
    h.fire(1);

    assert.ok(h.closed);
    assert.equal(h.renders, 2, 'the report and the close');
    assert.match(h.events[0], /stream error/);
    assert.doesNotMatch(h.events[0], /reconnecting/, 'do not promise a retry that will not happen');
});

test('the retry ceiling is a small finite number', () => {
    const { ceiling } = loadPolicy();
    assert.ok(ceiling > 0 && ceiling <= 20,
        'a ceiling that large is indistinguishable from none');
});

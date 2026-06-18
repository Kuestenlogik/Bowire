// Reproduces the morphdom-stale-closure bug that was sending the
// wrong history entry on Replay, and verifies the data-attribute
// resolver in render-main.js fixes it.
//
// We do NOT try to load the live bowire.js bundle — it's an IIFE
// wrapped around the browser globals. Instead we re-implement the
// minimum needed to demonstrate the bug + fix shape that lives in
// the production code:
//
//   addHistory / getHistory  — same shape as history-env.js (unshift,
//                              JSON round-trip through localStorage)
//   render()                 — builds a row per history entry, each with
//                              a click handler. We test two handler styles:
//                                A) closure-captured `h` (the old, buggy
//                                   shape)
//                                B) data-attribute resolved at click time
//                                   (the fix shipped in render-main.js)
//   morphdom-like patch      — on re-render we KEEP the existing DOM
//                              nodes (only updating their attributes),
//                              which is exactly what morphdom does when
//                              there are no stable ids — and exactly what
//                              causes the closure-bound handlers to
//                              fire with stale `h`.

import { test } from 'node:test';
import assert from 'node:assert/strict';

// ---------- minimum browser stubs ----------

function makeLocalStorage() {
    const store = new Map();
    return {
        getItem: k => store.has(k) ? store.get(k) : null,
        setItem: (k, v) => store.set(k, String(v)),
        removeItem: k => store.delete(k),
    };
}

class FakeNode {
    constructor(tag) {
        this.tagName = tag.toUpperCase();
        this.children = [];
        this.attrs = new Map();
        this._listeners = new Map();
    }
    setAttribute(k, v) { this.attrs.set(k, String(v)); }
    getAttribute(k) { return this.attrs.has(k) ? this.attrs.get(k) : null; }
    addEventListener(name, fn) { this._listeners.set(name, fn); }
    appendChild(child) { this.children.push(child); child.parent = this; return child; }
    dispatch(name, evt) {
        const fn = this._listeners.get(name);
        if (fn) fn({ ...evt, currentTarget: this, stopPropagation() {} });
    }
}

// ---------- production-equivalent helpers ----------

function makeHistoryApi(localStorage, key = 'h') {
    const get = () => {
        const raw = localStorage.getItem(key);
        return raw ? JSON.parse(raw) : [];
    };
    const add = entry => {
        const list = get();
        // matches history-env.js:50 — unshift so newest is index 0
        list.unshift({ ...entry, timestamp: entry.timestamp ?? Date.now() });
        localStorage.setItem(key, JSON.stringify(list));
    };
    return { get, add };
}

// Renders ONE history row into an existing or fresh node, using one of
// two click-handler shapes.
//   shape: 'closure' — the bug, h captured at row-build time
//   shape: 'data-attr' — the fix, h resolved fresh at click time
function buildHistoryRow(h, replayed, historyApi, shape, existing) {
    const node = existing ?? new FakeNode('button');
    // Always update the data attributes — this is what render() in
    // production does. morphdom updates attrs in place but keeps node
    // identity and existing listeners.
    node.setAttribute('data-replay-ts', String(h.timestamp));
    node.setAttribute('data-replay-svc', h.service);
    node.setAttribute('data-replay-mth', h.method);
    if (!existing) {
        // First render: install the click handler. On re-render we do
        // NOT replace it — that's the morphdom behaviour we're testing.
        if (shape === 'closure') {
            node.addEventListener('click', () => {
                replayed.push(h);
            });
        } else {
            node.addEventListener('click', evt => {
                const btn = evt.currentTarget;
                const ts = parseInt(btn.getAttribute('data-replay-ts'), 10);
                const svc = btn.getAttribute('data-replay-svc');
                const mth = btn.getAttribute('data-replay-mth');
                const fresh = historyApi.get().find(x =>
                    x.timestamp === ts && x.service === svc && x.method === mth);
                if (fresh) replayed.push(fresh);
            });
        }
    }
    return node;
}

function render(historyApi, container, shape, replayed) {
    const list = historyApi.get();
    for (let i = 0; i < list.length; i++) {
        const existing = container.children[i];
        const node = buildHistoryRow(list[i], replayed, historyApi, shape, existing);
        if (!existing) container.appendChild(node);
    }
}

// ---------- the tests ----------

test('addHistory persists the body field independently per call', () => {
    const ls = makeLocalStorage();
    const { add, get } = makeHistoryApi(ls);
    add({ service: 'pet', method: 'getPetById', messages: ['{"petId": 10}'], timestamp: 1000 });
    add({ service: 'pet', method: 'getPetById', messages: ['{"petId": 5}'],  timestamp: 2000 });
    const h = get();
    assert.equal(h.length, 2);
    // unshift → newest first
    assert.deepEqual(h[0].messages, ['{"petId": 5}']);
    assert.deepEqual(h[1].messages, ['{"petId": 10}']);
});

test('OLD closure-capture handler fires with the WRONG entry after re-render (the bug)', () => {
    const ls = makeLocalStorage();
    const api = makeHistoryApi(ls);
    const container = new FakeNode('div');
    const replayed = [];

    // First call lands → render — row 0 = petId 10
    api.add({ service: 'pet', method: 'getPetById', messages: ['{"petId": 10}'], timestamp: 1000 });
    render(api, container, 'closure', replayed);

    // Second call lands → re-render — row 0 = petId 5, row 1 = petId 10
    // morphdom keeps the row-0 DOM node but updates its data attributes;
    // its click listener still closes over the petId=10 `h`.
    api.add({ service: 'pet', method: 'getPetById', messages: ['{"petId": 5}'], timestamp: 2000 });
    render(api, container, 'closure', replayed);

    // User clicks the visually-newest row (now showing petId=5)
    container.children[0].dispatch('click', {});

    // The bug: handler fires with the STALE captured `h` → petId=10
    assert.equal(replayed.length, 1);
    assert.deepEqual(replayed[0].messages, ['{"petId": 10}'],
        'closure-form replays the original first-render entry, NOT the row the user clicked');
});

test('NEW data-attr resolver fires with the CORRECT entry after re-render (the fix)', () => {
    const ls = makeLocalStorage();
    const api = makeHistoryApi(ls);
    const container = new FakeNode('div');
    const replayed = [];

    api.add({ service: 'pet', method: 'getPetById', messages: ['{"petId": 10}'], timestamp: 1000 });
    render(api, container, 'data-attr', replayed);
    api.add({ service: 'pet', method: 'getPetById', messages: ['{"petId": 5}'], timestamp: 2000 });
    render(api, container, 'data-attr', replayed);

    // Same click — visually-newest row
    container.children[0].dispatch('click', {});

    assert.equal(replayed.length, 1);
    assert.deepEqual(replayed[0].messages, ['{"petId": 5}'],
        'data-attr resolver picks up the freshly-updated attributes and looks the entry up at click time');

    // And clicking the lower (older) row replays petId=10
    container.children[1].dispatch('click', {});
    assert.equal(replayed.length, 2);
    assert.deepEqual(replayed[1].messages, ['{"petId": 10}']);
});

test('the shipped render-main.js uses the data-attr shape, not the closure shape', async () => {
    const fs = await import('node:fs/promises');
    const path = await import('node:path');
    const url = await import('node:url');
    const here = path.dirname(url.fileURLToPath(import.meta.url));
    const file = path.resolve(here, '../../../src/Kuestenlogik.Bowire/wwwroot/js/render-main.js');
    const src = await fs.readFile(file, 'utf8');

    // The fix surface: replay button writes the (ts, svc, mth) triple
    // onto the DOM and resolves at click time via getHistory().find.
    assert.ok(src.includes("'data-replay-ts'"),
        'render-main.js must stamp data-replay-ts onto the replay button');
    assert.ok(src.includes('getHistory().find('),
        'render-main.js must resolve the entry via getHistory().find at click time');

    // Regression guard: the old closure form must not come back.
    const replayBtnIdx = src.indexOf("className: 'bowire-history-replay'");
    assert.ok(replayBtnIdx > 0, 'replay button block must exist');
    const replayBtnBlock = src.slice(replayBtnIdx, replayBtnIdx + 1500);
    assert.ok(!/replayHistoryEntry\(h\)\s*;/.test(replayBtnBlock),
        'replay button must NOT call replayHistoryEntry(h) directly — that is the stale-closure form');
});

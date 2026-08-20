// #587 — the Rollup rail's rendering and its path-input contract.
//
// The rail reads POST /api/report/rollup and renders one row per service.
// Two things are worth pinning here rather than in a browser: that a missing
// report renders as an em dash and never as 0 (the whole point of the
// nullable counts — a service nobody linted must not read as clean), and
// that typing in the path box updates the state the fetch reads. The second
// one matters because the input's `value` comes from state on every render:
// if the keystroke didn't reach state, the next render would silently revert
// what the operator typed.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

// Minimal DOM: el() records what was built so assertions can walk it, and
// nodes carry the handlers the fragment attached.
const _prelude = `
    function el(tag, attrs, ...children) {
        const node = {
            tag,
            attrs: attrs || {},
            children: [],
            text: '',
        };
        const push = (c) => {
            if (c === null || c === undefined) return;
            if (Array.isArray(c)) { c.forEach(push); return; }
            // Text lands as a child only — keeping it on the parent too would
            // make textOf() count it twice.
            if (typeof c === 'string') { node.children.push({ tag: '#text', text: c, children: [], attrs: {} }); return; }
            node.children.push(c);
        };
        children.forEach(push);
        return node;
    }
    var _renders = 0;
    function render() { _renders++; }
    var _fetches = [];
    var _fetchResult = null;
    async function fetch(url, init) {
        _fetches.push({ url, init });
        return _fetchResult;
    }
`;
const _postlude = `
    return {
        renderRollupMain: renderRollupMain,
        setRollup: function (r) { bowireRollup = r; },
        setResult: function (r) { _fetchResult = r; },
        loadRollup: function () { return bowireLoadRollup(); },
        path: function () { return bowireRollupPath; },
        fetches: function () { return _fetches; },
        renders: function () { return _renders; },
    };
`;
// `window` arrives as a parameter rather than from the appended block: the
// loader appends everything AFTER the fragment, and rollup.js registers its
// renderer on `window` at top level — a `var window` in the appended block
// would still be undefined at that point, because declarations hoist but
// assignments do not.
const _load = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/rollup.js',
    ['window'],
    _prelude + '\n' + _postlude
);

function load() {
    return _load({ window: { __BOWIRE_CONFIG__: { prefix: '' } } });
}

// Walk the built tree collecting nodes with a given class.
function byClass(node, className, found = []) {
    const cls = node.attrs?.class || '';
    if (typeof cls === 'string' && cls.split(' ').includes(className)) found.push(node);
    (node.children || []).forEach(c => byClass(c, className, found));
    return found;
}

function textOf(node) {
    let out = node.text || '';
    (node.children || []).forEach(c => { out += ' ' + textOf(c); });
    return out.replace(/\s+/g, ' ').trim();
}

const SAMPLE = {
    services: [
        {
            service: 'orders-api', worst: 'medium',
            lint: { high: 0, medium: 1, low: 1, info: 0 },
            contracts: { passed: 1, total: 1 },
            tests: { passed: 42, total: 42 },
            benchmark: null, scanErrors: null, lastReportAt: '2026-08-19T22:00:00Z',
            sources: [{ kind: 'lint', path: 'reports/orders-api/lint.json' }],
        },
        {
            service: 'gateway', worst: 'high',
            lint: null, contracts: null, tests: null, benchmark: null,
            scanErrors: 1, lastReportAt: null, sources: [],
        },
    ],
    summary: { services: 2, atHigh: 1, clean: 0, skipped: 1 },
    skipped: [{ path: 'x.json', error: 'not recognised' }],
};

test('a missing report renders as an em dash, never as zero', () => {
    const rail = load();
    rail.setRollup(SAMPLE);
    const tree = rail.renderRollupMain();

    const rows = byClass(tree, 'bowire-rollup-row');
    assert.equal(rows.length, 2);

    // gateway has no lint, contract, test or benchmark report at all.
    const gateway = textOf(rows.find(r => textOf(r).startsWith('gateway')));
    assert.match(gateway, /—/);
    assert.doesNotMatch(gateway, /\b0\/0\b/);

    // orders-api genuinely ran them, so it shows counts.
    const orders = textOf(rows.find(r => textOf(r).startsWith('orders-api')));
    assert.match(orders, /1\/1/);
    assert.match(orders, /42\/42/);
});

test('the worst-severity cell carries a text label, not only a colour', () => {
    const rail = load();
    rail.setRollup(SAMPLE);
    const tree = rail.renderRollupMain();

    const verdicts = byClass(tree, 'bowire-rollup-worst').map(textOf);
    assert.deepEqual(verdicts.sort(), ['HIGH', 'MEDIUM']);
});

test('a clean service reads "OK" rather than an empty cell', () => {
    const rail = load();
    rail.setRollup({
        services: [{ service: 'quiet', worst: null, lint: null, contracts: null, tests: null, benchmark: null, scanErrors: null, lastReportAt: null, sources: [] }],
        summary: { services: 1, atHigh: 0, clean: 1, skipped: 0 },
        skipped: [],
    });
    const tree = rail.renderRollupMain();

    assert.equal(byClass(tree, 'bowire-rollup-worst').map(textOf)[0], 'OK');
});

test('the summary line reports services, highs and skipped files', () => {
    const rail = load();
    rail.setRollup(SAMPLE);
    const summary = textOf(byClass(rail.renderRollupMain(), 'bowire-rollup-summary')[0]);

    assert.match(summary, /2 service/);
    assert.match(summary, /1 at high/);
    assert.match(summary, /1 file\(s\) skipped/);
});

test('an empty result says so instead of showing a bare table', () => {
    const rail = load();
    rail.setRollup({ services: [], summary: { services: 0, atHigh: 0, clean: 0, skipped: 2 }, skipped: [] });
    const tree = rail.renderRollupMain();

    assert.equal(byClass(tree, 'bowire-rollup-row').length, 0);
    assert.match(textOf(byClass(tree, 'bowire-rollup-empty')[0]), /No Bowire reports found/);
});

test('typing in the path box updates the state the fetch reads', async () => {
    // The input's value is rendered from state, so a keystroke that never
    // reached state would be reverted by the next render.
    const rail = load();
    const input = byClass(rail.renderRollupMain(), 'bowire-rollup-path')[0];
    assert.equal(input.attrs.value, '.bowire');

    input.attrs.oninput.call({ value: 'reports/, other/' });
    assert.equal(rail.path(), 'reports/, other/');

    // …and the fetch splits it into the paths the API expects, dropping the
    // blank a trailing comma leaves behind.
    rail.setResult({ ok: true, json: async () => ({ services: [], summary: {}, skipped: [] }) });
    await rail.loadRollup();

    const body = JSON.parse(rail.fetches()[0].init.body);
    assert.deepEqual(body.from, ['reports/', 'other/']);
});

test('a failed request surfaces the status instead of an empty pane', async () => {
    const rail = load();
    rail.setResult({ ok: false, status: 500 });
    await rail.loadRollup();

    const tree = rail.renderRollupMain();
    assert.match(textOf(byClass(tree, 'bowire-rollup-error')[0]), /500/);
});

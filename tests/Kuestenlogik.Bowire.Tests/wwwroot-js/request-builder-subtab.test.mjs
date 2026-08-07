// #349 — regression: the request-builder tab-body id must be keyed by
// BOTH the mount (Compose tab id) and the active sub-tab, so morphdom
// discards+rebuilds the KV-table — rebinding its `onInput` closures —
// on a Parameter -> Header flip, and never salvages a stale body across
// two open Compose tabs that happen to share a sub-tab.
//
// The shared request-builder.test.mjs harness stubs el() as a no-op that
// drops attributes, so it can't observe the id. This file loads the same
// SRC with a recording el() + minimal DOM/svg stubs and asserts the id
// construction contract directly.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

// Host stubs live in the appended block (hoisted); the fragment is
// compiled alone with its real filename so V8 attributes coverage to
// request-builder.js (#367). `document` is read at the fragment's top
// level, so it arrives as a parameter (defined before the body runs).
const _prelude = `
        var _ls = {};
        var localStorage = {
            getItem: function (k) { return Object.prototype.hasOwnProperty.call(_ls, k) ? _ls[k] : null; },
            setItem: function (k, v) { _ls[k] = String(v); },
            removeItem: function (k) { delete _ls[k]; }
        };
        function wsKey(k) { return 'ws::' + k; }
        // Recording el: captures id/className so the test can inspect the
        // built node tree. Ignores event handlers (onX) — not needed here.
        function el(tag, attrs) {
            var node = {
                tagName: String(tag || 'div').toUpperCase(),
                id: (attrs && attrs.id) || '',
                className: (attrs && attrs.className) || '',
                dataset: {},
                children: [],
                appendChild: function (c) { if (c) this.children.push(c); return c; },
                addEventListener: function () {}
            };
            if (attrs) {
                Object.keys(attrs).forEach(function (k) {
                    if (k.indexOf('data-') === 0) node.dataset[k.slice(5)] = attrs[k];
                });
            }
            for (var i = 2; i < arguments.length; i++) {
                if (arguments[i]) node.children.push(arguments[i]);
            }
            return node;
        }
        function svgIcon() { return ''; }
        function render() {}
        function toast() {}
        function markSaved() {}
        function recordAction() {}
        function substituteVars(s) { return s; }
        function substituteMetadata(m) { return m; }
        function executeRequest() {}
        function activeWorkspace() { return null; }
        var workspaceId = 'ws1';
        var freeformRequest = {};
        var responseLastJson = null;
        function persistHoppHistory() {}
`;
const _postlude = `
    return {
        ensureHoppState: ensureHoppState,
        _newHoppState: _newHoppState,
        _renderHoppTabBody: _renderHoppTabBody
    };
`;
const _load = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/request-builder.js',
    ['document'],
    _prelude + '\n' + _postlude
);

function loadWithRecordingEl() {
    const document = {
        addEventListener() {},
        removeEventListener() {},
        getElementById() { return null; },
        querySelector() { return null; },
        querySelectorAll() { return []; },
        createElement() { return { appendChild() {} }; },
        body: { appendChild() {}, removeChild() {} },
        activeElement: null,
    };
    return _load({ document });
}

function freshRest(activeTab) {
    const rb = { protocol: 'rest', byProtocol: {}, params: [], headers: [], activeTab };
    return { _requestBuilder: rb, method: 'GET' };
}

test('#349 tab-body id encodes mount + active sub-tab', () => {
    const sb = loadWithRecordingEl();

    const frParam = freshRest('parameter');
    sb.ensureHoppState(frParam);
    const paramBody = sb._renderHoppTabBody(frParam, 'design_3');
    assert.equal(paramBody.id, 'bowire-request-builder-tab-body-design_3-parameter');

    const frHeader = freshRest('header');
    sb.ensureHoppState(frHeader);
    const headerBody = sb._renderHoppTabBody(frHeader, 'design_3');
    assert.equal(headerBody.id, 'bowire-request-builder-tab-body-design_3-header');

    // Parameter vs Header within one tab MUST differ so morphdom rebuilds.
    assert.notEqual(paramBody.id, headerBody.id);
});

test('#349 tab-body id differs across Compose tabs on the same sub-tab', () => {
    const sb = loadWithRecordingEl();

    const frA = freshRest('parameter');
    sb.ensureHoppState(frA);
    const bodyA = sb._renderHoppTabBody(frA, 'design_1');

    const frB = freshRest('parameter');
    sb.ensureHoppState(frB);
    const bodyB = sb._renderHoppTabBody(frB, 'design_2');

    // Same sub-tab, different mount → distinct ids so morphdom's keyed
    // salvage can't recycle one tab's stale body into the other.
    assert.equal(bodyA.id, 'bowire-request-builder-tab-body-design_1-parameter');
    assert.equal(bodyB.id, 'bowire-request-builder-tab-body-design_2-parameter');
    assert.notEqual(bodyA.id, bodyB.id);
});

test('#349 tab-body id falls back to a solo mount segment when unmounted', () => {
    const sb = loadWithRecordingEl();
    const fr = freshRest('parameter');
    sb.ensureHoppState(fr);
    const body = sb._renderHoppTabBody(fr);
    assert.equal(body.id, 'bowire-request-builder-tab-body-solo-parameter');
});

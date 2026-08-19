// Rail overflow measurement — `_railOuterHeight`.
//
// The activity rail decides what to push into the '…' overflow by adding
// up its children's heights. getBoundingClientRect() reports the BORDER
// box only, so the group dividers' vertical margin (6px top + 6px bottom
// in bowire.css) was invisible to the sum: with several groups the rail
// believed everything fitted while the bottom-anchored Settings button
// was already clipped by the status bar. Operator feedback: 'der umbruch
// bei den rails kommt etwas zu spät bzw. der einstellungs button ist
// angeschnitten von der unteren leiste.'
//
// _railOuterHeight() adds the vertical margins back. Same standalone
// fragment wrap as rail-attention.test.mjs.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

// The fragment only needs enough host surface to compile; the function
// under test reaches for getComputedStyle, which we script per node.
const _prelude = `
    var window = {
        __BOWIRE_CONFIG__: { rails: [] },
        getComputedStyle: function (node) { return node.__style || {}; }
    };
    function el() { return { appendChild: function () {} }; }
    function svgIcon() { return ''; }
    var uiMode = 'standalone';
    function schemaChangeUnreadCount() { return 0; }
    function ensureSchemaChangeLogLoaded() {}
`;
const _postlude = `
    return { _railOuterHeight: _railOuterHeight };
`;
const _load = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/render-sidebar.js',
    [],
    _prelude + '\n' + _postlude
);

function node(height, marginTop, marginBottom) {
    return {
        getBoundingClientRect: function () { return { height: height }; },
        __style: { marginTop: marginTop, marginBottom: marginBottom },
    };
}

test('adds vertical margins to the border-box height', () => {
    const { _railOuterHeight } = _load({});
    // A rail divider as bowire.css styles it: 1px tall, margin 6px 8px.
    // The old measurement saw 1; the real column cost is 13.
    assert.equal(_railOuterHeight(node(1, '6px', '6px'), 1), 13);
});

test('a zero-margin button measures as its plain height', () => {
    const { _railOuterHeight } = _load({});
    // Rail buttons have no vertical margin (gap: 0; padding: 0), so the
    // fix must not inflate them.
    assert.equal(_railOuterHeight(node(44, '0px', '0px'), 44), 44);
});

test('missing or unparseable margins fall back to zero, never NaN', () => {
    const { _railOuterHeight } = _load({});
    // A NaN would poison the running total and disable overflow entirely.
    assert.equal(_railOuterHeight(node(44, undefined, ''), 44), 44);
    assert.equal(_railOuterHeight(node(44, 'auto', 'auto'), 44), 44);
});

test('a zero-height node falls back to the caller default', () => {
    const { _railOuterHeight } = _load({});
    // Measuring before layout yields 0; the caller's default keeps the
    // budget arithmetic honest instead of counting the child as free.
    assert.equal(_railOuterHeight(node(0, '0px', '0px'), 44), 44);
});

test('a null node yields the fallback rather than throwing', () => {
    const { _railOuterHeight } = _load({});
    assert.equal(_railOuterHeight(null, 44), 44);
    assert.equal(_railOuterHeight(undefined, 0), 0);
});

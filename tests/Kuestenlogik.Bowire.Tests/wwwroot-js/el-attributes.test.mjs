// #686 — el() and boolean attributes.
//
// An HTML boolean attribute is set by PRESENCE: `disabled="false"` disables
// the element just as surely as `disabled=""`. el() routed every unknown key
// through setAttribute, so `disabled: someCondition` produced the inverse of
// what the call site said whenever the condition was false.
//
// The fix is an allow-list, not a blanket "skip false", because enumerated
// attributes — spellcheck, draggable, contenteditable, translate, aria-* —
// genuinely want the literal string "false". These tests pin both halves.
//
// There is no DOM under `node --test`, so the stub below records what el()
// would have done. That is exactly the layer the bug lived in.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/helpers.js'),
    'utf8'
);

/** The BOOLEAN_ATTRS set, verbatim — up to and including its closing `]);`. */
function extractBooleanAttrs() {
    const i = SRC.indexOf('const BOOLEAN_ATTRS');
    assert.ok(i >= 0, 'BOOLEAN_ATTRS not found in helpers.js');
    const end = SRC.indexOf(']);', i);
    assert.ok(end > i, 'BOOLEAN_ATTRS is not closed the way this test expects');
    return SRC.slice(i, end + 3);
}

/** The el() function, by matching braces from its signature. */
function extractEl() {
    const start = SRC.indexOf('function el(tag, attrs, ...children)');
    assert.ok(start >= 0, 'el() not found in helpers.js');
    const open = SRC.indexOf('{', start);
    let depth = 0, i = open;
    for (; i < SRC.length; i++) {
        if (SRC[i] === '{') depth++;
        else if (SRC[i] === '}') { depth--; if (depth === 0) { i++; break; } }
    }
    return SRC.slice(start, i);
}

function makeEl() {
    const document = {
        createElement(tag) {
            return {
                tagName: tag.toUpperCase(),
                attrs: {},
                dataset: {},
                listeners: {},
                kids: [],
                setAttribute(k, v) { this.attrs[k] = String(v); },
                addEventListener(type, fn) { this.listeners[type] = fn; },
                appendChild(c) { this.kids.push(c); return c; },
            };
        },
        createTextNode(t) { return { text: t }; },
    };
    const body = [extractBooleanAttrs(), extractEl(), 'return el;'].join('\n');
    // eslint-disable-next-line no-new-func
    return new Function('document', body)(document);
}

const el = makeEl();

// ---- boolean attributes ----

test('disabled: false leaves the control enabled', () => {
    // The whole point of #686: setAttribute('disabled', false) disables.
    assert.equal('disabled' in el('button', { disabled: false }).attrs, false);
});

test('disabled: true still disables', () => {
    assert.ok('disabled' in el('button', { disabled: true }).attrs);
});

test('the legacy string idiom keeps working', () => {
    // presets.js:510 uses `disabled: allowed ? null : 'disabled'`.
    assert.equal(el('button', { disabled: 'disabled' }).attrs.disabled, 'disabled');
    assert.equal('disabled' in el('button', { disabled: null }).attrs, false);
    assert.equal('disabled' in el('button', { disabled: undefined }).attrs, false);
});

test('every boolean attribute the workbench uses obeys the same rule', () => {
    for (const name of ['checked', 'selected', 'readonly', 'required',
                        'multiple', 'hidden', 'open', 'autofocus']) {
        assert.equal(name in el('input', { [name]: false }).attrs, false,
            `${name}: false must not be set`);
        assert.ok(name in el('input', { [name]: true }).attrs,
            `${name}: true must be set`);
    }
});

test('the check is case-insensitive, so readOnly works like readonly', () => {
    assert.equal('readOnly' in el('input', { readOnly: false }).attrs, false);
});

// ---- enumerated attributes must keep their "false" ----

test('spellcheck: false still reaches the DOM as "false"', () => {
    // Eight call sites rely on this to keep red squiggles out of JSON editors
    // and token fields. Dropping the attribute would turn them back on.
    assert.equal(el('textarea', { spellcheck: false }).attrs.spellcheck, 'false');
});

test('draggable, contenteditable and translate keep their "false" too', () => {
    for (const name of ['draggable', 'contenteditable', 'translate']) {
        assert.equal(el('div', { [name]: false }).attrs[name], 'false',
            `${name} is enumerated, not boolean`);
    }
});

test('aria-* attributes keep their "false"', () => {
    // aria-pressed="false" means "a toggle that is off". Omitting it means
    // "not a toggle at all", which is a different thing to a screen reader.
    const node = el('button', { 'aria-pressed': false, 'aria-expanded': false });
    assert.equal(node.attrs['aria-pressed'], 'false');
    assert.equal(node.attrs['aria-expanded'], 'false');
});

// ---- the untouched paths ----

test('className, textContent, dataset and on* handlers are unaffected', () => {
    const seen = [];
    const node = el('div', {
        className: 'x',
        textContent: 'hello',
        dataset: { setId: 'a1' },
        onClick: () => seen.push('clicked'),
        title: 'tip',
    });
    assert.equal(node.className, 'x');
    assert.equal(node.textContent, 'hello');
    assert.equal(node.dataset.setId, 'a1');
    assert.equal(node.attrs.title, 'tip');
    node.listeners.click();
    assert.deepEqual(seen, ['clicked']);
});

test('a zero or an empty string is still set — only false is special', () => {
    assert.equal(el('input', { size: 0 }).attrs.size, '0');
    assert.equal(el('input', { value: '' }).attrs.value, '');
});

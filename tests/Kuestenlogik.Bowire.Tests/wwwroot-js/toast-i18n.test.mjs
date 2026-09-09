// #117 — the toast reads its own labels from the catalogue.
//
// The regression this pins: toast() built its element into a local called `t`,
// and the i18n sweep put t('common.undo') and t('common.dismiss') inside that
// same function. From the declaration onwards `t` was a DOM node, so every
// toast threw "t is not a function" — including the plain ones, because the
// close button's aria-label is unconditional.
//
// Nothing else caught it. The bundle parses, the analysers are happy, and no
// test had ever rendered a toast. locales.test.mjs now refuses any fragment
// that both binds the name `t` and calls it; this test comes at the same bug
// from the other side, by actually running the function.
//
// There is no DOM under `node --test`, so the stubs below record what the real
// el() would have built. That is the layer the bug lived in.

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

/** A named function from helpers.js, by matching braces from its signature. */
function extract(signature) {
    const start = SRC.indexOf(signature);
    assert.ok(start >= 0, `${signature} not found in helpers.js`);
    const open = SRC.indexOf('{', start);
    let depth = 0, i = open;
    for (; i < SRC.length; i++) {
        if (SRC[i] === '{') depth++;
        else if (SRC[i] === '}') { depth--; if (depth === 0) { i++; break; } }
    }
    return SRC.slice(start, i);
}

function makeNode(tag) {
    return {
        tag,
        attrs: {},
        children: [],
        classList: { add() {} },
        appendChild(child) { this.children.push(child); return child; },
        remove() {},
    };
}

/**
 * Runs toast() against stubs, and reports both what it built and which
 * catalogue keys it asked for.
 */
function runToast(message, type, options) {
    const asked = [];
    const t = (key) => { asked.push(key); return `«${key}»`; };

    const el = (tag, attrs, ...children) => {
        const node = makeNode(tag);
        Object.assign(node.attrs, attrs || {});
        for (const child of children.flat()) if (child) node.children.push(child);
        return node;
    };

    const body = makeNode('body');
    const scope = {
        el,
        t,
        $: () => null,                       // no container yet — toast() makes one
        document: { body },
        svgIcon: () => '<svg/>',
        setTimeout: () => 0,
        clearTimeout: () => {},
        recordAction: undefined,
    };

    const factory = new Function(
        ...Object.keys(scope),
        `${extract('function toast(message, type, options)')}
         ${extract('function dismissToast(')}
         return toast;`
    );
    const node = factory(...Object.values(scope))(message, type, options);
    return { node, asked };
}

/** Every string the toast put on screen, in order. */
function labels(node, out = []) {
    if (node.attrs && typeof node.attrs.textContent === 'string') out.push(node.attrs.textContent);
    for (const child of node.children || []) labels(child, out);
    return out;
}

function ariaLabels(node, out = []) {
    if (node.attrs && node.attrs['aria-label']) out.push(node.attrs['aria-label']);
    for (const child of node.children || []) ariaLabels(child, out);
    return out;
}

test('a plain toast renders without throwing', () => {
    // The bug's real shape: not the Undo button, which is optional, but the
    // close button every toast carries.
    const { node } = runToast('Saved', 'success', {});
    assert.equal(node.tag, 'div');
    assert.match(node.attrs.className, /bowire-toast success/);
});

test('the close button takes its aria-label from the catalogue', () => {
    const { node, asked } = runToast('Saved', 'success', {});
    assert.ok(asked.includes('common.dismiss'), `keys asked for: ${asked.join(', ')}`);
    assert.ok(ariaLabels(node).includes('«common.dismiss»'));
});

test('the Undo button takes its label from the catalogue', () => {
    const { node, asked } = runToast('Deleted', 'info', { undo: () => {} });
    assert.ok(asked.includes('common.undo'), `keys asked for: ${asked.join(', ')}`);
    assert.ok(labels(node).includes('«common.undo»'));
});

test('the message itself is passed through, not translated', () => {
    // Callers hand toast() a finished sentence — often one built from their
    // own catalogue keys. Translating it again here would be wrong twice.
    const { node, asked } = runToast('Loaded “staging p95”', 'success', {});
    assert.ok(labels(node).includes('Loaded “staging p95”'));
    assert.ok(!asked.some((k) => k.startsWith('toast.')), 'the message must not be looked up');
});

test('a caller-supplied action label wins over any catalogue text', () => {
    const { node } = runToast('Invocation enabled', 'info', {
        action: { label: 'Open Settings', onClick: () => {} },
    });
    assert.ok(labels(node).includes('Open Settings'));
});

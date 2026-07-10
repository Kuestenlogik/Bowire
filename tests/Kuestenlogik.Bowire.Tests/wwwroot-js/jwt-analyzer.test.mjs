// #105 — tests for the JWT-analyzer render surface in ai.js. renderJwtResult
// projects the /api/ai/jwt-analyze response (alg + flags + AI narrative) into a
// container; it closes over the global `el`, so we snip it out and run it
// against an injected fake element factory.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/ai.js'),
    'utf8'
);

function extractFn(name, keyword = 'function') {
    const needle = `${keyword} ${name}(`;
    const start = SRC.indexOf(needle);
    assert.ok(start >= 0, `${needle} not found`);
    const open = SRC.indexOf('{', start);
    let depth = 0, i = open, prevSig = '';
    for (; i < SRC.length; i++) {
        const c = SRC[i], n = SRC[i + 1];
        if (c === '/' && n === '/') { i = SRC.indexOf('\n', i); if (i < 0) i = SRC.length; continue; }
        if (c === '/' && n === '*') { i = SRC.indexOf('*/', i + 2) + 1; continue; }
        if (c === '"' || c === "'" || c === '`') { i = skipString(SRC, i, c); prevSig = c; continue; }
        if (c === '/' && '=(,:[!&|?{;'.includes(prevSig)) { i = skipRegex(SRC, i); prevSig = '/'; continue; }
        if (c === '{') depth++;
        else if (c === '}') { depth--; if (depth === 0) { i++; break; } }
        if (!/\s/.test(c)) prevSig = c;
    }
    return SRC.slice(start, i);
}
function skipString(s, i, q) {
    for (i++; i < s.length; i++) { if (s[i] === '\\') { i++; continue; } if (s[i] === q) return i; }
    return s.length;
}
function skipRegex(s, i) {
    let inClass = false;
    for (i++; i < s.length; i++) {
        if (s[i] === '\\') { i++; continue; }
        if (s[i] === '[') inClass = true;
        else if (s[i] === ']') inClass = false;
        else if (s[i] === '/' && !inClass) return i;
    }
    return s.length;
}

// Minimal element factory matching the workbench's `el(tag, props, ...children)`.
function fakeEl() {
    return function el(tag, props, ...children) {
        const node = {
            tag,
            className: props && props.className,
            textContent: (props && props.textContent) || '',
            children: [],
            appendChild(c) { this.children.push(c); return c; },
            replaceChildren() { this.children = []; },
        };
        children.forEach((c) => node.children.push(c));
        return node;
    };
}

function loadRenderJwtResult() {
    const body = `${extractFn('renderJwtResult')}\nreturn renderJwtResult;`;
    return new Function('el', body);
}

function texts(node, acc = []) {
    if (node.textContent) acc.push(node.textContent);
    (node.children || []).forEach((c) => texts(c, acc));
    return acc;
}

test('renderJwtResult renders alg, flags, and AI narrative', () => {
    const el = fakeEl();
    const renderJwtResult = loadRenderJwtResult()(el);
    const container = el('div');
    renderJwtResult(container, {
        algorithm: 'none',
        keyId: 'k1',
        flags: [{ level: 'HIGH', claim: 'alg', message: 'unsigned token' }],
        aiNarrative: 'Reject this token.',
    });

    const t = texts(container);
    assert.ok(t.some((x) => x === 'alg: none · kid: k1'), 'alg line');
    assert.ok(t.some((x) => x === 'HIGH'), 'level');
    assert.ok(t.some((x) => x === 'alg'), 'claim');
    assert.ok(t.some((x) => x === 'unsigned token'), 'message');
    assert.ok(t.some((x) => x === 'Reject this token.'), 'AI narrative');
});

test('renderJwtResult without AI narrative omits it', () => {
    const el = fakeEl();
    const renderJwtResult = loadRenderJwtResult()(el);
    const container = el('div');
    renderJwtResult(container, { algorithm: 'RS256', flags: [{ level: 'LOW', claim: 'nbf', message: 'no nbf' }] });

    const t = texts(container);
    assert.ok(t.some((x) => x === 'no nbf'));
    assert.ok(!t.some((x) => /Reject|narrative/i.test(x)), 'no narrative node');
});

test('renderJwtResult clears the container before rendering', () => {
    const el = fakeEl();
    const renderJwtResult = loadRenderJwtResult()(el);
    const container = el('div');
    container.appendChild(el('div', { textContent: 'STALE' }));
    renderJwtResult(container, { flags: [] });
    assert.ok(!texts(container).some((x) => x === 'STALE'), 'stale content cleared');
});

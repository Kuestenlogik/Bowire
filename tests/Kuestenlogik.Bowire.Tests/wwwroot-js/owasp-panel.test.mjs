// #106 — tests for the OWASP API Top 10 per-method panel render surface in
// ai.js. renderOwaspPanelRows projects the /api/ai/owasp-panel response (rows +
// AI review) into a container; snipped out and run against an injected fake el.

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
function load() {
    return new Function('el', `${extractFn('renderOwaspPanelRows')}\nreturn renderOwaspPanelRows;`);
}
function texts(node, acc = []) {
    if (node.textContent) acc.push(node.textContent);
    (node.children || []).forEach((c) => texts(c, acc));
    return acc;
}
function classes(node, acc = []) {
    if (node.className) acc.push(node.className);
    (node.children || []).forEach((c) => classes(c, acc));
    return acc;
}

test('renderOwaspPanelRows renders every row with status + probe', () => {
    const el = fakeEl();
    const render = load()(el);
    const container = el('div');
    render(container, {
        rows: [
            { entry: 'API1:2023', title: 'BOLA', status: 'AtRisk', rationale: 'id in path', suggestedProbe: 'BOLA probe' },
            { entry: 'API6:2023', title: 'Business flows', status: 'NotApplicable', rationale: 'n-a', suggestedProbe: null },
        ],
        aiReview: 'Focus on the BOLA.',
    });

    const t = texts(container);
    assert.ok(t.includes('API1:2023'));
    assert.ok(t.includes('AtRisk'));
    assert.ok(t.includes('BOLA probe'));
    assert.ok(t.includes('Focus on the BOLA.'), 'AI review');
    // status drives a per-row class
    assert.ok(classes(container).some((c) => c.includes('bowire-owasp-atrisk')));
});

test('renderOwaspPanelRows omits AI review when absent', () => {
    const el = fakeEl();
    const render = load()(el);
    const container = el('div');
    render(container, { rows: [{ entry: 'API4:2023', title: 'Resource', status: 'Maybe', suggestedProbe: 'rate-limit' }] });
    assert.ok(!classes(container).some((c) => c === 'bowire-ai-owasp-review'));
});

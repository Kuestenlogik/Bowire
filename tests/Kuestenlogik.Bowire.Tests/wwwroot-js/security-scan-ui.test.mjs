// #104 — tests for the AI security-scan render surface in ai.js.
// renderScanResult projects the /api/ai/security-scan response (summary +
// triaged findings + report markdown) into a container.

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
    return new Function('el', `${extractFn('renderScanResult')}\nreturn renderScanResult;`);
}
function texts(node, acc = []) {
    if (node.textContent) acc.push(node.textContent);
    (node.children || []).forEach((c) => texts(c, acc));
    return acc;
}

test('renderScanResult renders summary, findings, and report', () => {
    const el = fakeEl();
    const render = load()(el);
    const container = el('div');
    render(container, {
        ranked: [{}, {}, {}],
        probed: ['e1'],
        findings: [{ endpointId: 'e1', ruleId: 'R1', title: 'BOLA on e1', severity: 'high', realScore: 80 }],
        suppressedCount: 2,
        reportMarkdown: '# AI security scan\n\nFindings: 1 high.',
    });

    const t = texts(container);
    assert.ok(t.some((x) => x === 'Ranked 3 · probed 1 · kept 1 finding(s) · suppressed 2'), 'summary');
    assert.ok(t.some((x) => x === '[high] BOLA on e1'), 'finding title');
    assert.ok(t.some((x) => x.includes('real 80%')), 'finding meta');
    assert.ok(t.some((x) => x.includes('# AI security scan')), 'report markdown');
});

test('renderScanResult with no findings still shows the summary', () => {
    const el = fakeEl();
    const render = load()(el);
    const container = el('div');
    render(container, { ranked: [{}], probed: [], findings: [], suppressedCount: 0 });
    assert.ok(texts(container).some((x) => x.includes('kept 0 finding(s)')));
});

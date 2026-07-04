// #208 Phase 5 — tests for the AI re-roll seam in ai.js. rerollAiVar
// drops one cached ai.* value + its inflight promise and re-runs the
// prefetch so the resolver UI's re-roll button cycles to a fresh value.
// The function closes over _aiVarsCache / _aiVarsInflight / _runAiPrefetch,
// so we snip it out and run it against injected stand-ins.

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

// String / regex / comment-aware brace matcher (ai.js function bodies can
// carry regex + template strings that a naive counter would trip over).
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

function makeHarness(runAiPrefetch) {
    const body = `
        var _aiVarsCache = {};
        var _aiVarsInflight = {};
        var _runAiPrefetch = runAiPrefetch;
        ${extractFn('rerollAiVar', 'async function')}
        return {
            rerollAiVar: rerollAiVar,
            cache: _aiVarsCache,
            inflight: _aiVarsInflight,
        };
    `;
    return new Function('runAiPrefetch', body)(runAiPrefetch);
}

test('reroll replaces the cached value with the freshly prefetched one', async () => {
    let calls = 0;
    const h = makeHarness((name) => { calls++; return Promise.resolve('fresh-' + calls); });
    h.cache['pet'] = 'stale';

    const v = await h.rerollAiVar('pet');

    assert.equal(v, 'fresh-1');
    assert.equal(h.cache['pet'], 'fresh-1');
    assert.equal(calls, 1);
});

test('reroll returns null and leaves no cache entry when prefetch yields nothing', async () => {
    const h = makeHarness(() => Promise.resolve(null));
    h.cache['pet'] = 'stale';

    const v = await h.rerollAiVar('pet');

    assert.equal(v, null);
    assert.equal(Object.prototype.hasOwnProperty.call(h.cache, 'pet'), false);
});

test('reroll empty-string result is treated as no value', async () => {
    const h = makeHarness(() => Promise.resolve(''));
    const v = await h.rerollAiVar('addr');
    assert.equal(v, null);
});

test('reroll clears the inflight slot after completion', async () => {
    const h = makeHarness(() => Promise.resolve('ok'));
    await h.rerollAiVar('x');
    assert.equal(Object.prototype.hasOwnProperty.call(h.inflight, 'x'), false);
});

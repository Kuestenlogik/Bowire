// #208 Phase 5 — per-frame variable resolution for streaming sends.
// channelSend / channelConnect (protocols.js) are too dependency-coupled
// to extract and run in isolation, so these tests pin the ordering
// invariant that makes async vars resolve per frame: the ai.* / keyring.*
// prefetch MUST run before the substituteVars() call that reads their
// caches. A regression that moved substitution above the prefetch would
// silently make {{ai.…}} / {{keyring.…}} resolve stale (or empty) on
// every streaming frame — exactly the bug this phase closed.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/protocols.js'),
    'utf8'
);

// String / regex / comment-aware brace matcher so the function body ends
// at the right brace even when it carries strings or regexes.
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

test('channelSend prefetches ai + keyring before substituteVars', () => {
    const body = extractFn('channelSend', 'async function');
    const ai = body.indexOf('bowirePrefetchAiVars');
    const keyring = body.indexOf('bowirePrefetchKeyringVars');
    const sub = body.indexOf('substituteVars(message)');
    assert.ok(ai >= 0, 'channelSend must prefetch ai vars');
    assert.ok(keyring >= 0, 'channelSend must prefetch keyring vars');
    assert.ok(sub >= 0, 'channelSend must substitute the message');
    assert.ok(ai < sub, 'ai prefetch must precede substitution');
    assert.ok(keyring < sub, 'keyring prefetch must precede substitution');
    // Both prefetches are awaited so the caches are warm before substitution.
    assert.match(body, /await window\.bowirePrefetchAiVars\(\[message\]\)/);
    assert.match(body, /await window\.bowirePrefetchKeyringVars\(\[message\]\)/);
});

test('channelConnect prefetches metadata async vars before substituting them', () => {
    const body = extractFn('channelConnect', 'async function');
    const ai = body.indexOf('bowirePrefetchAiVars');
    const keyring = body.indexOf('bowirePrefetchKeyringVars');
    // The first per-row substituteVars(inputs[1].value) is the metadata pass.
    const sub = body.indexOf('substituteVars(inputs[1].value)');
    assert.ok(ai >= 0 && keyring >= 0, 'channelConnect must prefetch metadata vars');
    assert.ok(sub >= 0, 'channelConnect must substitute metadata values');
    assert.ok(ai < sub && keyring < sub, 'metadata prefetch must precede substitution');
});

// #676 — json-path.js, the browser half of Bowire's one path resolver.
//
// Paths/json-path-golden.json pins what every path selects; this file
// replays it through the real fragment, BowireJsonPathGoldenTests.cs through
// the C# resolver. A path that differs between the two fails one side.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { compileFragment } from './_load-fragment.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const GOLDEN = JSON.parse(readFileSync(resolve(__dirname, '../Paths/json-path-golden.json'), 'utf8'));

const api = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/json-path.js',
    [],
    'return { bowireJsonPathResolve, bowireJsonPathSelect };'
)();

test('golden fixture is non-empty (an emptied file must not pass vacuously)', () => {
    assert.ok(GOLDEN.cases.length >= 40, 'got ' + GOLDEN.cases.length);
});

for (const c of GOLDEN.cases) {
    test(`golden: ${c.path}`, () => {
        if (c.error !== undefined) {
            assert.throws(() => api.bowireJsonPathResolve(GOLDEN.document, c.path), (e) => e.position === c.error);
            return;
        }
        const r = api.bowireJsonPathResolve(GOLDEN.document, c.path);
        assert.equal(r.found, c.found);
        if (c.found) assert.deepEqual(r.value, c.value);
    });
}

test('a match says where it sits, so a writer can set it', () => {
    const doc = { items: [{ done: true, n: 1 }, { done: false, n: 2 }] };
    const matches = api.bowireJsonPathSelect(doc, '$.items[?(@.done == true)].n');
    assert.equal(matches.length, 1);
    assert.equal(matches[0].key, 'n');
    assert.equal(matches[0].parent, doc.items[0]);
});

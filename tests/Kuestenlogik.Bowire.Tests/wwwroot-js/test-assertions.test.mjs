// #676 — the Tests tab's assertion evaluator (test-assertions.js) on top of
// the JSONPath resolver (json-path.js): filter paths, the short form as
// before, exists / notexists telling a missing field from a JSON null, and a
// malformed path failing with its position instead of throwing.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment, readFragment } from './_load-fragment.mjs';

const JSON_PATH_SRC = readFragment('../../../src/Kuestenlogik.Bowire/wwwroot/js/json-path.js');

const api = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/test-assertions.js',
    [],
    JSON_PATH_SRC + `
        var localStorage = { getItem: function () { return null; }, setItem: function () {} };
        return { evaluateAssertion: evaluateAssertion };`
)();

const body = {
    id: 7,
    deletedAt: null,
    items: [
        { id: 'a', done: true, price: 12 },
        { id: 'b', done: false, price: 8 },
        { id: 'c', done: true, price: 20 },
    ],
};

function run(path, op, expected) {
    return api.evaluateAssertion({ path, op, expected }, 'OK', body);
}

test('the short form works as before, with and without the response. prefix', () => {
    assert.equal(run('response.id', 'eq', 7).pass, true);
    assert.equal(run('items.1.id', 'eq', 'b').pass, true);
});

test('a filter path selects every match, as an array', () => {
    const r = run('$.items[?(@.done == true)].id', 'eq', ['a', 'c']);
    assert.equal(r.pass, true);
    assert.deepEqual(r.actual, ['a', 'c']);
    assert.equal(run('$.items[?(@.price > 10)].id', 'contains', 'c').pass, true);
});

test('exists is about the path matching: a JSON null exists, a missing field does not', () => {
    assert.equal(run('deletedAt', 'exists').pass, true);
    assert.equal(run('deletedAt', 'notexists').pass, false);
    assert.equal(run('deletedAt', 'eq', null).pass, true);
    assert.equal(run('archivedAt', 'exists').pass, false);
    assert.equal(run('archivedAt', 'notexists').pass, true);
    assert.equal(run('$.items[?(@.price > 100)]', 'notexists').pass, true);
});

test('a malformed path fails the assertion with its position', () => {
    const r = run('$.items[?(@.done ==)]', 'exists');
    assert.equal(r.pass, false);
    assert.match(r.message, /^Invalid JSONPath at position 19:/);
});

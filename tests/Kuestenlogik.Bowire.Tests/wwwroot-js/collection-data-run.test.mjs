// #675 — a data-driven collection run in the workbench.
//
// collections.js's runCollection runs the items once per row of the data set
// the DataSets package attached (bowireCollectionDataSet), with the row's
// values on bowireRunIteration — the layer history-env.js lays over the
// environment — and keeps one outcome per row for the results table. The
// per-item network call is replaced here; what is under test is the loop.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const _host = `
        var collectionsList = [{ id: 'col_1', name: 'Users', items: [
            { id: 'ci_a', service: 'Users', method: 'Get', methodType: 'Unary' },
            { id: 'ci_b', service: 'Users', method: 'Update', methodType: 'Unary' }
        ] }];
        var collectionRunState = null;
        var bowireRunIteration = null;
        var dataSet = __harness.dataSet;
        var calls = [];
        function bowireCollectionDataSet(id) { return id === 'col_1' ? dataSet : null; }
        function render() {}
        function captureResponse() {}
        var localStorage = { getItem: function () { return null; }, setItem: function () {} };
        // The network call, replaced: record what the row bound, fail where the row says so.
        function runCollectionItem(item) {
            var it = bowireRunIteration;
            calls.push({ item: item.id, iteration: it.iteration, count: it.count, user: it.vars.user });
            var pass = !(it.vars.fail === item.id);
            return Promise.resolve({ pass: pass, status: pass ? 'OK' : 'NotFound', durationMs: 1 });
        }
        return {
            run: runCollection,
            state: function () { return collectionRunState; },
            calls: function () { return calls; },
            iteration: function () { return bowireRunIteration; }
        };
`;

const load = compileFragment('../../../src/Kuestenlogik.Bowire/wwwroot/js/collections.js', ['__harness'], _host);

function csvDataSet(failFast) {
    return {
        fileName: 'users.csv', format: 'csv', failFast: failFast,
        rows: [
            { iteration: 1, sourceLine: 2, values: { user: 'ada' } },
            { iteration: 2, sourceLine: 3, values: { user: 'grace', fail: 'ci_b' } },
            { iteration: 3, sourceLine: 4, values: { user: 'linus' } }
        ]
    };
}

test('every item runs once per row, with the row bound', async () => {
    const f = load({ __harness: { dataSet: csvDataSet(false) } });
    await f.run('col_1');

    assert.deepEqual(f.calls().map(c => `${c.iteration}/${c.count}:${c.item}:${c.user}`), [
        '1/3:ci_a:ada', '1/3:ci_b:ada',
        '2/3:ci_a:grace', '2/3:ci_b:grace',
        '3/3:ci_a:linus', '3/3:ci_b:linus'
    ]);
    // The row layer is gone once the run is over.
    assert.equal(f.iteration(), null);
});

test('each row keeps its outcome, and a failure names the row and the item', async () => {
    const f = load({ __harness: { dataSet: csvDataSet(false) } });
    await f.run('col_1');

    const s = f.state();
    assert.equal(s.status, 'done');
    assert.deepEqual(s.iterations.map(i => i.pass), [true, false, true]);
    assert.equal(s.iterations[1].source, 'users.csv line 3');
    assert.deepEqual(s.iterations[1].failed.map(x => x.name + ' ' + x.status), ['Users / Update NotFound']);
    assert.equal(s.stoppedEarly, false);
});

test('fail-fast stops after the first failing row', async () => {
    const f = load({ __harness: { dataSet: csvDataSet(true) } });
    await f.run('col_1');

    const s = f.state();
    assert.equal(s.iterations.length, 2);
    assert.equal(s.stoppedEarly, true);
    assert.equal(f.calls().length, 4);
});

test('a JSON row is named by its item, and without a data set the run is one pass as before', async () => {
    const json = load({ __harness: { dataSet: { fileName: 'u.json', format: 'json', failFast: false, rows: [{ iteration: 1, sourceLine: 1, values: { fail: 'ci_a' } }] } } });
    await json.run('col_1');
    assert.equal(json.state().iterations[0].source, 'u.json item 1');

    const plain = load({ __harness: { dataSet: null } });
    await plain.run('col_1');
    assert.equal(plain.state().iterations, null);
    assert.equal(plain.calls().length, 2);
    assert.deepEqual(plain.calls().map(c => c.iteration + '/' + c.count), ['1/1', '1/1']);
});

// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #174 — the in-browser flow runner expands a data-driven step into one
// execution per row.
//
// Until this shipped, `node.data` was authored in the workbench and read
// only by `bowire test`: in the browser the step ran once, with its
// `{{placeholders}}` unresolved, and reported a single result that looked
// like a pass. What is checked here is the part that is easy to get
// subtly wrong — how many executions happen, what each one sees in the
// variable scope, and what the scope looks like afterwards.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const FLOWS = '../../../src/Kuestenlogik.Bowire.Flows/wwwroot/js/flows.js';

const HOST_NAMES = [
    'el', 'config', 'render', 'fetch', 'localStorage', 'window', 'document',
    'substituteVars', 'captureResponse', 'bowireToast', 'bowirePrompt',
    'bowireConfirm', 'services', 'protocols', 'performance', 'tNodes',
];

// Everything after the fragment: hand the pieces under test back out, and
// give the suite a way to drive module state the fragment keeps private.
const APPENDED = `
return {
    executeDataDrivenRequest,
    setRunning: () => { flowRunStatus = 'running'; },
    setVars: (v) => { flowVars = v; },
    getVars: () => flowVars,
};
`;

function load({ rows, expandStatus = 200, failRow = null }) {
    const executions = [];
    const fragment = compileFragment(FLOWS, HOST_NAMES, APPENDED);
    // The fetch stub has to read the fragment's private variable scope,
    // which only exists once the fragment has been compiled and run. Bind
    // it late through this holder rather than threading a callback the
    // caller cannot build yet.
    const bound = {};

    const hosts = {
        el: () => ({ appendChild() {} }),
        config: { prefix: '' },
        render: () => {},
        localStorage: { getItem: () => null, setItem: () => {} },
        window: { addEventListener() {} },
        document: { createElement: () => ({ style: {}, appendChild() {} }) },
        substituteVars: (x) => x,
        captureResponse: () => {},
        bowireToast: () => {},
        bowirePrompt: () => Promise.resolve(null),
        bowireConfirm: () => Promise.resolve(false),
        services: [],
        protocols: [],
        performance: { now: () => 0 },
        tNodes: (k) => k,
        // The row expansion the runner asks the server for, and the
        // per-row invoke it makes afterwards.
        fetch: (url, init) => {
            if (String(url).includes('/api/flows/data/expand')) {
                return Promise.resolve({
                    ok: expandStatus === 200,
                    status: expandStatus,
                    json: () => Promise.resolve(
                        expandStatus === 200 ? { rows } : { error: 'no rows for you' }),
                });
            }
            // An invoke: record what the variable scope held when it was made.
            executions.push({
                body: JSON.parse(init.body),
                // A copy: the runner puts the row's columns back after the
                // row, so a live reference would read the restored scope.
                scope: bound.peek ? { ...bound.peek() } : null,
            });
            // `failRow` names the row whose invoke comes back refused, so
            // a partial pass can be asserted rather than assumed.
            const current = bound.peek ? bound.peek().__row : null;
            const refused = failRow !== null && current === failRow;
            return Promise.resolve({
                ok: !refused,
                json: () => Promise.resolve(refused
                    ? { title: 'refused', status: 'Error' }
                    : { status: 'OK', response: '{}', duration_ms: 1 }),
            });
        },
    };

    const api = fragment(hosts);
    bound.peek = api.getVars;
    return { api, executions };
}

const STEP = { id: 'n1', type: 'request', service: 'S', method: 'M', body: '{}', data: { inline: [] } };

test('a row source runs the step once per row', async () => {
    const { api, executions } = load({
        rows: [{ label: 'a', values: { userId: '1' } }, { label: 'b', values: { userId: '2' } }],
    });
    api.setRunning();
    api.setVars({});

    const result = await api.executeDataDrivenRequest(STEP);

    assert.equal(executions.length, 2, 'one invoke per row');
    assert.equal(result.rowsTotal, 2);
    assert.equal(result.rowsPassed, 2);
    assert.equal(result.pass, true);
    assert.deepEqual(result.rows.map((r) => r.label), ['a', 'b']);
});

test("each row's columns are in scope while that row runs", async () => {
    const { api, executions } = load({
        rows: [
            { label: 'a', values: { userId: '1', role: 'admin' } },
            { label: 'b', values: { userId: '2', role: 'guest' } },
        ],
    });
    api.setRunning();
    api.setVars({ role: 'from-the-run' });

    await api.executeDataDrivenRequest(STEP);

    // Each invoke saw its own row, not the previous one and not the
    // run-level value the row shadows.
    assert.deepEqual(executions.map((e) => e.scope.userId), ['1', '2']);
    assert.deepEqual(executions.map((e) => e.scope.role), ['admin', 'guest']);
    // And the run-level value is back afterwards.
    assert.equal(api.getVars().role, 'from-the-run');
});

test('the variable scope is left as it was found', async () => {
    const { api } = load({
        rows: [{ label: 'a', values: { userId: '9' } }],
    });
    api.setRunning();
    api.setVars({ userId: 'original', other: 'keep' });

    await api.executeDataDrivenRequest(STEP);

    // A row's column wins for the row and is put back afterwards, so row
    // N+1 — and anything downstream — does not inherit row N.
    assert.deepEqual(api.getVars(), { userId: 'original', other: 'keep' });
});

test('a column with no previous value does not linger', async () => {
    const { api } = load({ rows: [{ label: 'a', values: { fresh: '1' } }] });
    api.setRunning();
    api.setVars({ kept: 'yes' });

    await api.executeDataDrivenRequest(STEP);

    assert.deepEqual(api.getVars(), { kept: 'yes' });
});

test('one failing row fails the step, and the count says which', async () => {
    const { api } = load({
        rows: [
            { label: 'a', values: { __row: 'a' } },
            { label: 'b', values: { __row: 'b' } },
            { label: 'c', values: { __row: 'c' } },
        ],
        failRow: 'b',
    });
    api.setRunning();
    api.setVars({});

    const result = await api.executeDataDrivenRequest(STEP);

    // A step whose rows partly failed is a failed step: reporting the
    // majority as a pass is how a parameterised probe goes quiet.
    assert.equal(result.pass, false);
    assert.equal(result.rowsPassed, 2);
    assert.equal(result.rowsTotal, 3);
    assert.match(result.status, /2\/3 rows passed/);
    assert.deepEqual(result.rows.map((r) => r.pass), [true, false, true]);
    // The failing row is identifiable, which is the whole point of a label.
    assert.equal(result.rows.find((r) => !r.pass).label, 'b');
});

test('rows that cannot be expanded fail the step rather than passing vacuously', async () => {
    const { api, executions } = load({ rows: [], expandStatus: 400 });
    api.setRunning();
    api.setVars({});

    const result = await api.executeDataDrivenRequest(STEP);

    assert.equal(result.pass, false);
    assert.equal(executions.length, 0, 'nothing is invoked when there are no rows');
    assert.match(result.error, /no rows for you/);
});

test('a stopped run does not keep working through the rows', async () => {
    const { api, executions } = load({
        rows: [{ label: 'a', values: {} }, { label: 'b', values: {} }],
    });
    api.setVars({});
    // Never set to running: the loop checks before each row.
    const result = await api.executeDataDrivenRequest(STEP);

    assert.equal(executions.length, 0);
    assert.equal(result.rowsTotal, 0);
    assert.equal(result.pass, false, 'no rows run is not a pass');
});

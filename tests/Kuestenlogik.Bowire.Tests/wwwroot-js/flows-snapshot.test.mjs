// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #171 — the in-browser runner checks a step's snapshot and can accept the
// new answer as the baseline.
//
// Re-baselining was `bowire test --update-snapshots`, so the person who
// could see the drift on screen was not the one who could act on it. What
// is checked here is the part the server cannot check for itself: that the
// runner asks at all, that drift fails the step rather than only colouring
// a panel, and that a step with no workspace says so instead of quietly
// not being checked.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const FLOWS = '../../../src/Kuestenlogik.Bowire.Flows/wwwroot/js/flows.js';

const HOST_NAMES = [
    'el', 'config', 'render', 'fetch', 'localStorage', 'window', 'document',
    'substituteVars', 'captureResponse', 'bowireToast', 'bowirePrompt',
    'bowireConfirm', 'services', 'protocols', 'performance', 'tNodes',
    'activeWorkspaceId', 'activeWorkspace',
];

const APPENDED = `
return {
    evaluateSnapshot,
    approveSnapshot,
    bodyTextOf,
    setResults: (r) => { flowRunResults = r; },
    getResults: () => flowRunResults,
};
`;

function load({ compare = null, approve = null, workspaceId = 'harbor', storageRoot = null } = {}) {
    const calls = [];

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
        bowirePrompt: () => null,
        bowireConfirm: () => true,
        services: [],
        protocols: [],
        performance: { now: () => 0 },
        tNodes: (k) => [k],
        activeWorkspaceId: workspaceId,
        activeWorkspace: () => (storageRoot ? { storageRoot } : null),
        fetch: async (url, init) => {
            const body = JSON.parse(init.body);
            calls.push({ url, body });
            const canned = url.endsWith('/compare') ? compare : approve;
            return {
                ok: canned?.ok !== false,
                json: async () => canned?.data ?? {},
            };
        },
    };

    return { api: compileFragment(FLOWS, HOST_NAMES, APPENDED)(hosts), calls };
}

const NODE = { id: 'n1', snapshot: { mode: 'exact', ignore: ['$.updatedAt'] } };

test('a step without a snapshot block is not asked about', async () => {
    const { api, calls } = load();
    const result = { response: { ok: true } };

    await api.evaluateSnapshot('flow_a', { id: 'n1' }, result);

    assert.equal(calls.length, 0);
    assert.equal(result.snapshot, undefined);
});

test('a disabled snapshot is not asked about either', async () => {
    const { api, calls } = load();
    const result = { response: { ok: true } };

    await api.evaluateSnapshot('flow_a', { id: 'n1', snapshot: { enabled: false } }, result);

    assert.equal(calls.length, 0);
});

test('a step that errored is not compared', async () => {
    // There is no response to compare, and writing the error as a baseline
    // would make the next run "pass" against it.
    const { api, calls } = load();

    await api.evaluateSnapshot('flow_a', NODE, { error: 'connection refused' });

    assert.equal(calls.length, 0);
});

test('the flow, the step and the comparison settings all travel', async () => {
    // The flow id because a baseline belongs to a flow, not to the file;
    // mode and ignore because otherwise the browser and the CLI would be
    // asking different questions of the same bytes.
    const { api, calls } = load({ compare: { data: { captured: true, diffs: [] } } });

    await api.evaluateSnapshot('flow_a', NODE, { response: { id: 42 } });

    assert.equal(calls.length, 1);
    assert.equal(calls[0].body.flowId, 'flow_a');
    assert.equal(calls[0].body.stepId, 'n1');
    assert.equal(calls[0].body.workspaceId, 'harbor');
    assert.equal(calls[0].body.mode, 'exact');
    assert.deepEqual(calls[0].body.ignore, ['$.updatedAt']);
});

test('a git-native workspace sends its checkout', async () => {
    const { api, calls } = load({
        compare: { data: { captured: true, diffs: [] } },
        storageRoot: 'C:/checkouts/harbor',
    });

    await api.evaluateSnapshot('flow_a', NODE, { response: {} });

    assert.equal(calls[0].body.storageRoot, 'C:/checkouts/harbor');
});

test('drift fails the step', async () => {
    // A snapshot that only colours a panel is not a test. The CLI fails on
    // drift; so does this.
    const { api } = load({ compare: { data: { captured: true, diffs: ['name: "Ada" -> "Grace"'] } } });
    const result = { pass: true, response: {} };

    await api.evaluateSnapshot('flow_a', NODE, result);

    assert.equal(result.pass, false);
    assert.deepEqual(result.snapshot.diffs, ['name: "Ada" -> "Grace"']);
});

test('a step with no baseline yet does not fail', async () => {
    // Nothing has been guarding it, which is a thing to offer to fix rather
    // than a failure to report.
    const { api } = load({ compare: { data: { captured: false, diffs: [] } } });
    const result = { pass: true, response: {} };

    await api.evaluateSnapshot('flow_a', NODE, result);

    assert.equal(result.pass, true);
    assert.equal(result.snapshot.captured, false);
});

test('without a workspace it says so instead of skipping quietly', async () => {
    // A snapshot that is silently not being checked is worse than one that
    // says it cannot be.
    const { api, calls } = load({ workspaceId: '' });
    const result = { pass: true, response: {} };

    await api.evaluateSnapshot('flow_a', NODE, result);

    assert.equal(calls.length, 0);
    assert.ok(result.snapshot.unavailable);
});

test('a server refusal is reported, not swallowed', async () => {
    const { api } = load({ compare: { ok: false, data: { error: 'storageRoot must be an absolute path.' } } });
    const result = { pass: true, response: {} };

    await api.evaluateSnapshot('flow_a', NODE, result);

    assert.match(result.snapshot.unavailable, /absolute/);
});

test('approving sends the response and reports the file it went to', async () => {
    const { api, calls } = load({ approve: { data: { approved: true, file: '/ws/__snapshots__/flow_a/n1.snap.json' } } });
    api.setResults({ n1: { pass: false, response: { id: 43 } } });

    await api.approveSnapshot('flow_a', NODE);

    const call = calls.find((c) => c.url.endsWith('/approve'));
    assert.equal(call.body.flowId, 'flow_a');
    assert.equal(call.body.actual, '{"id":43}');

    const result = api.getResults().n1;
    assert.equal(result.pass, true);
    assert.match(result.snapshot.file, /n1\.snap\.json$/);
});

test('a body is sent as the text a baseline is made of', () => {
    const { api } = load();

    assert.equal(api.bodyTextOf({ id: 42 }), '{"id":42}');
    assert.equal(api.bodyTextOf('raw text'), 'raw text');
    assert.equal(api.bodyTextOf(null), '');
});

// #145 — vars-deprecation.js unit tests.
//
// The fragment is concatenated INSIDE prologue.js's IIFE so every
// free identifier (recordingsList, collectionsList, freeformRequest,
// flowsList, getEnvironments, getGlobalVars, persistRecordings,
// persistCollections, persistFlows, saveEnvironments, saveGlobalVars,
// render, toast, el, wsKey, localStorage, console) is module-scoped
// in production. We re-create that scope here by wrapping the source
// in a fresh IIFE, declaring the host-provided names as `var`
// bindings on the wrapping scope, and exposing the testable surface
// via the runner's return-value.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/vars-deprecation.js'),
    'utf8'
);

// Build a sandbox that:
//   - declares the host-provided names + the workspace state arrays
//   - inlines the fragment under test
//   - returns the migrate functions + a setter for each piece of state
//     so each test can prime a scenario before calling them.
function loadDeprecation(state) {
    const prelude = `
        var recordingsList = state.recordingsList || [];
        var collectionsList = state.collectionsList || [];
        var freeformRequest = state.freeformRequest || null;
        var flowsList = state.flowsList || [];
        var _envStore = state.envs || [];
        var _globalStore = state.globals || {};
        var _snoozeFlags = {};
        var localStorage = {
            getItem: function (k) { return _snoozeFlags[k] || null; },
            setItem: function (k, v) { _snoozeFlags[k] = String(v); },
            removeItem: function (k) { delete _snoozeFlags[k]; }
        };
        var _persistCalls = [];
        function persistRecordings() { _persistCalls.push('recordings'); }
        function persistCollections() { _persistCalls.push('collections'); }
        function persistFlows() { _persistCalls.push('flows'); }
        function getEnvironments() { return _envStore; }
        function saveEnvironments(envs) { _envStore = envs; _persistCalls.push('environments'); }
        function getGlobalVars() { return _globalStore; }
        function saveGlobalVars(v) { _globalStore = v; _persistCalls.push('globals'); }
        function wsKey(k) { return 'ws::' + k; }
        function render() { _persistCalls.push('render'); }
        function toast() { /* swallow */ return null; }
        function el() { return {}; }
    `;
    const postlude = `
        return {
            migrateString: migrateString,
            migrateLegacyVars: migrateLegacyVars,
            workspaceHasLegacyVars: workspaceHasLegacyVars,
            isLegacyVarsToastSnoozed: isLegacyVarsToastSnoozed,
            snoozeLegacyVarsToast: snoozeLegacyVarsToast,
            _getState: function () {
                return {
                    recordingsList: recordingsList,
                    collectionsList: collectionsList,
                    freeformRequest: freeformRequest,
                    flowsList: flowsList,
                    envs: _envStore,
                    globals: _globalStore,
                    persistCalls: _persistCalls,
                    snoozeFlags: _snoozeFlags
                };
            }
        };
    `;
    const body = prelude + '\n' + SRC + '\n' + postlude;
    const fn = new Function('state', body);
    return fn(state || {});
}

// ---- migrateString — pure, per-string rewriter ----

test('migrateString: bare ${name} → {{name}}', () => {
    const sb = loadDeprecation();
    assert.equal(sb.migrateString('hello ${name}'), 'hello {{name}}');
    assert.equal(sb.migrateString('${a}/${b}'), '{{a}}/{{b}}');
});

test('migrateString: ${response} → {{prev}}', () => {
    const sb = loadDeprecation();
    assert.equal(sb.migrateString('${response}'), '{{prev}}');
    assert.equal(sb.migrateString('${response.id}'), '{{prev.id}}');
    assert.equal(sb.migrateString('${response.data.x}'), '{{prev.data.x}}');
});

test('migrateString: system vars → {{runtime.X}}', () => {
    const sb = loadDeprecation();
    assert.equal(sb.migrateString('${now}'), '{{runtime.now}}');
    assert.equal(sb.migrateString('${nowMs}'), '{{runtime.nowMs}}');
    assert.equal(sb.migrateString('${timestamp}'), '{{runtime.timestamp}}');
    assert.equal(sb.migrateString('${uuid}'), '{{runtime.uuid}}');
    assert.equal(sb.migrateString('${random}'), '{{runtime.random}}');
});

test('migrateString: ${now+N} / ${now-N} arithmetic', () => {
    const sb = loadDeprecation();
    assert.equal(sb.migrateString('${now+3600}'), '{{runtime.now+3600}}');
    assert.equal(sb.migrateString('${now-60}'), '{{runtime.now-60}}');
});

test('migrateString: $${name} escape → {{{{name}}}}', () => {
    const sb = loadDeprecation();
    assert.equal(sb.migrateString('$${name}'), '{{{{name}}}}');
    // Mixed: escape + real
    assert.equal(sb.migrateString('$${a} and ${b}'), '{{{{a}}}} and {{b}}');
});

test('migrateString: existing {{name}} left untouched', () => {
    const sb = loadDeprecation();
    assert.equal(sb.migrateString('{{already}}'), '{{already}}');
    assert.equal(sb.migrateString('{{a}} and ${b}'), '{{a}} and {{b}}');
});

test('migrateString: no ${ in string returns identical reference', () => {
    const sb = loadDeprecation();
    const s = 'plain text {{already}} no dollar';
    assert.equal(sb.migrateString(s), s);
});

test('migrateString: non-strings pass through', () => {
    const sb = loadDeprecation();
    assert.equal(sb.migrateString(null), null);
    assert.equal(sb.migrateString(undefined), undefined);
    assert.equal(sb.migrateString(42), 42);
});

test('migrateString: idempotent (running twice == running once)', () => {
    const sb = loadDeprecation();
    const cases = [
        'hello ${name}',
        '${response.id}/$${escaped}/${now+10}',
        'plain {{already}} ${legacy}'
    ];
    for (const s of cases) {
        const once = sb.migrateString(s);
        const twice = sb.migrateString(once);
        assert.equal(twice, once, 'idempotency broken for ' + JSON.stringify(s));
    }
});

test('migrateString: env.X / captured.X / secret.X / ai.X prefixes pass through name-for-name', () => {
    const sb = loadDeprecation();
    assert.equal(sb.migrateString('${env.TOKEN}'), '{{env.TOKEN}}');
    assert.equal(sb.migrateString('${captured.id}'), '{{captured.id}}');
    assert.equal(sb.migrateString('${secret.api}'), '{{secret.api}}');
    assert.equal(sb.migrateString('${ai.suggestion}'), '{{ai.suggestion}}');
    assert.equal(sb.migrateString('${step1.path}'), '{{step1.path}}');
});

test('migrateString: name with surrounding whitespace gets trimmed', () => {
    const sb = loadDeprecation();
    assert.equal(sb.migrateString('${  name  }'), '{{name}}');
});

// ---- workspaceHasLegacyVars — scanner ----

test('workspaceHasLegacyVars: false on empty workspace', () => {
    const sb = loadDeprecation({});
    assert.equal(sb.workspaceHasLegacyVars(), false);
});

test('workspaceHasLegacyVars: false on a workspace with only curly syntax', () => {
    const sb = loadDeprecation({
        recordingsList: [{ steps: [{ body: '{"x":"{{name}}"}', metadata: { auth: '{{token}}' } }] }],
        freeformRequest: { body: '{{ok}}', serverUrl: 'https://{{host}}', metadata: {} }
    });
    assert.equal(sb.workspaceHasLegacyVars(), false);
});

test('workspaceHasLegacyVars: true on recording body with ${X}', () => {
    const sb = loadDeprecation({
        recordingsList: [{ steps: [{ body: 'hello ${name}', metadata: {} }] }]
    });
    assert.equal(sb.workspaceHasLegacyVars(), true);
});

test('workspaceHasLegacyVars: true on recording metadata with ${X}', () => {
    const sb = loadDeprecation({
        recordingsList: [{ steps: [{ body: '{}', metadata: { Authorization: 'Bearer ${TOKEN}' } }] }]
    });
    assert.equal(sb.workspaceHasLegacyVars(), true);
});

test('workspaceHasLegacyVars: true on collection item', () => {
    const sb = loadDeprecation({
        collectionsList: [{ items: [{ body: '${x}', metadata: {} }] }]
    });
    assert.equal(sb.workspaceHasLegacyVars(), true);
});

test('workspaceHasLegacyVars: true on freeform request', () => {
    const sb = loadDeprecation({
        freeformRequest: { body: 'use ${a}', metadata: {} }
    });
    assert.equal(sb.workspaceHasLegacyVars(), true);
});

test('workspaceHasLegacyVars: true on flow node config', () => {
    const sb = loadDeprecation({
        flowsList: [{ nodes: [{ config: { path: '${api}/x' } }] }]
    });
    assert.equal(sb.workspaceHasLegacyVars(), true);
});

test('workspaceHasLegacyVars: true on environment var', () => {
    const sb = loadDeprecation({
        envs: [{ vars: { GREETING: 'hello ${user}' } }]
    });
    assert.equal(sb.workspaceHasLegacyVars(), true);
});

test('workspaceHasLegacyVars: true on global var', () => {
    const sb = loadDeprecation({
        globals: { default: '${user}' }
    });
    assert.equal(sb.workspaceHasLegacyVars(), true);
});

test('workspaceHasLegacyVars: $${X} escape does NOT trigger detection', () => {
    const sb = loadDeprecation({
        recordingsList: [{ steps: [{ body: 'literal $${notVar}', metadata: {} }] }]
    });
    assert.equal(sb.workspaceHasLegacyVars(), false);
});

// ---- migrateLegacyVars — full walk ----

test('migrateLegacyVars: empty workspace returns total=0 + no persist calls', () => {
    const sb = loadDeprecation({});
    const counts = sb.migrateLegacyVars();
    assert.equal(counts.total, 0);
    assert.equal(counts.recordings, 0);
    assert.equal(counts.collections, 0);
    const st = sb._getState();
    // render is always invoked; persist hooks only fire when something changed
    assert.equal(st.persistCalls.includes('recordings'), false);
    assert.equal(st.persistCalls.includes('collections'), false);
});

test('migrateLegacyVars: rewrites recording body + metadata + persists', () => {
    const sb = loadDeprecation({
        recordingsList: [
            { steps: [{ body: 'hi ${name}', metadata: { Authorization: 'Bearer ${TOKEN}' } }] }
        ]
    });
    const counts = sb.migrateLegacyVars();
    assert.equal(counts.recordings, 2);
    const st = sb._getState();
    assert.equal(st.recordingsList[0].steps[0].body, 'hi {{name}}');
    assert.equal(st.recordingsList[0].steps[0].metadata.Authorization, 'Bearer {{TOKEN}}');
    assert.ok(st.persistCalls.includes('recordings'));
});

test('migrateLegacyVars: collection items + headers + params', () => {
    const sb = loadDeprecation({
        collectionsList: [{
            items: [{
                body: '${x}',
                serverUrl: 'https://${host}/api',
                headers: { Auth: '${TOKEN}' },
                params: { page: '${page}' },
                metadata: {}
            }]
        }]
    });
    const counts = sb.migrateLegacyVars();
    assert.equal(counts.collections, 4);
    const st = sb._getState();
    const item = st.collectionsList[0].items[0];
    assert.equal(item.body, '{{x}}');
    assert.equal(item.serverUrl, 'https://{{host}}/api');
    assert.equal(item.headers.Auth, '{{TOKEN}}');
    assert.equal(item.params.page, '{{page}}');
    assert.ok(st.persistCalls.includes('collections'));
});

test('migrateLegacyVars: freeform request', () => {
    const sb = loadDeprecation({
        freeformRequest: { body: '${a}', serverUrl: '${u}', metadata: { k: '${v}' } }
    });
    const counts = sb.migrateLegacyVars();
    assert.equal(counts.freeform, 3);
});

test('migrateLegacyVars: flows', () => {
    const sb = loadDeprecation({
        flowsList: [{ nodes: [{ config: { url: '${u}', topic: '${t}' } }] }]
    });
    const counts = sb.migrateLegacyVars();
    assert.equal(counts.flows, 2);
    const st = sb._getState();
    assert.equal(st.flowsList[0].nodes[0].config.url, '{{u}}');
    assert.ok(st.persistCalls.includes('flows'));
});

test('migrateLegacyVars: environments + globals', () => {
    const sb = loadDeprecation({
        envs: [{ vars: { K: '${x}', Y: 'plain' } }],
        globals: { G: '${y}' }
    });
    const counts = sb.migrateLegacyVars();
    assert.equal(counts.environments, 1);
    assert.equal(counts.globals, 1);
    assert.equal(counts.total, 2);
    const st = sb._getState();
    assert.ok(st.persistCalls.includes('environments'));
    assert.ok(st.persistCalls.includes('globals'));
});

test('migrateLegacyVars: idempotent — second run returns total=0', () => {
    const sb = loadDeprecation({
        recordingsList: [{ steps: [{ body: '${x}', metadata: {} }] }]
    });
    const first = sb.migrateLegacyVars();
    assert.equal(first.recordings, 1);
    const second = sb.migrateLegacyVars();
    assert.equal(second.total, 0);
});

test('migrateLegacyVars: always snoozes the toast after running', () => {
    const sb = loadDeprecation({});
    sb.migrateLegacyVars();
    assert.equal(sb.isLegacyVarsToastSnoozed(), true);
});

// ---- snooze ----

test('isLegacyVarsToastSnoozed: returns false initially', () => {
    const sb = loadDeprecation();
    assert.equal(sb.isLegacyVarsToastSnoozed(), false);
});

test('snoozeLegacyVarsToast then isLegacyVarsToastSnoozed reports true', () => {
    const sb = loadDeprecation();
    sb.snoozeLegacyVarsToast();
    assert.equal(sb.isLegacyVarsToastSnoozed(), true);
});

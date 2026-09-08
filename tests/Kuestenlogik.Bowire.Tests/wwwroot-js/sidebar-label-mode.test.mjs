// #47 — unit tests for the sidebar label mode in helpers.js:
// methodRowLabel (which text identifies a method in a sidebar row) and
// methodRowTitle (the tooltip that carries whatever the row doesn't show).
//
// Both read `methodLabelMode` out of the enclosing IIFE scope, so the
// mode arrives here as a function parameter — the same snip-and-eval
// approach list-filter-sort.test.mjs uses for the other pure helpers.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/helpers.js'),
    'utf8'
);

function extractFn(name) {
    const start = SRC.indexOf(`function ${name}(`);
    assert.ok(start >= 0, `function ${name} not found in helpers.js`);
    const open = SRC.indexOf('{', start);
    let depth = 0, i = open;
    for (; i < SRC.length; i++) {
        if (SRC[i] === '{') depth++;
        else if (SRC[i] === '}') { depth--; if (depth === 0) { i++; break; } }
    }
    return SRC.slice(start, i);
}

// Build the two helpers with `methodLabelMode` bound to the mode under test.
const withMode = (mode) => new Function(
    'methodLabelMode',
    extractFn('methodRowLabel') + '\n' +
    extractFn('methodRowTitle') + '\n' +
    'return { methodRowLabel, methodRowTitle };'
)(mode);

const NAME = withMode('name');
const ROUTE = withMode('route');

// A transcoded REST endpoint: has both identities.
const rest = {
    name: 'GetForecast',
    httpMethod: 'GET',
    httpPath: '/api/Weather/forecast/{city}',
    summary: 'Five-day forecast for a city',
};

// A plain gRPC method: no route, so there is nothing to switch to.
const grpc = { name: 'StreamTelemetry', methodType: 'ServerStreaming' };

// ---- methodRowLabel ----

test('name mode shows the method name even when a route exists', () => {
    assert.equal(NAME.methodRowLabel(rest), 'GetForecast');
});

test('route mode shows the HTTP path', () => {
    assert.equal(ROUTE.methodRowLabel(rest), '/api/Weather/forecast/{city}');
});

test('route mode does not repeat the verb — the row badge already carries it', () => {
    assert.ok(!ROUTE.methodRowLabel(rest).includes('GET'));
});

test('route mode falls back to the name for a method without a route', () => {
    assert.equal(ROUTE.methodRowLabel(grpc), 'StreamTelemetry');
    assert.equal(NAME.methodRowLabel(grpc), 'StreamTelemetry');
});

test('a favorite whose service is gone falls back to the stored name', () => {
    // No method object left — only the name the favorite was saved under.
    assert.equal(ROUTE.methodRowLabel(null, 'GetForecast'), 'GetForecast');
    assert.equal(NAME.methodRowLabel(undefined, 'GetForecast'), 'GetForecast');
});

test('no method and no fallback yields an empty label, never "undefined"', () => {
    assert.equal(ROUTE.methodRowLabel(null), '');
    assert.equal(NAME.methodRowLabel(null), '');
});

// ---- methodRowTitle ----

test('name mode keeps the pre-#47 tooltip: summary wins over the name', () => {
    assert.equal(NAME.methodRowTitle(rest), 'Five-day forecast for a city');
});

test('name mode falls back to description, then to the name', () => {
    assert.equal(
        NAME.methodRowTitle({ name: 'Ping', description: 'Liveness probe' }),
        'Liveness probe'
    );
    assert.equal(NAME.methodRowTitle({ name: 'Ping' }), 'Ping');
});

test('route mode puts the method name in the tooltip — the row no longer shows it', () => {
    const title = ROUTE.methodRowTitle(rest);
    assert.ok(title.startsWith('GetForecast'),
        `expected the name first, got: ${JSON.stringify(title)}`);
    assert.ok(title.includes('Five-day forecast for a city'));
    assert.equal(title, 'GetForecast\nFive-day forecast for a city');
});

test('route mode without a summary shows just the name', () => {
    const m = { name: 'GetForecast', httpMethod: 'GET', httpPath: '/api/w' };
    assert.equal(ROUTE.methodRowTitle(m), 'GetForecast');
});

test('route mode leaves the tooltip alone for a method with no route', () => {
    // Nothing was hidden from the row, so nothing has to be recovered.
    assert.equal(
        ROUTE.methodRowTitle({ name: 'StreamTelemetry', summary: 'Live feed' }),
        'Live feed'
    );
});

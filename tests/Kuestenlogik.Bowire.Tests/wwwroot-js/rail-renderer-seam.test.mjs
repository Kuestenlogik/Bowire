// #306 / #314 — tests for the rail-renderer-key seam dispatch in
// render-sidebar.js (_currentRailRenderer + _railModeById). The audit
// flagged this seam as built-but-unused ("zero adopters"); Compose is
// the first adopter, and these tests pin the dispatch contract so the
// remaining rail migrations have a regression net.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/render-sidebar.js'),
    'utf8'
);

// Snip the two seam functions out of the big fragment by name.
function extractFn(name) {
    const start = SRC.indexOf(`function ${name}(`);
    assert.ok(start >= 0, `function ${name} not found`);
    const open = SRC.indexOf('{', start);
    let depth = 0, i = open;
    for (; i < SRC.length; i++) {
        if (SRC[i] === '{') depth++;
        else if (SRC[i] === '}') { depth--; if (depth === 0) { i++; break; } }
    }
    return SRC.slice(start, i);
}

// Build a tiny harness: the two functions close over module-scope
// `railMode`, `_railModes`, and `window` — inject them as the harness's
// own vars so the extracted bodies resolve exactly as they do in the
// bundle.
function makeSeam({ railMode, railModes, registry }) {
    const body = `
        var railMode = ${JSON.stringify(railMode)};
        var _railModes = ${JSON.stringify(railModes)};
        var window = { __bowireRailRenderers: registry };
        ${extractFn('_currentRailRenderer')}
        ${extractFn('_railModeById')}
        return { _currentRailRenderer: _currentRailRenderer };
    `;
    // registry carries live functions, so pass it in rather than JSON.
    return new Function('registry', body)(registry);
}

test('resolves a registered main renderer by the descriptor key', () => {
    const fn = () => 'compose-main';
    const seam = makeSeam({
        railMode: 'compose',
        railModes: [{ id: 'compose', mainPaneRendererKey: 'composeMain' }],
        registry: { composeMain: fn },
    });
    assert.equal(seam._currentRailRenderer('main'), fn);
});

test('resolves sidebar and main independently', () => {
    const sb = () => 's', mn = () => 'm';
    const seam = makeSeam({
        railMode: 'compose',
        railModes: [{ id: 'compose', sidebarRendererKey: 'cSide', mainPaneRendererKey: 'cMain' }],
        registry: { cSide: sb, cMain: mn },
    });
    assert.equal(seam._currentRailRenderer('sidebar'), sb);
    assert.equal(seam._currentRailRenderer('main'), mn);
});

test('returns null when the rail sets no key (legacy rail falls through)', () => {
    const seam = makeSeam({
        railMode: 'discover',
        railModes: [{ id: 'discover' }],
        registry: {},
    });
    assert.equal(seam._currentRailRenderer('main'), null);
});

test('returns null when the key is set but the fn has not registered yet', () => {
    const seam = makeSeam({
        railMode: 'compose',
        railModes: [{ id: 'compose', mainPaneRendererKey: 'composeMain' }],
        registry: {}, // package fragment not loaded → nothing registered
    });
    assert.equal(seam._currentRailRenderer('main'), null);
});

test('returns null for an unknown active rail', () => {
    const seam = makeSeam({
        railMode: 'ghost',
        railModes: [{ id: 'compose', mainPaneRendererKey: 'composeMain' }],
        registry: { composeMain: () => 'x' },
    });
    assert.equal(seam._currentRailRenderer('main'), null);
});

// #707 — the host tears down what a viewer's mount() handed back, whether
// that is the cleanup function itself or a Promise of it.
//
// The map widget's mount is `async` (it loads MapLibre first), so
// `ext.viewer.mount()` returns a Promise. The teardown used to check
// `typeof unmount === 'function'`, see a Promise, and skip it — every
// re-render of the widget host left the previous widget registered,
// consuming frames, holding a map. These tests drive
// `mountWidgetsForMethod` through the real framework with a fake
// extension and a fake /api/semantics/effective.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const SRC = '../../../src/Kuestenlogik.Bowire/wwwroot/js/extensions.js';

// ---------------------------------------------------------------
// Stubs — the framework reads a handful of DOM surfaces at mount time
// (createElement for the slot, add/removeEventListener for the frame
// bus, documentElement / body for the theme bundle).
// ---------------------------------------------------------------

function makeElement(tag) {
    return {
        tagName: String(tag || 'div').toUpperCase(),
        style: {}, dataset: {}, children: [], parentNode: null,
        className: '', textContent: '', innerHTML: '',
        appendChild(c) { this.children.push(c); c.parentNode = this; return c; },
        removeChild(c) {
            const i = this.children.indexOf(c);
            if (i >= 0) this.children.splice(i, 1);
            c.parentNode = null; return c;
        },
        get childElementCount() { return this.children.length; },
        addEventListener() {}, removeEventListener() {},
        querySelectorAll() { return []; }, querySelector() { return null; },
        setAttribute() {}, getAttribute() { return null; },
        classList: { add() {}, remove() {}, toggle() {}, contains() { return false; } },
    };
}

function makeDocument() {
    return {
        createElement: makeElement,
        addEventListener() {}, removeEventListener() {},
        dispatchEvent() { return true; },
        getElementById() { return null; },
        querySelectorAll() { return []; }, querySelector() { return null; },
        documentElement: { getAttribute() { return 'dark'; } },
        body: makeElement('body'),
        currentScript: null,
    };
}

const settle = async (n = 6) => {
    for (let i = 0; i < n; i++) await new Promise((r) => setTimeout(r, 0));
};

// A response whose lat/lon sit under one parent — the pairing the
// fake extension below requires.
const ANNOTATIONS = {
    annotations: [
        { semantic: 'coordinate.latitude', jsonPath: '$.position.lat' },
        { semantic: 'coordinate.longitude', jsonPath: '$.position.lon' },
    ],
};

/**
 * Boot the framework with a fetch that answers the effective-annotations
 * call — optionally only when the test says so, to hold a mount back.
 */
function boot({ holdFetch = false } = {}) {
    const win = { __BOWIRE_CONFIG__: { prefix: '' } };
    let releaseFetch = null;
    const fetchStub = () => new Promise((resolve) => {
        const answer = () => resolve({ ok: true, json: async () => ANNOTATIONS });
        if (holdFetch) releaseFetch = answer; else answer();
    });
    const host = {
        window: win,
        document: makeDocument(),
        config: { prefix: '' },
        render() {},
        state: {},
        services: [],
        el: (tag) => makeElement(tag),
        fetch: fetchStub,
        localStorage: { getItem() { return null; }, setItem() {} },
        getComputedStyle() { return { fontFamily: 'system-ui' }; },
    };
    compileFragment(SRC, Object.keys(host), '')(host);
    return { win, fw: win.__bowireExtFramework, api: win.BowireExtensions, release: () => releaseFetch && releaseFetch() };
}

function registerViewer(api, mount) {
    api.register({
        id: 'test.viewer',
        bowireApi: '1.x',
        kind: 'coordinate.wgs84',
        pairing: { required: ['coordinate.latitude', 'coordinate.longitude'], scope: 'same-parent' },
        viewer: { label: 'Test', selectionMode: 'multi', mount },
    });
}

describe('extensions — mount / unmount lifecycle (#707)', { concurrency: 1 }, () => {

    it('calls the cleanup an async mount resolves to', async () => {
        const { api, fw } = boot();
        const calls = { mount: 0, unmount: 0 };
        registerViewer(api, async function mount() {
            calls.mount++;
            await new Promise((r) => setTimeout(r, 0)); // "MapLibre loading"
            return function unmount() { calls.unmount++; };
        });

        const pane = makeElement('div');
        const dispose = fw.mountWidgetsForMethod('Situation', 'Subscribe', pane);
        await settle();
        assert.equal(calls.mount, 1);
        assert.equal(pane.children.length, 1, 'one slot mounted');

        dispose();
        await settle();
        assert.equal(calls.unmount, 1, 'the resolved cleanup ran');
        assert.equal(pane.children.length, 0, 'slot removed');
    });

    it('still calls a cleanup returned synchronously', async () => {
        const { api, fw } = boot();
        let unmounted = 0;
        registerViewer(api, function mount() { return () => { unmounted++; }; });

        const pane = makeElement('div');
        const dispose = fw.mountWidgetsForMethod('Situation', 'Subscribe', pane);
        await settle();
        dispose();
        await settle();
        assert.equal(unmounted, 1);
    });

    it('awaits a cleanup that has not resolved yet at teardown time', async () => {
        // Teardown lands while the mount is still awaiting its renderer.
        const { api, fw } = boot();
        let resolveMount = null;
        let unmounted = 0;
        registerViewer(api, () => new Promise((r) => { resolveMount = r; }));

        const pane = makeElement('div');
        const dispose = fw.mountWidgetsForMethod('Situation', 'Subscribe', pane);
        await settle();
        assert.ok(resolveMount, 'mount was called');

        dispose();
        await settle();
        assert.equal(unmounted, 0, 'nothing to run yet');
        resolveMount(() => { unmounted++; });
        await settle();
        assert.equal(unmounted, 1, 'ran as soon as the mount finished');
    });

    it('does not mount at all when torn down before the annotations arrive', async () => {
        // A tab switch right after execute: dispose() runs before
        // /api/semantics/effective has answered.
        const { api, fw, release } = boot({ holdFetch: true });
        let mounted = 0;
        registerViewer(api, () => { mounted++; return () => {}; });

        const pane = makeElement('div');
        const dispose = fw.mountWidgetsForMethod('Situation', 'Subscribe', pane);
        await settle();
        dispose();
        release();
        await settle();
        assert.equal(mounted, 0);
        assert.equal(pane.children.length, 0);
    });

    it('survives an async mount that rejects', async () => {
        const { api, fw } = boot();
        const errors = [];
        const origError = console.error;
        console.error = (...a) => errors.push(a.join(' '));
        try {
            registerViewer(api, async () => { throw new Error('renderer failed to load'); });
            const pane = makeElement('div');
            const dispose = fw.mountWidgetsForMethod('Situation', 'Subscribe', pane);
            await settle();
            dispose();
            await settle();
            assert.equal(pane.children.length, 0, 'slot still removed');
            assert.ok(errors.some((e) => e.includes('test.viewer') && e.includes('renderer failed')), errors.join('\n'));
        } finally {
            console.error = origError;
        }
    });
});

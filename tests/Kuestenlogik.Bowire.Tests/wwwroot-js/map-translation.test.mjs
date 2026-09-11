// #117 — the map widget reads the catalogue through the extension contract.
//
// The bug this is written against: the i18n sweep translated the map widget's
// nineteen strings with a bare `t(...)`, the same call every other fragment
// makes. But map.js does not share the workbench IIFE — it is served from the
// extension-asset endpoint as its own <script>, so `t` was never in its scope.
// Every mount threw ReferenceError before the widget drew anything, and
// nothing in the suite noticed: the file parses, all nineteen keys are in the
// catalogue, and the guard that checks each key has a caller was satisfied by
// exactly the calls that could not run.
//
// `_untranslated.mjs` now derives the outside-the-IIFE package list from
// BowireHtmlGenerator, and locales.test.mjs fails a bundle there that calls
// t() without binding it. That is the structural half. This is the behavioural
// half: the label a person actually reads comes from the catalogue the core
// holds, and it follows a language change rather than freezing at load.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const SRC = '../../../src/Kuestenlogik.Bowire.Map/wwwroot/js/widgets/map.js';

/** The minimum DOM the bundle touches on its way to registering. */
function makeDocument() {
    const head = {
        appendChild(c) { return c; },
    };
    return {
        head, currentScript: null,
        createElement: () => ({ style: {}, id: '', textContent: '', appendChild() {} }),
        getElementById() { return null; },
        querySelectorAll() { return []; },
        addEventListener() {}, removeEventListener() {},
        documentElement: { getAttribute() { return 'dark'; } },
    };
}

/** The minimum window map.js needs to reach its registration call. */
function load({ translator } = {}) {
    let registered = null;
    const win = {
        __BOWIRE_CONFIG__: { mapBasemap: 'none' },
        __bowireExtFramework: { register(s) { registered = s; }, markBuiltIn() {} },
    };
    if (translator) win.BowireExtensions = { t: translator };
    compileFragment(SRC, ['window', 'document'], '')({
        window: win,
        document: makeDocument(),
    });
    return registered;
}

describe('map widget — translation (#117)', () => {
    it('takes its labels from BowireExtensions.t', () => {
        const asked = [];
        const reg = load({
            translator: (key) => { asked.push(key); return 'DE:' + key; },
        });

        assert.equal(reg.viewer.label, 'DE:map.label');
        assert.equal(reg.editor.label, 'DE:map.pick');
        assert.ok(asked.includes('map.label'), 'the viewer label went through the catalogue');
    });

    it('follows a language change instead of freezing at load', () => {
        // Registration runs once, at bundle load. setLocale does not reload
        // the page, so a label resolved into the descriptor there would show
        // the boot language for the rest of the session — the reason both
        // labels are getters.
        let locale = 'en';
        const reg = load({ translator: (key) => locale + ':' + key });

        assert.equal(reg.viewer.label, 'en:map.label');
        locale = 'de';
        assert.equal(reg.viewer.label, 'de:map.label',
            'the label re-resolves; a plain value would still read en:');
    });

    it('renders key names rather than nothing when the core is older', () => {
        // A bundle newer than the core it is served by: the handle is absent,
        // and the widget still has to mount. Showing `map.label` is ugly and
        // self-explaining; throwing would take the whole response pane down.
        const reg = load();

        assert.equal(reg.viewer.label, 'map.label');
        assert.equal(reg.editor.label, 'map.pick');
    });
});

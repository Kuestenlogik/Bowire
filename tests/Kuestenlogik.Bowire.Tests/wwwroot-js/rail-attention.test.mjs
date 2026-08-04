// #185 — the Discover-rail attention dot.
//
// `_railModeAttention` is the entire rail-badge decision: pulse ONLY
// the Discover rail, ONLY outside embedded mode (embedded hides the
// statusbar, and with it the pill that clears the pulse — an
// uncloseable pulse is worse than none), and ONLY while the workspace
// change log has unread entries. It also owes the log a hydration
// kick, so the dot lights on a fresh boot without the pill having
// rendered first.
//
// Same standalone-fragment wrap as sidebar-collapsed-rows.test.mjs.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SIDEBAR = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/render-sidebar.js'),
    'utf8'
);

function load(opts) {
    opts = opts || {};
    const prelude = `
        var window = { __BOWIRE_CONFIG__: { rails: [] } };
        function el() { return { appendChild: function () {} }; }
        function svgIcon() { return ''; }
        var uiMode = ${JSON.stringify(opts.uiMode || 'standalone')};
        var _unread = ${JSON.stringify(opts.unread || 0)};
        var _hydrateKicks = 0;
        function schemaChangeUnreadCount() { return _unread; }
        function ensureSchemaChangeLogLoaded() { _hydrateKicks++; }
    `;
    const postlude = `
        return {
            _railModeAttention: _railModeAttention,
            _setUnread: function (n) { _unread = n; },
            _hydrateKicks: function () { return _hydrateKicks; }
        };
    `;
    return new Function(prelude + '\n' + SIDEBAR + '\n' + postlude)();
}

test('attention: only the Discover rail pulses, and only while unread changes exist', () => {
    const sb = load({ unread: 2 });
    assert.equal(sb._railModeAttention('discover'), true);
    assert.equal(sb._railModeAttention('recordings'), false, 'other rails never pulse');
    sb._setUnread(0);
    assert.equal(sb._railModeAttention('discover'), false, 'read log, no pulse');
});

test('attention: embedded mode never pulses — there is no pill to clear it with', () => {
    const sb = load({ uiMode: 'embedded', unread: 5 });
    assert.equal(sb._railModeAttention('discover'), false);
});

test('attention: the check kicks the one-shot log hydration', () => {
    // On a fresh boot the rail renders before the statusbar pill —
    // without this kick the dot could not light until something else
    // hydrated the log.
    const sb = load({ unread: 0 });
    sb._railModeAttention('discover');
    sb._railModeAttention('discover');
    assert.equal(sb._hydrateKicks(), 2, 'delegates to the (internally one-shot) hydration');
});

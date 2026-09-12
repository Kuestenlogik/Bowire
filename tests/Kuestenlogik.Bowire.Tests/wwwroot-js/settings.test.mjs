// v2.2 (#325 Workspace… sub-tree) — unit tests for the settings tab
// migration + tree-node builder.
//
// settings.js is a fragment that runs inside prologue.js's IIFE, so we
// can't `import` it. We surgically extract the two functions we need
// (_migrateLegacySettingsTab + _buildSettingsTreeNodes) and evaluate
// them in a minimal shim.
//
// The migration is a pure string → string function with no external
// deps other than reading a `tabId` string, so its shim is trivial.
// The tree builder reads a bunch of module-scope names + calls `leaf`
// + `header` + `isSettingsTreeNodeExpanded`; we stub those to observe
// the returned node shape.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { t } from './_load-fragment.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/settings.js'),
    'utf8'
);

// Extract a top-level `function name(...) { ... }` block from the
// fragment. The fragment is indented 4 spaces; the closing `}` is at
// column 4 (same indent as `function`). We rely on that convention to
// slice the source without a real parser.
function extractFunction(name) {
    const marker = '    function ' + name + '(';
    const start = SRC.indexOf(marker);
    if (start < 0) throw new Error('function not found: ' + name);
    // Walk forward finding the matching close-brace at the SAME column.
    // The function body ends with `\n    }` — 4 spaces + `}`.
    const closeMarker = '\n    }';
    const end = SRC.indexOf(closeMarker, start);
    if (end < 0) throw new Error('close-brace not found for: ' + name);
    return SRC.substring(start, end + closeMarker.length);
}

// Load `_migrateLegacySettingsTab` on its own — pure function.
function loadMigrate() {
    const body = extractFunction('_migrateLegacySettingsTab')
        + '\nreturn _migrateLegacySettingsTab;';
    return new Function(body)();
}

// Load `_buildSettingsTreeNodes` with a stubbed environment. Returns
// { build, setSettingsTab, setPluginCounts }.
function loadBuildTree() {
    const body = `
        var settingsTab = 'general';
        var installedPlugins = [];
        var installedExtensions = [];
        var settingsTreeExpanded = {};
        function isSettingsTreeNodeExpanded(key, defaultOpen) {
            if (settingsTreeExpanded[key] === undefined) return !!defaultOpen;
            return !!settingsTreeExpanded[key];
        }
        function toggleSettingsTreeNode() {}
        function renderSettingsDialog() {}
        ${extractFunction('_buildSettingsTreeNodes')}
        return {
            build: function () { return _buildSettingsTreeNodes(); },
            setSettingsTab: function (v) { settingsTab = v; },
            setPluginCounts: function (p, e) {
                installedPlugins = p || [];
                installedExtensions = e || [];
            }
        };
    `;
    // #117 — the tree labels come from the catalogue now. This harness builds
    // its own Function rather than going through compileFragment, so it has to
    // be handed the translator.
    return new Function('t', body)(t);
}

// ---- Migration ----

test('_migrateLegacySettingsTab: legacy "workspace" → "workspace-sources"', () => {
    const migrate = loadMigrate();
    // Regression guard for the Workspace… sub-tree expansion. Operators
    // with a saved deep-link to the old single "Workspace settings"
    // leaf should land on the first sub-page.
    assert.equal(migrate('workspace'), 'workspace-sources');
});

test('_migrateLegacySettingsTab: new workspace sub-page ids pass through', () => {
    const migrate = loadMigrate();
    assert.equal(migrate('workspace-sources'), 'workspace-sources');
    assert.equal(migrate('workspace-environments'), 'workspace-environments');
    assert.equal(migrate('workspace-overrides'), 'workspace-overrides');
    assert.equal(migrate('workspace-data'), 'workspace-data');
});

test('_migrateLegacySettingsTab: known machine-scope tabs pass through', () => {
    const migrate = loadMigrate();
    assert.equal(migrate('general'), 'general');
    assert.equal(migrate('rails'), 'rails');
    assert.equal(migrate('shortcuts'), 'shortcuts');
    assert.equal(migrate('data'), 'data');
    assert.equal(migrate('configure-protocols'), 'configure-protocols');
    assert.equal(migrate('configure-modules'), 'configure-modules');
});

test('_migrateLegacySettingsTab: legacy plugin aliases still redirect', () => {
    const migrate = loadMigrate();
    assert.equal(migrate('plugins'), 'configure-protocols');
    assert.equal(migrate('modules'), 'configure-modules');
    assert.equal(migrate('ai'), 'configure-modules');
    assert.equal(migrate('discovery'), 'configure-discovery');
    assert.equal(migrate('tools'), 'configure-tools');
    assert.equal(migrate('extensions'), 'configure-protocols');
});

test('_migrateLegacySettingsTab: unknown ids fall through to general', () => {
    const migrate = loadMigrate();
    assert.equal(migrate('bogus'), 'general');
    assert.equal(migrate(''), 'general');
    assert.equal(migrate(null), 'general');
    assert.equal(migrate(undefined), 'general');
});

test('_migrateLegacySettingsTab: no tree leaf silently falls back to General', () => {
    // 'general' is the default branch's answer for an id it does not know,
    // so a leaf missing from the switch renders the General page and reads
    // as a dead nav entry. #95 shipped exactly that for an afternoon.
    //
    // Deliberate aliases are fine (the expandable 'plugins' parent lands on
    // configure-protocols), which is why this asserts "not the fallback"
    // rather than "maps to itself", and only looks at leaves.
    const migrate = loadMigrate();
    const tree = loadBuildTree();
    tree.setSettingsTab('general');

    const leaves = [];
    (function walk(nodes) {
        nodes.forEach((n) => {
            if (Array.isArray(n.children) && n.children.length) { walk(n.children); return; }
            if (n.id && n.id.indexOf('settings:') === 0 && !n.header) {
                leaves.push(n.id.slice('settings:'.length));
            }
        });
    })(tree.build());

    assert.ok(leaves.length >= 8, `expected the tree to offer leaves, got ${leaves.length}`);
    for (const id of leaves) {
        if (id === 'general') continue;
        assert.notEqual(migrate(id), 'general',
            `settings tab '${id}' is in the tree but not in the migration switch, `
            + 'so clicking it renders the General page');
    }
});

test('_migrateLegacySettingsTab: dynamic plugin- / extension- ids are preserved', () => {
    const migrate = loadMigrate();
    assert.equal(migrate('plugin-rest'), 'plugin-rest');
    assert.equal(migrate('extension-map'), 'extension-map');
});

// ---- Tree structure ----

test('_buildSettingsTreeNodes: Workspace… header sits above an expandable parent with 5 children', () => {
    const tree = loadBuildTree();
    tree.setSettingsTab('workspace-sources');
    const nodes = tree.build();

    // Locate the Workspace… header + the parent that follows.
    const headerIdx = nodes.findIndex(n => n.header && /Workspace/.test(n.label));
    assert.ok(headerIdx >= 0, 'Workspace… header must exist');

    const parent = nodes[headerIdx + 1];
    assert.ok(parent, 'parent node must exist after the Workspace… header');
    assert.equal(parent.id, 'settings:workspace');
    assert.equal(parent.expandable, true);
    assert.ok(Array.isArray(parent.children));

    const childIds = parent.children.map(c => c.id);
    assert.deepEqual(childIds.sort(), [
        'settings:workspace-data',
        'settings:workspace-environments',
        // #95 - the header library is workspace-owned, so it lives beside
        // Environments rather than under System settings.
        'settings:workspace-headers',
        'settings:workspace-overrides',
        'settings:workspace-sources'
    ]);
});

test('_buildSettingsTreeNodes: parent expands when a workspace-* tab is active', () => {
    const tree = loadBuildTree();
    tree.setSettingsTab('workspace-environments');
    const nodes = tree.build();
    const parent = nodes.find(n => n.id === 'settings:workspace');
    assert.equal(parent.expanded, true);
});

test('_buildSettingsTreeNodes: child selection propagates via `selected`', () => {
    const tree = loadBuildTree();
    tree.setSettingsTab('workspace-overrides');
    const nodes = tree.build();
    const parent = nodes.find(n => n.id === 'settings:workspace');
    const overrides = parent.children.find(c => c.id === 'settings:workspace-overrides');
    assert.equal(overrides.selected, true);
    // Sibling non-selected.
    const sources = parent.children.find(c => c.id === 'settings:workspace-sources');
    assert.equal(sources.selected, false);
});

test('_buildSettingsTreeNodes: legacy tab "workspace" does NOT flag the parent as selected', () => {
    // The parent leaves `selected: false` at the object level; only the
    // relevant child gets `selected: true`. This guards against accidental
    // regression where clicking the parent would highlight it in addition
    // to the resolved child.
    const tree = loadBuildTree();
    tree.setSettingsTab('workspace-sources');
    const nodes = tree.build();
    const parent = nodes.find(n => n.id === 'settings:workspace');
    assert.equal(parent.selected, false);
});

// ---------------------------------------------------------------------------
// #691 — text the backend sends, with an optional catalogue key beside it.
//
// A plugin's setting labels, its descriptions and its one-line summary are
// written in C# and arrive over /api/protocols and /api/plugins already
// rendered in English. `backendText` is the whole of the contract on this
// side: prefer the key when the catalogue knows it, keep the text when it
// does not.
//
// The second half is the part that matters for third-party plugins. They have
// no entry in Bowire's catalogue and no way to add one, so whatever this does
// has to leave their English string working exactly as it did before.
// ---------------------------------------------------------------------------

function loadBackendText(catalogue) {
    const body = `
        function t(key) {
            return Object.prototype.hasOwnProperty.call(CATALOGUE, key)
                ? CATALOGUE[key] : key;
        }
        ${extractFunction('backendText')}
        return backendText;
    `;
    return new Function('CATALOGUE', body)(catalogue || {});
}

test('backendText: the catalogue entry wins over the English text', () => {
    const backendText = loadBackendText({
        'plugin.nats.scanDuration.label': 'Dauer der Betreffsuche',
    });
    assert.equal(
        backendText('Subject scan duration', 'plugin.nats.scanDuration.label'),
        'Dauer der Betreffsuche');
});

test('backendText: a plugin that sends no key keeps its own text', () => {
    // The third-party case. No key, no catalogue entry, no change.
    const backendText = loadBackendText({});
    assert.equal(backendText('Retry budget', undefined), 'Retry budget');
    assert.equal(backendText('Retry budget', null), 'Retry budget');
    assert.equal(backendText('Retry budget', ''), 'Retry budget');
});

test('backendText: a key the catalogue has never heard of falls back', () => {
    // A plugin built against a newer Bowire, or one whose key was renamed.
    // t() returns the key itself when it misses, and showing
    // "plugin.acme.retries.label" on screen would be worse than the English
    // the plugin already gave us.
    const backendText = loadBackendText({});
    assert.equal(
        backendText('Retry budget', 'plugin.acme.retries.label'),
        'Retry budget');
});

test('backendText: no text and no key renders empty, not "undefined"', () => {
    const backendText = loadBackendText({});
    assert.equal(backendText(undefined, undefined), '');
    assert.equal(backendText(null, null), '');
});

test('backendText: a key with no text behind it still translates', () => {
    // Description is optional on BowirePluginSetting; a plugin may send a key
    // and leave the text empty rather than repeating itself in English.
    const backendText = loadBackendText({ 'plugin.acme.retries.desc': 'Wie oft erneut' });
    assert.equal(backendText('', 'plugin.acme.retries.desc'), 'Wie oft erneut');
});

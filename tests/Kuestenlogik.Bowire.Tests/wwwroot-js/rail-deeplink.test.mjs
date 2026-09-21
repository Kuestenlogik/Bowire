// #735 — `?rail=<id>`, the deep link the docs advertised in three places
// and nothing read. What is worth testing is not the parsing but the three
// decisions it makes for somebody who was sent a link:
//   * a link wins over the rail the recipient happened to leave open,
//     otherwise a shared link does nothing the moment they have history;
//   * an id this build does not have is refused, and visibly — landing on
//     the last rail in silence reads as "this is where you were sent";
//   * a retired id is handed to the boot migration rather than mapped a
//     second time here, so a link and a stored value agree.
//
// The function lives in prologue.js, which cannot be imported — it is a
// fragment of the assembled bundle. Snipped out and run on its own, the
// way keyring-vars.test.mjs does it.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const PROLOGUE = resolve(
    __dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/prologue.js');
const SRC = readFileSync(PROLOGUE, 'utf8');

function extractFn(name) {
    const needle = `function ${name}(`;
    const start = SRC.indexOf(needle);
    assert.ok(start >= 0, `${needle} not found in prologue.js`);
    const open = SRC.indexOf('{', start);
    let depth = 0, i = open;
    for (; i < SRC.length; i++) {
        const c = SRC[i], n = SRC[i + 1];
        if (c === '/' && n === '/') { i = SRC.indexOf('\n', i); if (i < 0) i = SRC.length; continue; }
        if (c === '/' && n === '*') { i = SRC.indexOf('*/', i + 2) + 1; continue; }
        if (c === '"' || c === "'" || c === '`') {
            // No escape handling: the function snipped out here carries no
            // escaped quotes, and a scanner that guesses at them is a second
            // parser to maintain. A quote that did need escaping would end
            // the scan early and the extract would fail to compile — loudly,
            // not silently.
            const close = SRC.indexOf(c, i + 1);
            i = close < 0 ? SRC.length : close;
            continue;
        }
        if (c === '{') depth++;
        else if (c === '}') { depth--; if (depth === 0) { i++; break; } }
    }
    return SRC.slice(start, i);
}

/** The retired-id table, read out of the fragment rather than restated. */
function extractRetiredIds() {
    const at = SRC.indexOf('var _RETIRED_RAIL_IDS = [');
    assert.ok(at >= 0, '_RETIRED_RAIL_IDS not found in prologue.js');
    const end = SRC.indexOf('];', at);
    return SRC.slice(at, end + 2);
}

const resolveDeepLink = new Function(`
    ${extractRetiredIds()}
    ${extractFn('_railDeepLinkFrom')}
    return _railDeepLinkFrom;
`)();

const SHIPPED = ['home', 'discover', 'compose', 'intercept', 'workspaces', 'flows'];

test('no parameter leaves the stored rail alone', () => {
    const r = resolveDeepLink('', SHIPPED);
    assert.equal(r.status, 'none');
    assert.equal(r.id, null);
});

test('an unrelated query string is not a rail request', () => {
    const r = resolveDeepLink('?workspaceId=abc&theme=dark', SHIPPED);
    assert.equal(r.status, 'none');
    assert.equal(r.id, null);
});

test('a rail this build ships is adopted', () => {
    for (const id of SHIPPED) {
        const r = resolveDeepLink(`?rail=${id}`, SHIPPED);
        assert.equal(r.status, 'known', id);
        assert.equal(r.id, id);
    }
});

test('the parameter is found beside others, in either order', () => {
    assert.equal(resolveDeepLink('?theme=dark&rail=compose', SHIPPED).id, 'compose');
    assert.equal(resolveDeepLink('rail=compose&theme=dark', SHIPPED).id, 'compose');
});

test('an id this build does not have is refused, and names itself', () => {
    // Not mapped to the default: boot keeps the stored rail and init.js
    // says which id was asked for. The requested value travels so the
    // message can quote it.
    const r = resolveDeepLink('?rail=nonesuch', SHIPPED);
    assert.equal(r.status, 'unknown');
    assert.equal(r.id, null);
    assert.equal(r.requested, 'nonesuch');
});

test('`?rail=` with nothing after it is a typo, not the default', () => {
    const r = resolveDeepLink('?rail=', SHIPPED);
    assert.equal(r.status, 'unknown');
    assert.equal(r.id, null);
});

test('ids are case-sensitive, as the docs say', () => {
    // docs/features/rail-strip.md: "the rail's Id string (verbatim —
    // case-sensitive, no spaces)". 'Compose' is not a rail; saying so
    // beats guessing which one was meant.
    const r = resolveDeepLink('?rail=Compose', SHIPPED);
    assert.equal(r.status, 'unknown');
});

test('surrounding whitespace is trimmed rather than refused', () => {
    // '%20compose' is what a link pasted out of a chat message can carry.
    assert.equal(resolveDeepLink('?rail=%20compose%20', SHIPPED).id, 'compose');
});

test('a retired id is passed on for the boot migration, not mapped here', () => {
    // 'collections' was retired into Compose. Mapping it here too would be
    // a second table beside the migration chain, and the two would drift.
    const r = resolveDeepLink('?rail=collections', SHIPPED);
    assert.equal(r.status, 'retired');
    assert.equal(r.id, 'collections');
});

test('every id the boot migration rewrites is accepted as retired', () => {
    // The list in the test is the list in the fragment — read from it, so
    // a rail retired later without updating the table fails here.
    for (const id of ['sources', 'environments', 'collections',
                      'mocks', 'traffic', 'proxy', 'intercepted']) {
        assert.equal(resolveDeepLink(`?rail=${id}`, SHIPPED).status, 'retired', id);
    }
});

test('a build that ships no rail list refuses rather than throws', () => {
    // An embedded host can boot before __BOWIRE_CONFIG__.rails is filled;
    // a throw here would take the whole workbench down at boot.
    assert.equal(resolveDeepLink('?rail=compose', []).status, 'unknown');
    assert.equal(resolveDeepLink('?rail=compose', undefined).status, 'unknown');
});

test('the boot code adopts the deep link over the stored rail', () => {
    // Read off the source: the stored value is only consulted in the else
    // branch. Asserted here because it is the whole point of the ticket —
    // a shared link that loses to the recipient's history is no link.
    const boot = SRC.slice(SRC.indexOf('var _railDeepLink = _railDeepLinkFrom('));
    const head = boot.slice(0, boot.indexOf('// Boot migration'));
    assert.match(head, /if \(_railDeepLink\.id\) \{[\s\S]*railMode = _railDeepLink\.id;/);
    assert.match(head, /localStorage\.setItem\('bowire_rail_mode', railMode\)/);
    const stored = head.indexOf("localStorage.getItem('bowire_rail_mode')");
    assert.ok(stored > head.indexOf('} else {'),
        'the stored rail must only be read when no deep link applies');
});

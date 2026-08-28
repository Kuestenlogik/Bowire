// #97 — the one-time offer to copy a single-user install's data into the
// signed-in identity's slot.
//
// What is worth testing here is not the markup. It is the three things that
// decide whether somebody keeps their work:
//   * the offer appears only in the one state that has something to decide;
//   * accepting reloads, because every store already read an empty slot;
//   * a failed accept leaves the offer standing, because an accept that
//     vanished silently looks exactly like one that worked.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment, readFragment } from './_load-fragment.mjs';

const _RELPATH = '../../../src/Kuestenlogik.Bowire/wwwroot/js/user-migration.js';

function load(responder) {
    const appended = `
        function _mkNode(tag) {
            var n = {
                tag: tag, children: [], style: {}, className: '', textContent: '',
                disabled: false, parentNode: null,
                appendChild: function (c) { if (c) { c.parentNode = n; n.children.push(c); } return c; },
                removeChild: function (c) {
                    var i = n.children.indexOf(c);
                    if (i >= 0) { n.children.splice(i, 1); c.parentNode = null; }
                    return c;
                },
                _walk: function (hit, into) {
                    for (var i = 0; i < n.children.length; i++) {
                        var c = n.children[i];
                        if (hit(c)) into.push(c);
                        if (c._walk) c._walk(hit, into);
                    }
                    return into;
                },
                querySelectorAll: function (sel) {
                    return n._walk(function (c) { return c.tag === sel; }, []);
                },
                querySelector: function (sel) {
                    var cls = sel.replace('.', '');
                    var hits = n._walk(function (c) {
                        return String(c.className || '').split(' ').indexOf(cls) >= 0;
                    }, []);
                    return hits.length ? hits[0] : null;
                }
            };
            return n;
        }
        function el(tag, props) {
            var n = _mkNode(tag);
            if (props) { for (var k in props) { n[k] = props[k]; } }
            // A string child becomes a text node, as it does in the real el() —
            // otherwise the sentence the person actually reads is invisible to
            // the test that checks what it says.
            function _child(c) {
                if (Array.isArray(c)) { for (var j = 0; j < c.length; j++) _child(c[j]); }
                else if (typeof c === 'string') { var t = _mkNode('#text'); t.textContent = c; n.appendChild(t); }
                else if (c) n.appendChild(c);
            }
            for (var i = 2; i < arguments.length; i++) _child(arguments[i]);
            return n;
        }
        var config = { prefix: '' };
        var document = { body: _mkNode('body') };
        var reloads = 0;
        var window = {
            addEventListener: function () {},
            location: { reload: function () { reloads++; } }
        };
        var calls = [];
        function fetch(url, init) {
            calls.push({ url: url, method: (init && init.method) || 'GET' });
            return _responder(url, init);
        }
        return {
            size: userMigrationSize,
            offer: fetchUserMigrationOffer,
            body: document.body,
            calls: calls,
            reloads: function () { return reloads; },
            current: function () { return userMigrationOffer; }
        };
    `;
    return compileFragment(_RELPATH, ['_responder'], appended)({ _responder: responder });
}

/** A fetch stub: GET returns `plan`, POST returns `post` (or fails). */
function responder(plan, post) {
    return function (url, init) {
        if (!init || init.method !== 'POST') {
            return Promise.resolve({ ok: true, json: () => Promise.resolve(plan) });
        }
        if (post === 'fail') return Promise.resolve({ ok: false, status: 500 });
        return Promise.resolve({ ok: true, json: () => Promise.resolve(post || {}) });
    };
}

const AVAILABLE = {
    state: 'Available',
    files: 12,
    bytes: 4096,
    source: '/home/ada/.bowire',
    slot: '/home/ada/.bowire/users/ada-4f2a1c07',
};

const settle = () => new Promise((r) => setTimeout(r, 0));

/** All the text in a rendered node, in order. */
function text(node) {
    return [node.textContent || '', node.title || '']
        .concat((node.children || []).map(text))
        .join(' ');
}

// ---- when the question gets asked ----

test('the offer appears when there is something to decide', async () => {
    const f = load(responder(AVAILABLE));
    f.offer();
    await settle();

    assert.equal(f.body.children.length, 1);
    assert.ok(f.current(), 'the fragment kept a handle on what it rendered');
});

for (const state of ['Off', 'NothingToMigrate', 'AlreadyDecided', 'SlotNotEmpty', 'Disabled']) {
    test(`nothing is shown for ${state}`, async () => {
        // Each of these is a state with nothing to ask about. A dialog that
        // appears anyway is a dialog people learn to dismiss unread — which
        // is precisely how the one that matters gets dismissed too.
        const f = load(responder({ state }));
        f.offer();
        await settle();

        assert.equal(f.body.children.length, 0);
    });
}

test('an install that answers nothing at all is left alone', async () => {
    const f = load(() => Promise.resolve({ ok: false, status: 404 }));
    f.offer();
    await settle();

    assert.equal(f.body.children.length, 0);
});

test('the offer says how much is at stake', async () => {
    const f = load(responder(AVAILABLE));
    f.offer();
    await settle();

    const said = text(f.body.children[0]);
    assert.match(said, /12 files/);
    assert.match(said, /4\.0 KB/);
    assert.match(said, /\/home\/ada\/\.bowire/);
});

// ---- deciding ----

test('accepting copies and then reloads', async () => {
    // Every store read its empty slot while the page was loading. Re-reading
    // them one at a time is a list that goes stale; reloading is not.
    const f = load(responder(AVAILABLE, { outcome: 'Migrated' }));
    f.offer();
    await settle();

    f.body.children[0].querySelector('.bowire-migration-go').onClick();
    await settle();

    assert.deepEqual(
        f.calls.map((c) => c.method + ' ' + c.url),
        ['GET /api/migration', 'POST /api/migration/accept']);
    assert.equal(f.reloads(), 1);
});

test('declining is recorded and the offer goes away', async () => {
    const f = load(responder(AVAILABLE, { outcome: 'Declined' }));
    f.offer();
    await settle();

    const dialog = f.body.children[0];
    dialog.querySelectorAll('button')[0].onClick();
    await settle();

    assert.equal(f.calls[1].url, '/api/migration/decline');
    assert.equal(f.body.children.length, 0);
    assert.equal(f.current(), null);
});

test('a failed accept leaves the offer standing and says so', async () => {
    // The failure mode this exists for: the dialog disappears, the person
    // assumes it worked, and finds out in a week that it did not.
    const f = load(responder(AVAILABLE, 'fail'));
    f.offer();
    await settle();

    const dialog = f.body.children[0];
    dialog.querySelector('.bowire-migration-go').onClick();
    await settle();

    assert.equal(f.body.children.length, 1, 'the offer is still on screen');
    assert.match(dialog.querySelector('.bowire-migration-error').textContent, /untouched/);
    assert.equal(
        dialog.querySelectorAll('button').filter((b) => b.disabled).length, 0,
        'the buttons are usable again');
});

test('the buttons are locked while the decision is in flight', async () => {
    let release;
    const held = new Promise((r) => { release = r; });
    const f = load((url, init) => (init && init.method === 'POST')
        ? held.then(() => ({ ok: true, json: () => Promise.resolve({}) }))
        : Promise.resolve({ ok: true, json: () => Promise.resolve(AVAILABLE) }));

    f.offer();
    await settle();
    const dialog = f.body.children[0];
    dialog.querySelector('.bowire-migration-go').onClick();

    assert.ok(dialog.querySelectorAll('button').every((b) => b.disabled),
        'a second click would decide twice');
    release();
});

// ---- the size the person reads ----

test('sizes are rounded to something a person can act on', () => {
    const { size } = load(responder(AVAILABLE));

    assert.equal(size(0), '');
    assert.equal(size(900), '900 bytes');
    assert.equal(size(2048), '2.0 KB');
    assert.equal(size(1536 * 1024), '1.5 MB');
    assert.equal(size(40 * 1024 * 1024), '40 MB');
});

// ---- how it starts ----

test('the offer is fetched once the page has loaded, not at parse time', () => {
    // `config.prefix` is populated by the bootstrap script, so a fetch fired
    // while the bundle is still being parsed goes to the wrong prefix.
    const src = readFragment(_RELPATH);

    assert.match(src, /addEventListener\('load'/);
    assert.doesNotMatch(src, /^\s*fetchUserMigrationOffer\(\);/m);
});

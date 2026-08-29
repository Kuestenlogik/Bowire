// #98 — the account chip.
//
// The thing worth pinning is not the markup. It is that a single-user install
// renders nothing at all — no placeholder, no empty circle — and that a
// multi-tenant one never shows a raw subject where a person expects a name.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const _RELPATH = '../../../src/Kuestenlogik.Bowire/wwwroot/js/user-chip.js';

function load(me) {
    const appended = `
        function _mkNode(tag) {
            var n = {
                tag: tag, children: [], style: {}, className: '', textContent: '', src: '',
                href: '', parentNode: null,
                appendChild: function (c) { if (c) { c.parentNode = n; n.children.push(c); } return c; },
                _walk: function (hit, into) {
                    for (var i = 0; i < n.children.length; i++) {
                        var c = n.children[i];
                        if (hit(c)) into.push(c);
                        if (c._walk) c._walk(hit, into);
                    }
                    return into;
                },
                querySelectorAll: function (sel) {
                    var cls = sel.replace('.', '');
                    return n._walk(function (c) {
                        return c.tag === sel
                            || String(c.className || '').split(' ').indexOf(cls) >= 0;
                    }, []);
                },
                querySelector: function (sel) {
                    var hits = n.querySelectorAll(sel);
                    return hits.length ? hits[0] : null;
                }
            };
            return n;
        }
        function el(tag, props) {
            var n = _mkNode(tag);
            if (props) { for (var k in props) { n[k] = props[k]; } }
            function _child(c) {
                if (Array.isArray(c)) { for (var j = 0; j < c.length; j++) _child(c[j]); }
                else if (typeof c === 'string') { var t = _mkNode('#text'); t.textContent = c; n.appendChild(t); }
                else if (c) n.appendChild(c);
            }
            for (var i = 2; i < arguments.length; i++) _child(arguments[i]);
            return n;
        }
        var config = { prefix: '' };
        var renders = 0;
        function render() { renders++; }
        var window = { addEventListener: function () {} };
        var calls = [];
        function fetch(url) {
            calls.push(url);
            return _me === null
                ? Promise.resolve({ ok: false, status: 404 })
                : Promise.resolve({ ok: true, json: function () { return Promise.resolve(_me); } });
        }
        return {
            load: fetchBowireIdentity,
            chip: renderUserChip,
            multiTenant: isMultiTenant,
            name: userChipName,
            calls: calls,
            renders: function () { return renders; }
        };
    `;
    return compileFragment(_RELPATH, ['_me'], appended)({ _me: me });
}

const settle = () => new Promise((r) => setTimeout(r, 0));

/** All the text in a rendered node, in order. */
function text(node) {
    if (!node) return '';
    return [node.textContent || '']
        .concat((node.children || []).map(text))
        .join(' ');
}

const ADA = {
    multiTenant: true,
    subject: '8f14e45f',
    displayName: 'Ada Lovelace',
    email: 'ada@example.com',
    isAdmin: false,
    initials: 'AL',
};

async function ready(me) {
    const f = load(me);
    f.load();
    await settle();
    return f;
}

// ---- when there is nobody to identify ----

test('a single-user install renders no chip at all', async () => {
    // Not an empty circle, not a placeholder. There is genuinely nobody to
    // name, and a control that says otherwise misdescribes the deployment.
    const f = await ready({ multiTenant: false });

    assert.equal(f.chip(), null);
    assert.equal(f.multiTenant(), false);
});

test('nothing is rendered before the server has answered', () => {
    const f = load(ADA);

    assert.equal(f.chip(), null);
});

test('an install that answers nothing is treated as single-user', async () => {
    const f = await ready(null);

    assert.equal(f.chip(), null);
});

test('a single-user answer does not cost a re-render', async () => {
    const f = await ready({ multiTenant: false });

    assert.equal(f.renders(), 0);
});

test('a multi-tenant answer does trigger one', async () => {
    const f = await ready(ADA);

    assert.equal(f.renders(), 1);
});

// ---- naming the person ----

test('the chip shows the name the identity provider gave', async () => {
    const f = await ready(ADA);

    assert.match(text(f.chip()), /Ada Lovelace/);
});

test('without a name it falls back to the e-mail address', async () => {
    const f = await ready({ ...ADA, displayName: null });

    assert.equal(f.name(), 'ada@example.com');
});

test('the raw subject is the last resort, not the first', async () => {
    // For most providers the subject is a GUID. Showing it where a name was
    // expected reads as a bug rather than as a missing claim, so it only
    // appears when there is nothing else at all.
    const withEmail = await ready({ ...ADA, displayName: null });
    const withNeither = await ready({ ...ADA, displayName: null, email: null });

    assert.notEqual(withEmail.name(), '8f14e45f');
    assert.equal(withNeither.name(), '8f14e45f');
});

test('initials stand in for a missing picture', async () => {
    const f = await ready(ADA);

    const avatar = f.chip().querySelector('.bowire-user-chip-initials');
    assert.ok(avatar, 'no initials avatar');
    assert.equal(avatar.textContent, 'AL');
});

test('a picture from the provider is used when there is one', async () => {
    const f = await ready({ ...ADA, picture: 'https://example.com/ada.png' });

    const img = f.chip().querySelector('img');
    assert.ok(img, 'no image');
    assert.equal(img.src, 'https://example.com/ada.png');
});

// ---- the popover ----

test('the popover appears on click and says who and what', async () => {
    const f = await ready({ ...ADA, isAdmin: true });

    f.chip().onClick({ stopPropagation() {} });
    const opened = text(f.chip());

    assert.match(opened, /Ada Lovelace/);
    assert.match(opened, /ada@example\.com/);
    assert.match(opened, /Administrator/);
});

test('somebody without the admin role is a member, not an administrator', async () => {
    const f = await ready(ADA);

    f.chip().onClick({ stopPropagation() {} });

    assert.match(text(f.chip()), /Member/);
    assert.doesNotMatch(text(f.chip()), /Administrator/);
});

test('the popover says the work is stored separately', async () => {
    // The whole reason somebody looks at this chip: are these recordings
    // mine, and can anyone else see them.
    const f = await ready(ADA);

    f.chip().onClick({ stopPropagation() {} });

    assert.match(text(f.chip()), /stored separately/);
});

test('sign out is offered only when there is somewhere to send them', async () => {
    // A link that clears nothing teaches people to believe they signed out
    // when they did not.
    const without = await ready(ADA);
    without.chip().onClick({ stopPropagation() {} });
    assert.equal(without.chip().querySelector('.bowire-user-chip-signout'), null);

    const withUrl = await ready({ ...ADA, signOutUrl: 'https://idp.example.com/logout' });
    withUrl.chip().onClick({ stopPropagation() {} });
    const link = withUrl.chip().querySelector('.bowire-user-chip-signout');
    assert.ok(link, 'no sign-out link');
    assert.equal(link.href, 'https://idp.example.com/logout');
});

// ---- how it starts ----

test('the identity is asked for once, from the workbench prefix', async () => {
    const f = await ready(ADA);

    assert.deepEqual(f.calls, ['/api/me']);
});

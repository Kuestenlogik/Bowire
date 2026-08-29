// #98 — the account chip, and acting on somebody else's behalf.
//
// The things worth pinning are not the markup. They are that a single-user
// install renders nothing at all, that a multi-tenant one never shows a raw
// subject where a person expects a name, and that an administrator can always
// see whose workbench they are looking at and get back out of it.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const _RELPATH = '../../../src/Kuestenlogik.Bowire/wwwroot/js/user-chip.js';

function load(me, extra) {
    const appended = `
        function _mkNode(tag) {
            var n = {
                tag: tag, children: [], style: {}, className: '', textContent: '', src: '',
                href: '', parentNode: null,
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
        var _body = _mkNode('body');
        var document = {
            body: _body,
            querySelector: function (sel) { return _body.querySelector(sel); }
        };
        var reloads = 0;
        var window = {
            addEventListener: function () {},
            location: { reload: function () { reloads++; } }
        };
        var calls = [];
        function fetch(url, init) {
            calls.push({ url: url, method: (init && init.method) || 'GET' });
            return _routes(url, init);
        }
        return {
            load: fetchBowireIdentity,
            chip: renderUserChip,
            banner: renderImpersonationBanner,
            openPicker: openUserPicker,
            picker: function () { return _body.querySelector('.bowire-user-picker'); },
            body: _body,
            multiTenant: isMultiTenant,
            name: userChipName,
            label: ownedLabel,
            empty: ownedEmpty,
            calls: calls,
            renders: function () { return renders; },
            reloads: function () { return reloads; }
        };
    `;

    const routes = (url, init) => {
        const method = (init && init.method) || 'GET';
        if (url.indexOf('/api/me') === 0) {
            return me === null
                ? Promise.resolve({ ok: false, status: 404 })
                : Promise.resolve({ ok: true, json: () => Promise.resolve(me) });
        }
        if (url.indexOf('/api/users') === 0) {
            const users = (extra && extra.users) || [];
            return users === 'fail'
                ? Promise.resolve({ ok: false, status: 500 })
                : Promise.resolve({ ok: true, json: () => Promise.resolve(users) });
        }
        if (url.indexOf('/api/impersonation') === 0) {
            const outcome = extra && extra[method === 'POST' ? 'begin' : 'end'];
            return outcome === 'fail'
                ? Promise.resolve({ ok: false, status: 403 })
                : Promise.resolve({ ok: true, json: () => Promise.resolve({}) });
        }
        return Promise.resolve({ ok: false, status: 404 });
    };

    return compileFragment(_RELPATH, ['_routes'], appended)({ _routes: routes });
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

const ADMIN = { ...ADA, displayName: 'Grace Hopper', email: 'grace@example.com', isAdmin: true };

async function ready(me, extra) {
    const f = load(me, extra);
    f.load();
    await settle();
    return f;
}

function open(f) {
    f.chip().onClick({ stopPropagation() {} });
    return f.chip();
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
    assert.equal(load(ADA).chip(), null);
});

test('an install that answers nothing is treated as single-user', async () => {
    assert.equal((await ready(null)).chip(), null);
});

test('a single-user answer does not cost a re-render', async () => {
    assert.equal((await ready({ multiTenant: false })).renders(), 0);
});

test('a multi-tenant answer does trigger one', async () => {
    assert.equal((await ready(ADA)).renders(), 1);
});

// ---- naming the person ----

test('the chip shows the name the identity provider gave', async () => {
    assert.match(text((await ready(ADA)).chip()), /Ada Lovelace/);
});

test('without a name it falls back to the e-mail address', async () => {
    assert.equal((await ready({ ...ADA, displayName: null })).name(), 'ada@example.com');
});

test('the raw subject is the last resort, not the first', async () => {
    // For most providers the subject is a GUID. Showing it where a name was
    // expected reads as a bug rather than as a missing claim.
    const withEmail = await ready({ ...ADA, displayName: null });
    const withNeither = await ready({ ...ADA, displayName: null, email: null });

    assert.notEqual(withEmail.name(), '8f14e45f');
    assert.equal(withNeither.name(), '8f14e45f');
});

test('initials stand in for a missing picture', async () => {
    const avatar = (await ready(ADA)).chip().querySelector('.bowire-user-chip-initials');

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
    const opened = text(open(await ready(ADMIN)));

    assert.match(opened, /Grace Hopper/);
    assert.match(opened, /grace@example\.com/);
    assert.match(opened, /Administrator/);
});

test('somebody without the admin role is a member, not an administrator', async () => {
    const opened = text(open(await ready(ADA)));

    assert.match(opened, /Member/);
    assert.doesNotMatch(opened, /Administrator/);
});

test('the popover says the work is stored separately', async () => {
    assert.match(text(open(await ready(ADA))), /stored separately/);
});

test('sign out is offered only when there is somewhere to send them', async () => {
    // A link that clears nothing teaches people to believe they signed out
    // when they did not.
    assert.equal(open(await ready(ADA)).querySelector('.bowire-user-chip-signout'), null);

    const link = open(await ready({ ...ADA, signOutUrl: 'https://idp.example.com/logout' }))
        .querySelector('.bowire-user-chip-signout');
    assert.ok(link, 'no sign-out link');
    assert.equal(link.href, 'https://idp.example.com/logout');
});

test('the identity is asked for once, from the workbench prefix', async () => {
    assert.deepEqual((await ready(ADA)).calls.map((c) => c.url), ['/api/me']);
});

// ---- acting as somebody else ----

test('only an administrator is offered the switch', async () => {
    assert.equal(open(await ready(ADA)).querySelector('.bowire-user-chip-switch'), null);
    assert.ok(open(await ready(ADMIN)).querySelector('.bowire-user-chip-switch'));
});

test('an administrator already in a session is not offered a second hop', async () => {
    // Two levels of "acting as" is how somebody loses track of whose
    // workbench they are looking at. Getting out is the banner's job.
    const f = await ready({ ...ADMIN, actingAs: { subject: 'x', displayName: 'Ada Lovelace' } });

    assert.equal(open(f).querySelector('.bowire-user-chip-switch'), null);
});

test('no banner while an administrator is themselves', async () => {
    assert.equal((await ready(ADMIN)).banner(), null);
    assert.equal((await ready(ADA)).banner(), null);
});

test('the banner names whose workbench this is, and what it costs', async () => {
    const f = await ready({
        ...ADMIN,
        actingAs: { subject: 'x', displayName: 'Ada Lovelace', email: 'ada@example.com' },
    });
    const said = text(f.banner());

    assert.match(said, /Viewing as/);
    assert.match(said, /Ada Lovelace/);
    assert.match(said, /ada@example\.com/);
    // The part an administrator needs to know before they touch anything.
    assert.match(said, /recorded against your own account/);
});

test('ending the session tells the server and reloads', async () => {
    // Every store read the other person's slot while the page was up, so
    // re-reading them one at a time is a list that goes stale.
    const f = await ready({ ...ADMIN, actingAs: { subject: 'x', displayName: 'Ada' } });

    f.banner().querySelector('.bowire-impersonation-end').onClick();
    await settle();

    assert.deepEqual(f.calls[1], { url: '/api/impersonation', method: 'DELETE' });
    assert.equal(f.reloads(), 1);
});

test('a failed end still returns them to their own workbench', async () => {
    // Leaving the page as it was would leave somebody believing they had
    // returned when they had not.
    const f = await ready({ ...ADMIN, actingAs: { subject: 'x', displayName: 'Ada' } },
        { end: 'fail' });

    f.banner().querySelector('.bowire-impersonation-end').onClick();
    await settle();

    assert.equal(f.reloads(), 1);
});

// ---- the picker ----

test('opening the picker asks the server who else there is', async () => {
    const f = await ready(ADMIN, { users: [{ subject: 'x', displayName: 'Ada Lovelace' }] });

    f.openPicker();
    await settle();

    assert.ok(f.calls.some((c) => c.url.indexOf('/api/users') === 0), 'never asked');
    assert.match(text(f.picker()), /Ada Lovelace/);
});

test('the picker says what a session will cost before it starts one', async () => {
    const f = await ready(ADMIN, { users: [] });

    f.openPicker();
    await settle();

    assert.match(text(f.picker()), /recorded against your own account/);
});

test('an install with no directory says so rather than showing no results', async () => {
    // "No results" reads as a search that found nothing. This is a feature
    // that cannot work here, and the difference is worth a sentence.
    const f = await ready(ADMIN, { users: [] });

    f.openPicker();
    await settle();

    assert.match(text(f.picker()), /no directory listing other identities/);
});

test('picking somebody starts the session and reloads', async () => {
    const f = await ready(ADMIN, { users: [{ subject: 'ada-subject', displayName: 'Ada' }] });

    f.openPicker();
    await settle();
    f.picker().querySelector('.bowire-user-picker-row').onClick();
    await settle();

    const post = f.calls.find((c) => c.method === 'POST');
    assert.ok(post, 'no POST');
    assert.equal(post.url, '/api/impersonation');
    assert.equal(f.reloads(), 1);
});

test('a refused session leaves the picker open and says nothing changed', async () => {
    // A picker that closed on failure looks exactly like one that worked.
    const f = await ready(ADMIN,
        { users: [{ subject: 'ada-subject', displayName: 'Ada' }], begin: 'fail' });

    f.openPicker();
    await settle();
    f.picker().querySelector('.bowire-user-picker-row').onClick();
    await settle();

    assert.equal(f.reloads(), 0);
    assert.ok(f.picker(), 'the picker went away');
    assert.match(text(f.picker()), /Nothing changed/);
});

test('cancelling takes the picker away without starting anything', async () => {
    const f = await ready(ADMIN, { users: [{ subject: 'x', displayName: 'Ada' }] });

    f.openPicker();
    await settle();
    f.picker().querySelector('.bowire-btn').onClick();

    assert.equal(f.picker(), null);
    assert.equal(f.calls.filter((c) => c.method === 'POST').length, 0);
});

// ---- whose work is this ----

test('a shared instance says the lists are yours', async () => {
    // The question somebody actually has when a list of recordings appears on
    // a machine other people also use.
    const f = await ready(ADA);

    assert.equal(f.label('Recordings'), 'Your recordings');
    assert.equal(f.label('Collections'), 'Your collections');
});

test('a single-user install leaves the wording alone', async () => {
    // There is nobody to distinguish from, so "Your" would be noise.
    const f = await ready({ multiTenant: false });

    assert.equal(f.label('Recordings'), 'Recordings');
    assert.equal(f.empty('Recordings'), 'No recordings yet');
});

test('an empty account reads differently from an empty server', async () => {
    // "No recordings yet" on a shared instance leaves somebody wondering
    // whether they are looking at nothing of theirs or nothing at all.
    const shared = await ready(ADA);
    const alone = await ready({ multiTenant: false });

    assert.equal(shared.empty('Environments'), 'You have no environments yet');
    assert.equal(alone.empty('Environments'), 'No environments yet');
});

test('the wording is unknown until the server has answered', () => {
    // Before /api/me lands, the safe assumption is the one that changes
    // nothing: a chip flashing "Your" into a single-user workbench would be a
    // claim about the deployment nobody checked.
    const f = load(ADA);

    assert.equal(f.label('Recordings'), 'Recordings');
});

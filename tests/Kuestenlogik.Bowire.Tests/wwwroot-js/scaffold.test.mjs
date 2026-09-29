// #177 — the scaffold dialog's hand-over to the workspace.
//
// The generator writes the collection as JSON with its own ids; the dialog
// adds it through createCollection / addToCollection so it persists and
// syncs like any other collection. These tests hold that the ids from the
// file do not leak into the workspace and that every request arrives.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const _host = `
        var created = [];
        var added = [];
        function createCollection(name) { var c = { id: 'col_new', name: name }; created.push(c); return c; }
        function addToCollection(id, item) { added.push({ id: id, item: item }); }
        var window = {};
        return {
            addCollection: _scaffoldAddCollection,
            created: function () { return created; },
            added: function () { return added; }
        };
`;

const load = compileFragment('../../../src/Kuestenlogik.Bowire.Scaffold/wwwroot/js/scaffold.js', [], _host);

const collectionFile = {
    path: 'bowire/users.collection.json',
    kind: 'collection',
    content: JSON.stringify({
        id: 'col_scaffold_users',
        name: 'Users (scaffolded)',
        createdAt: 0,
        items: [
            { id: 'ci_scaffold_users_list', protocol: 'rest', service: 'users', method: 'listUsers', body: '{}' },
            { id: 'ci_scaffold_users_create', protocol: 'rest', service: 'users', method: 'createUser', body: '{"email":"ada@example.com"}' }
        ]
    })
};

test('the generated collection lands through the sidebar calls, without the file ids', () => {
    const f = load();
    const col = f.addCollection(collectionFile);
    assert.equal(col.id, 'col_new');
    assert.equal(f.created()[0].name, 'Users (scaffolded)');
    const added = f.added();
    assert.equal(added.length, 2);
    assert.ok(added.every((a) => a.id === 'col_new'));
    assert.ok(added.every((a) => !('id' in a.item)));
    assert.equal(added[1].item.method, 'createUser');
});

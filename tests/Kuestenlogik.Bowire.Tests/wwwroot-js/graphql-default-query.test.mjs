// The workbench's own default GraphQL operation, built in protocols.js for
// the query pane. It mirrors the server-side GraphQLQueryBuilder, and has to
// agree with it on the two things a server validates: the variable types it
// declares and whether a field takes a selection set at all.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const src = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/protocols.js'), 'utf8');

/** The GraphQL helpers at the top of protocols.js, up to getGraphQLQuery. */
function load(service, method) {
    const end = src.indexOf('function wrapAsGraphQLRequest');
    assert.ok(end > 0, 'wrapAsGraphQLRequest not found in protocols.js');
    return new Function('selectedService', 'selectedMethod', `
        var graphqlQueryOverrides = {};
        ${src.slice(0, end)}
        return { buildGraphQLDefaultQuery };
    `)(service, method);
}

const queryService = { name: 'Query', source: 'graphql' };

test('a root field that returns a scalar gets no selection set', () => {
    const method = {
        name: 'hello',
        inputType: { fields: [] },
        outputType: { name: 'String', fullName: 'graphql.Leaf', fields: [] },
    };
    const query = load(queryService, method).buildGraphQLDefaultQuery();
    assert.equal(query, 'query hello {\n  hello\n}');
});

test('variables are declared with the schema\'s own type names', () => {
    const method = {
        name: 'user',
        inputType: {
            fields: [
                { name: 'id', type: 'string', schemaType: 'ID', required: true },
                { name: 'role', type: 'string', schemaType: 'Role' },
                { name: 'tags', type: 'string', schemaType: 'ID', isRepeated: true },
                { name: 'limit', type: 'int32' },
            ],
        },
        outputType: { name: 'User', fullName: 'User', fields: [{ name: 'id', type: 'string' }] },
    };
    const query = load(queryService, method).buildGraphQLDefaultQuery();
    assert.match(query, /^query user\(\$id: ID!, \$role: Role, \$tags: \[ID!\], \$limit: Int\) \{/);
    assert.match(query, /user\(id: \$id, role: \$role, tags: \$tags, limit: \$limit\) \{\n {4}id\n {2}\}/);
});

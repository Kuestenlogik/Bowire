// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// scripts/ci/probe-mcp-image.mjs — #670, the MCP image keeps answering.
//
// Glama starts the image from the root Dockerfile and checks it answers introspection;
// awesome-mcp-servers lists only servers Glama has scored. The probe reads what the container
// said on stdout and decides whether a directory would still accept it.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const { probeRequests, checkProbe } = await import(pathToFileURL(resolve(__dirname, '../../../scripts/ci/probe-mcp-image.mjs')).href);

// From 2.10 on a release advertises the tasks extension and lists bowire.test.run (#802).
const isTasksRelease = version => { const [a, b] = version.split('.').map(Number); return a > 2 || (a === 2 && b >= 10); };
const initialize = (version = '2.9.0.0', { tasks = isTasksRelease(version) } = {}) => JSON.stringify({
    jsonrpc: '2.0', id: 1,
    result: {
        protocolVersion: '2025-03-26',
        capabilities: tasks ? { tools: {}, extensions: { 'io.modelcontextprotocol/tasks': {} } } : { tools: {} },
        serverInfo: { name: 'bowire-mcp', version },
    },
});
const toolsList = (names = ['bowire.discover', 'bowire.invoke', 'bowire.test.run']) => JSON.stringify({
    jsonrpc: '2.0', id: 2, result: { tools: names.map(name => ({ name, inputSchema: { type: 'object' } })) },
});

describe('probeRequests', () => {
    it('initialises, confirms, then lists the tools', () => {
        const methods = probeRequests().map(line => JSON.parse(line).method);
        assert.deepEqual(methods, ['initialize', 'notifications/initialized', 'tools/list']);
    });
});

describe('checkProbe', () => {
    it('accepts a server that initialises and lists tools', () => {
        assert.deepEqual(checkProbe([initialize(), toolsList()].join('\n')), []);
    });

    it('accepts the expected version with or without the fourth component', () => {
        assert.deepEqual(checkProbe([initialize('2.10.0.0'), toolsList()].join('\n'), { expectVersion: '2.10.0' }), []);
        assert.deepEqual(checkProbe([initialize('2.10.0'), toolsList()].join('\n'), { expectVersion: '2.10.0' }), []);
    });

    it('accepts a patch release, whose AssemblyVersion stays at Major.Minor.0.0', () => {
        // Directory.Build.props binds AssemblyVersion to Major.Minor, so 2.9.1 reports 2.9.0.0.
        assert.deepEqual(checkProbe([initialize('2.9.0.0'), toolsList()].join('\n'), { expectVersion: '2.9.1' }), []);
    });

    it('reports an image of an earlier minor even when the patch numbers agree', () => {
        const problems = checkProbe([initialize('2.8.0.0'), toolsList()].join('\n'), { expectVersion: '2.9.0' });
        assert.match(problems.join('\n'), /2\.8\.0\.0.*2\.9\.0/);
    });

    it('reports an image that still carries the previous release', () => {
        const problems = checkProbe([initialize('2.9.0.0'), toolsList()].join('\n'), { expectVersion: '2.10.0' });
        assert.match(problems.join('\n'), /2\.9\.0\.0.*2\.10\.0/);
    });

    it('reports a server that never answered initialize', () => {
        assert.match(checkProbe(toolsList()).join('\n'), /initialize/);
    });

    it('reports an empty tool list', () => {
        assert.match(checkProbe([initialize(), toolsList([])].join('\n')).join('\n'), /no tools/);
    });

    it('reports an error response', () => {
        const error = JSON.stringify({ jsonrpc: '2.0', id: 2, error: { code: -32601, message: 'Method not found' } });
        assert.match(checkProbe([initialize(), error].join('\n')).join('\n'), /Method not found/);
    });

    it('requires the tasks extension from 2.10 on', () => {
        const problems = checkProbe([initialize('2.10.0.0', { tasks: false }), toolsList()].join('\n'));
        assert.match(problems.join('\n'), /io\.modelcontextprotocol\/tasks/);
    });

    it('requires bowire.test.run from 2.10 on', () => {
        const problems = checkProbe([initialize('2.10.0.0'), toolsList(['bowire.discover'])].join('\n'));
        assert.match(problems.join('\n'), /bowire\.test\.run/);
    });

    it('probes a release before 2.10 as before, without the extension or the tool', () => {
        assert.deepEqual(checkProbe([initialize('2.9.0.0'), toolsList(['bowire.discover'])].join('\n')), []);
    });

    it('ignores log lines that are not JSON-RPC', () => {
        assert.deepEqual(checkProbe(['starting...', initialize(), '', toolsList()].join('\n')), []);
    });
});

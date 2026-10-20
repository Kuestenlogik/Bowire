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

const initialize = (version = '2.9.0.0') => JSON.stringify({
    jsonrpc: '2.0', id: 1,
    result: { protocolVersion: '2025-03-26', capabilities: { tools: {} }, serverInfo: { name: 'bowire-mcp', version } },
});
const toolsList = (names = ['bowire.discover', 'bowire.invoke']) => JSON.stringify({
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

    it('ignores log lines that are not JSON-RPC', () => {
        assert.deepEqual(checkProbe(['starting...', initialize(), '', toolsList()].join('\n')), []);
    });
});

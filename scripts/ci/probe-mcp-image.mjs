#!/usr/bin/env node
// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #670 - the MCP image built from the root Dockerfile keeps answering. Glama starts that image
// and checks it answers introspection; awesome-mcp-servers lists only servers Glama has scored.
// The Dockerfile installs the latest released Kuestenlogik.Bowire.Tool, so a release that breaks
// `bowire mcp serve` would break the listing without anyone touching the Dockerfile.
//
// The probe runs the image the way a directory does (`docker run -i`), sends initialize and
// tools/list over stdio, and fails unless both come back and the server lists tools. With
// --expect-version it also fails when the image carries another release than the one expected.
//
//   node scripts/ci/probe-mcp-image.mjs <image> [--expect-version 2.10.0]

import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const TIMEOUT_MS = 60_000;

/** The JSON-RPC lines the probe writes to the server's stdin. */
export function probeRequests() {
    return [
        { jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-03-26', capabilities: {}, clientInfo: { name: 'bowire-ci-probe', version: '1' } } },
        { jsonrpc: '2.0', method: 'notifications/initialized' },
        { jsonrpc: '2.0', id: 2, method: 'tools/list' },
    ].map(message => JSON.stringify(message));
}

/** What is wrong with the server's stdout, as sentences; empty when a directory would accept it. */
export function checkProbe(stdout, { expectVersion } = {}) {
    const responses = new Map();
    for (const line of stdout.split('\n')) {
        let message;
        try { message = JSON.parse(line); } catch { continue; }
        if (message && message.id !== undefined) responses.set(message.id, message);
    }

    const problems = [];
    const init = responses.get(1);
    if (!init) problems.push('no response to initialize');
    else if (init.error) problems.push(`initialize failed: ${init.error.message}`);
    else {
        // serverInfo.version is the AssemblyVersion, which Directory.Build.props binds to
        // Major.Minor: 2.9.1 reports 2.9.0.0. So only Major.Minor can be checked here; the
        // patch is held by the build, which installs the expected version or fails.
        const version = init.result?.serverInfo?.version ?? '';
        const majorMinor = v => v.split('.').slice(0, 2).join('.');
        if (expectVersion && majorMinor(version) !== majorMinor(expectVersion)) {
            problems.push(`the image runs ${version || 'an unknown version'}, expected ${expectVersion}`);
        }
    }

    const list = responses.get(2);
    if (!list) problems.push('no response to tools/list');
    else if (list.error) problems.push(`tools/list failed: ${list.error.message}`);
    else if (!(list.result?.tools?.length > 0)) problems.push('tools/list returned no tools');
    return problems;
}

/** Run the image, feed it the probe, and collect stdout until both answers are in or time runs out. */
function runImage(image) {
    return new Promise((resolve, reject) => {
        const child = spawn('docker', ['run', '-i', '--rm', image], { stdio: ['pipe', 'pipe', 'inherit'] });
        let stdout = '';
        const finish = () => { clearTimeout(timer); child.stdin.end(); child.kill(); resolve(stdout); };
        const timer = setTimeout(finish, TIMEOUT_MS);
        child.on('error', reject);
        child.on('exit', () => { clearTimeout(timer); resolve(stdout); });
        child.stdout.on('data', chunk => {
            stdout += chunk;
            if (checkProbe(stdout).length === 0) finish();
        });
        for (const line of probeRequests()) child.stdin.write(`${line}\n`);
    });
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const [image, ...rest] = process.argv.slice(2);
    const at = rest.indexOf('--expect-version');
    const expectVersion = at >= 0 ? rest[at + 1] : undefined;
    if (!image) {
        console.error('usage: node scripts/ci/probe-mcp-image.mjs <image> [--expect-version X.Y.Z]');
        process.exit(2);
    }

    const stdout = await runImage(image);
    const problems = checkProbe(stdout, { expectVersion });
    if (problems.length) {
        for (const p of problems) console.error(`::error::${p}`);
        console.error(`\nstdout was:\n${stdout || '(empty)'}`);
        process.exit(1);
    }
    const tools = JSON.parse(stdout.split('\n').find(l => l.includes('"id":2')) ?? '{}').result?.tools?.length;
    console.log(`MCP image ${image} answers initialize and lists ${tools} tools${expectVersion ? ` (version ${expectVersion})` : ''}.`);
}

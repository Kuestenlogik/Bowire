// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #101 — integration smoke test, run inside a real VS Code extension host.
//
//   code --extensionDevelopmentPath=<ext> --extensionTestsPath=<this file>
//
// The unit tests cover the logic without VS Code; this covers what they
// cannot: that the manifest is valid enough for VS Code to load the
// extension, that the command is actually registered, and that invoking it
// really does leave a Bowire server listening. Anything short of that would
// leave "it works" resting on the manifest never having been parsed.
//
// Requires `bowire` on PATH — the harness script puts a shim there.

'use strict';

const assert = require('node:assert');
const vscode = require('vscode');
const { portForWorkspace } = require('../lib/workbench');

async function waitForServer(url, timeoutMs = 90_000) {
    const deadline = Date.now() + timeoutMs;
    let lastError = 'never attempted';
    while (Date.now() < deadline) {
        try {
            const resp = await fetch(url, { signal: AbortSignal.timeout(5000) });
            if (resp.ok) return resp;
            lastError = `HTTP ${resp.status}`;
        } catch (err) {
            lastError = err && err.message ? err.message : String(err);
        }
        await new Promise(r => setTimeout(r, 1000));
    }
    throw new Error(`No Bowire server answered at ${url} within ${timeoutMs} ms (last: ${lastError})`);
}

async function run() {
    const results = [];
    const step = (name, detail) => {
        results.push(`  ok  ${name}${detail ? ' — ' + detail : ''}`);
    };

    // 1. VS Code parsed the manifest and can activate the extension.
    //
    //    Activation has to be triggered explicitly here. With
    //    `activationEvents: []` the extension activates lazily when its
    //    command is invoked, so asking for the command list first would only
    //    prove that lazy activation is lazy — which is how the first version
    //    of this test failed.
    const extension = vscode.extensions.getExtension('kuestenlogik.bowire-vscode');
    assert.ok(extension, 'VS Code did not load the extension — check package.json');
    await extension.activate();
    step('extension activated', extension.packageJSON.version);

    const commands = await vscode.commands.getCommands(true);
    assert.ok(commands.includes('bowire.openWorkbench'),
        'bowire.openWorkbench is not registered even after activation');
    step('command registered');

    // 2. A workspace folder is what the extension derives its port and the
    //    CLI's working directory from.
    const folder = vscode.workspace.workspaceFolders && vscode.workspace.workspaceFolders[0];
    assert.ok(folder, 'the smoke test needs a workspace folder open');
    const expectedPort = portForWorkspace(folder.uri.fsPath);
    step('workspace folder open', `port ${expectedPort}`);

    // 3. Invoking the command must start the CLI and open the panel. This is
    //    the step the unit tests structurally cannot reach.
    await vscode.commands.executeCommand('bowire.openWorkbench');
    step('command executed');

    // 4. …and a Bowire server must actually be answering on the port the
    //    extension derived. This is the real proof: the panel could render an
    //    empty frame and look fine while nothing was listening.
    const resp = await waitForServer(`http://127.0.0.1:${expectedPort}/`);
    const body = await resp.text();
    assert.ok(body.includes('__BOWIRE_CONFIG__'),
        'the server answered but did not serve the Bowire workbench');
    step('workbench served', `${body.length} bytes on ${expectedPort}`);

    // 5. Storage location is deliberately NOT asserted here.
    //
    //    This step used to require `.bowire/` in the workspace folder, on the
    //    assumption that starting the CLI there was enough to put it there. It
    //    is not: the storage root is computed once from the user profile
    //    (Auth/IBowireUserStore.cs), so the working directory never affects it
    //    and the directory is never created. The test was asserting a
    //    documented intention rather than a behaviour — and it was right to
    //    fail, which is how the gap was found.
    //
    //    #591 decides how a workspace's data gets rooted at the workspace.
    //    When it lands, this step comes back as a real assertion.

    // 6. Leave nothing running. Closing the panel is also what a user does,
    //    and it is what tells the extension to stop the CLI — without it the
    //    child process keeps the extension host alive and VS Code never
    //    exits, so the harness hangs instead of reporting a pass.
    await vscode.commands.executeCommand('workbench.action.closeAllEditors');
    await new Promise(r => setTimeout(r, 2000));
    step('panel closed, process stopped');

    report('Bowire VS Code smoke test\n' + results.join('\n'));
}

/**
 * Print the outcome, and also write it to BOWIRE_SMOKE_LOG when set.
 *
 * The extension host is a separate process from whatever launched it, and on
 * Windows its stdout does not reliably reach the caller's pipe — a run that
 * proved everything would otherwise look identical to one that never
 * started. A file the harness can read afterwards makes the result evidence
 * rather than an exit code taken on faith.
 */
function report(text) {
    console.log('\n' + text + '\n');
    const target = process.env.BOWIRE_SMOKE_LOG;
    if (!target) return;
    try { require('node:fs').writeFileSync(target, text + '\n', 'utf8'); }
    catch { /* the console line already carried it */ }
}

module.exports = { run };

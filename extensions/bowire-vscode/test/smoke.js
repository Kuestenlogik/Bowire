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

    // 2. The working directory — and with it the port — comes from the
    //    workspace folder, or from the extension's own storage when there is
    //    none. Both are exercised: the runner launches this file twice, once
    //    with a folder and once without.
    //
    //    The no-folder case is not a curiosity. globalStorageUri is a path
    //    VS Code does not create, so before the extension started creating it
    //    the spawn failed with ENOENT naming the executable — which exists.
    const folder = vscode.workspace.workspaceFolders && vscode.workspace.workspaceFolders[0];
    const expectedPort = folder ? portForWorkspace(folder.uri.fsPath) : null;
    step(folder ? 'workspace folder open' : 'no workspace folder — storage fallback',
        expectedPort ? `port ${expectedPort}` : 'port derived from extension storage');

    // 3. Invoking the command must start the CLI and open the panel. This is
    //    the step the unit tests structurally cannot reach.
    await vscode.commands.executeCommand('bowire.openWorkbench');
    step('command executed');

    // 4. …and a Bowire server must actually be answering. This is the real
    //    proof: the panel could render an empty frame and look fine while
    //    nothing was listening.
    const reported = extension.exports.currentUrl();
    assert.ok(reported, 'the extension started no workbench');
    if (expectedPort) {
        // With a folder the port is derived, so this also pins that the
        // derivation and the process agree rather than merely both existing.
        assert.equal(new URL(reported).port, String(expectedPort),
            'the CLI bound a different port than the extension derived');
    }
    const resp = await waitForServer(`${reported}/`);
    const body = await resp.text();
    assert.ok(body.includes('__BOWIRE_CONFIG__'),
        'the server answered but did not serve the Bowire workbench');
    // Deliberately nothing derived from the response body goes into the
    // report: it is written to a file, and letting HTTP content reach a file
    // write is a shape worth not having even in a test. The assertion above
    // already establishes what the body had to contain.
    step('workbench served', reported);

    // 5. Storage lands in the workspace when its manifest opts in (#591).
    //
    //    This step once asserted the same thing on a false premise — that
    //    starting the CLI in the workspace was enough. It never was: the root
    //    was computed from the user profile and the working directory had no
    //    say. The assertion was right to fail, and failing is how the gap got
    //    found.
    //
    //    What makes it true now is the repository, not the editor: the runner
    //    writes `.bowire/project.json` with `"storage": "project"`, and the
    //    same checkout resolves the same way from a terminal or from CI. Only
    //    run when the runner set that up, so the no-folder scenario skips it.
    if (folder) {
        const manifest = vscode.Uri.joinPath(folder.uri, '.bowire', 'project.json');
        let optedIn = true;
        try { await vscode.workspace.fs.stat(manifest); } catch { optedIn = false; }

        if (optedIn) {
            // Writing is what creates the store — starting the server does not
            // touch disk on its own, so asserting before a write would only
            // prove the directory the manifest already lives in exists.
            const put = await fetch(`${reported}/api/environments`, {
                method: 'PUT',
                headers: { 'content-type': 'application/json' },
                body: JSON.stringify([{ name: 'smoke', variables: {} }]),
                signal: AbortSignal.timeout(15_000),
            });
            assert.ok(put.ok, `storing an environment failed: HTTP ${put.status}`);

            const stored = vscode.Uri.joinPath(folder.uri, '.bowire', 'environments.json');
            await vscode.workspace.fs.stat(stored);
            step('storage rooted at the workspace', '.bowire/environments.json');
        }
    }

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
    const target = process.env.BOWIRE_SMOKE_LOG;
    if (!target) {
        console.log('\n' + text + '\n');
        return;
    }
    try {
        // The runner prints the file, and its stdio is inherited — logging as
        // well would show every result twice, which reads like two runs.
        require('node:fs').writeFileSync(target, text + '\n', 'utf8');
    } catch {
        console.log('\n' + text + '\n');
    }
}

module.exports = { run };

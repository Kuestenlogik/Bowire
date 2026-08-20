// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #101 — Bowire in VS Code.
//
// The extension does not reimplement anything: it starts the Bowire CLI with
// the workspace folder as its working directory and shows the workbench it
// serves in a webview. Two consequences fall out of that choice:
//
//   * `.bowire/` (collections, environments, recordings, contract results,
//     benchmark schedules) lands next to the code and travels with the repo,
//     git-diff-able, instead of in IDE-proprietary storage.
//   * no file-I/O bridge is needed. The original sketch worried about
//     `vscode.workspace.fs` round-trips; they only matter if the UI has to
//     reach the filesystem itself, and it doesn't — the CLI process already
//     reads and writes those files, and the webview only speaks HTTP to it.
//
// Plain CommonJS on purpose: no TypeScript build step means CI has nothing to
// compile and the shipped code is the code you can read here.

'use strict';

const vscode = require('vscode');
const { spawn } = require('node:child_process');
const path = require('node:path');
const fs = require('node:fs');
const {
    findCli,
    buildArgs,
    parseListeningUrl,
    portForWorkspace,
    buildWebviewHtml,
    missingCliMessage,
} = require('./lib/workbench');

/** The single panel and the process behind it. */
let panel = null;
let child = null;

function log(channel, line) {
    if (channel) channel.appendLine(line);
}

function stopProcess(channel) {
    if (!child) return;
    log(channel, 'Stopping the Bowire process.');
    try { child.kill(); } catch { /* already gone */ }
    child = null;
}

/**
 * Start the CLI and resolve with the URL it reports. Rejects if it exits or
 * never announces itself, so the panel is never opened onto a dead port.
 */
function startWorkbench(cli, cwd, port, channel) {
    return new Promise((resolve, reject) => {
        const proc = spawn(cli, buildArgs(port), { cwd, windowsHide: true });
        child = proc;

        let settled = false;
        const finish = (fn, value) => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            fn(value);
        };

        const onData = (buffer) => {
            const text = String(buffer);
            log(channel, text.trimEnd());
            const url = parseListeningUrl(text);
            if (url) finish(resolve, url);
        };

        proc.stdout?.on('data', onData);
        proc.stderr?.on('data', onData);

        proc.on('error', (err) => finish(reject, new Error(`Could not start Bowire: ${err.message}`)));
        proc.on('exit', (code) =>
            finish(reject, new Error(`Bowire exited with code ${code} before it started serving.`)));

        // Generous: a first run restores plugins and can take a while on a
        // cold machine. Failing here is better than a panel that never loads.
        const timer = setTimeout(
            () => finish(reject, new Error('Bowire did not report a listening URL within 60 seconds.')),
            60_000);
    });
}

async function openWorkbench(context, channel) {
    if (panel) {
        panel.reveal(vscode.ViewColumn.Beside);
        return;
    }

    const cli = findCli();
    if (!cli) {
        const answer = await vscode.window.showErrorMessage(
            missingCliMessage(), 'Open install docs');
        if (answer === 'Open install docs') {
            await vscode.env.openExternal(vscode.Uri.parse('https://bowire.io/docs/setup/install.html'));
        }
        return;
    }

    const folder = vscode.workspace.workspaceFolders?.[0];
    // No folder open: fall back to the extension's own storage so the CLI
    // still has somewhere to put `.bowire/` rather than the process CWD,
    // which in VS Code is wherever the editor happened to launch from.
    const cwd = folder ? folder.uri.fsPath : context.globalStorageUri.fsPath;
    const port = portForWorkspace(cwd);

    let url;
    try {
        url = await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: 'Starting Bowire…' },
            () => startWorkbench(cli, cwd, port, channel));
    } catch (err) {
        stopProcess(channel);
        await vscode.window.showErrorMessage(err.message);
        return;
    }

    const actualPort = Number(new URL(url).port || port);
    panel = vscode.window.createWebviewPanel(
        'bowire.workbench',
        'Bowire',
        vscode.ViewColumn.Beside,
        {
            enableScripts: true,
            retainContextWhenHidden: true,
            // Maps the CLI's port into the webview's origin so the iframe can
            // reach it without the workbench needing to know it is embedded.
            portMapping: [{ webviewPort: actualPort, extensionHostPort: actualPort }],
        });

    panel.webview.html = buildWebviewHtml(`http://localhost:${actualPort}`);

    // icon.png is copied in at package time from the repo's canonical logo,
    // so it is absent when running straight from a checkout. A missing file
    // would leave the tab icon blank; skipping keeps VS Code's default.
    const icon = path.join(context.extensionPath, 'icon.png');
    if (fs.existsSync(icon)) panel.iconPath = vscode.Uri.file(icon);

    // The process exists for this panel; closing the panel stops it rather
    // than leaving a server listening for the rest of the session.
    panel.onDidDispose(() => {
        panel = null;
        stopProcess(channel);
    }, null, context.subscriptions);
}

function activate(context) {
    const channel = vscode.window.createOutputChannel('Bowire');
    context.subscriptions.push(channel);

    context.subscriptions.push(
        vscode.commands.registerCommand('bowire.openWorkbench', () => openWorkbench(context, channel)));

    context.subscriptions.push({ dispose: () => stopProcess(channel) });
}

function deactivate() {
    stopProcess(null);
}

module.exports = { activate, deactivate };

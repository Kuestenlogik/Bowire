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
    resolveCli,
    manifestStartFailureMessage,
    describeSpawnError,
    readCliVersion,
    checkCliVersion,
    buildArgs,
    portForWorkspace,
    buildWebviewHtml,
    missingCliMessage, waitUntilServing
} = require('./lib/workbench');
const {
    PINNED_CLI_VERSION,
    ridFor,
    installManagedCli,
    unsupportedPlatformMessage,
    downloadOfferMessage,
} = require('./lib/download');

/** The single panel and the process behind it. */
let panel = null;
let child = null;
/** URL of the running workbench, exposed through the extension's exports. */
let currentUrl = null;

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
function startWorkbench(resolution, cwd, port, channel) {
    return new Promise((resolve, reject) => {
        // command + prefixArgs rather than a single path: a manifest-pinned
        // Bowire runs as `dotnet tool run bowire`, so the thing being spawned
        // is not always the CLI itself.
        const cli = resolution.command;
        const proc = spawn(cli, [...resolution.prefixArgs, ...buildArgs(port)], { cwd, windowsHide: true });
        child = proc;

        // Kept so the failure paths below can name the manifest instead of
        // blaming `dotnet`, which is never the actual problem.
        let lastOutput = '';

        // One deadline, not two: the poller owns it. A first run restores
        // plugins and can be slow on a cold machine, so it is generous.
        const START_TIMEOUT_MS = 60_000;

        let settled = false;
        const finish = (fn, value) => {
            if (settled) return;
            settled = true;
            fn(value);
        };

        const onData = (buffer) => {
            const text = String(buffer);
            lastOutput = text;
            log(channel, text.trimEnd());
        };

        // Readiness is decided by asking the port, not by reading the log.
        //
        // The banner was the old signal and it is unreliable twice over: it
        // disappears at a higher log level, and it is printed before the bind
        // is known to have worked — a second Bowire on a taken port prints
        // "Bowire is running at: …" and only then throws AddressInUse. The
        // port cannot be wrong: the CLI binds the one it is given or exits,
        // it never falls back to another, so there is nothing the log could
        // tell us that we do not already know.
        //
        // `proc.on('exit')` below still fires first if the process dies, so a
        // failed bind surfaces as its real error instead of a 30 s wait.
        // Generous: a first run restores plugins and can take a while on a
        // cold machine. Failing here is better than a panel that never loads.
        const url = `http://localhost:${port}`;
        waitUntilServing(url, { timeoutMs: START_TIMEOUT_MS }).then((up) => {
            if (up) finish(resolve, url);
            else finish(reject, new Error(
                `Bowire did not serve a response at ${url} within `
                + `${START_TIMEOUT_MS / 1000} seconds.`));
        });

        proc.stdout?.on('data', onData);
        proc.stderr?.on('data', onData);

        proc.on('error', (err) => finish(reject, new Error(
            resolution.source === 'manifest'
                ? manifestStartFailureMessage(resolution.manifest, err.message)
                : describeSpawnError(err, { cli, cwd }))));

        proc.on('exit', (code) => finish(reject, new Error(
            // A manifest-pinned tool that exits immediately has almost always
            // not been restored. Saying "Bowire exited with code 1" would be
            // true and useless; the fix is one command and belongs in the
            // message.
            resolution.source === 'manifest'
                ? manifestStartFailureMessage(resolution.manifest, lastOutput)
                : `Bowire exited with code ${code} before it started serving.`)));

    });
}

/**
 * The fourth step of the chain (#590): offer to fetch a CLI when nothing else
 * turned one up.
 *
 * "Offer" is the operative word. Pulling ~60 MB unannounced is not something an
 * editor should do on a command someone ran to look at a UI, and some
 * environments refuse outbound traffic outright — so the default asks, and
 * `bowire.autoDownload: never` removes even the question.
 *
 * Returns a resolution the caller can start, or null when there is nothing on
 * offer, the user declined, or the download failed. All three end up at the
 * same place: today's not-found message, unchanged.
 */
async function offerManagedCli(context, channel) {
    const mode = vscode.workspace.getConfiguration('bowire').get('autoDownload', 'prompt');
    if (mode === 'never') return null;

    // A platform with no published build has nothing to offer. Say so instead
    // of asking a question whose yes cannot be honoured.
    if (!ridFor()) {
        log(channel, unsupportedPlatformMessage());
        return null;
    }

    if (mode !== 'always') {
        const answer = await vscode.window.showInformationMessage(
            downloadOfferMessage(PINNED_CLI_VERSION),
            'Download', 'Not now');
        if (answer !== 'Download') return null;
    }

    const root = context.globalStorageUri.fsPath;
    try {
        const cli = await vscode.window.withProgress({
            location: vscode.ProgressLocation.Notification,
            title: `Downloading Bowire ${PINNED_CLI_VERSION}…`,
            cancellable: true,
        }, async (progress, token) => {
            const controller = new AbortController();
            token.onCancellationRequested(() => controller.abort());

            // VS Code's progress is incremental, so the absolute fraction has
            // to be differenced. A release that reports no Content-Length gives
            // a null fraction, and reporting nothing leaves the bar
            // indeterminate — which is honest, rather than a bar that invents
            // its own progress.
            let reported = 0;
            return await installManagedCli({
                root,
                signal: controller.signal,
                onProgress: (fraction) => {
                    if (fraction === null) return;
                    const percent = fraction * 100;
                    progress.report({ increment: percent - reported });
                    reported = percent;
                },
            });
        });

        log(channel, `Downloaded Bowire ${PINNED_CLI_VERSION} to ${cli}.`);
        return { command: cli, prefixArgs: [], source: 'managed' };
    } catch (err) {
        // Cancelling is a choice, not a fault: it belongs in the log, not in an
        // error notification the user has to dismiss after asking for it.
        if (err?.name === 'AbortError') {
            log(channel, 'Download cancelled.');
            return null;
        }
        log(channel, `Download failed: ${err?.message ?? err}`);
        await vscode.window.showErrorMessage(`Could not download Bowire: ${err?.message ?? err}`);
        return null;
    }
}

async function openWorkbench(context, channel) {
    if (panel) {
        panel.reveal(vscode.ViewColumn.Beside);
        return;
    }

    const folder = vscode.workspace.workspaceFolders?.[0];
    // No folder open: fall back to the extension's own storage, so the CLI
    // gets a real directory rather than the process CWD, which in VS Code is
    // wherever the editor happened to launch from.
    //
    // VS Code does not create globalStorageUri — the extension has to. Left
    // alone it is a path to a directory that does not exist, and Node reports
    // a missing working directory as ENOENT against the *command*: "Could not
    // spawn …\bowire.exe ENOENT" for an executable sitting right there.
    const cwd = folder ? folder.uri.fsPath : context.globalStorageUri.fsPath;
    try {
        fs.mkdirSync(cwd, { recursive: true });
    } catch (err) {
        await vscode.window.showErrorMessage(
            `Bowire needs a working directory it can use, but ${cwd} could not be created: ${err.message}`);
        return;
    }
    const port = portForWorkspace(cwd);

    // The folder has to be known first: `bowire.cliPath` may be written
    // relative to it via ${workspaceFolder}, which is the form that can be
    // committed to `.vscode/settings.json` and shared with the team.
    const configuredPath = vscode.workspace.getConfiguration('bowire').get('cliPath', '');
    let resolution = resolveCli({
        configuredPath,
        variables: folder ? { workspaceFolder: folder.uri.fsPath } : {},
        // Where to look for `.config/dotnet-tools.json`. Only meaningful with
        // a folder open — a manifest is a property of a checkout.
        workspaceDir: folder ? folder.uri.fsPath : '',
        // Where an earlier download would have landed. Passing it here rather
        // than letting the library guess keeps the storage location the
        // extension host's business, which is the only thing that knows it.
        managedRoot: context.globalStorageUri.fsPath,
    });

    // Nothing anywhere — but only a genuine miss is worth offering a download
    // for. A `bowire.cliPath` that does not resolve is a typo, and downloading
    // 60 MB is not the answer to a typo.
    if (!resolution.command && resolution.source === 'path') {
        resolution = (await offerManagedCli(context, channel)) ?? resolution;
    }

    if (!resolution.command) {
        const isSetting = resolution.source === 'setting';
        const action = isSetting ? 'Open settings' : 'Open install docs';
        const answer = await vscode.window.showErrorMessage(missingCliMessage(resolution), action);
        if (answer === 'Open settings') {
            await vscode.commands.executeCommand('workbench.action.openSettings', 'bowire.cliPath');
        } else if (answer === 'Open install docs') {
            await vscode.env.openExternal(vscode.Uri.parse('https://bowire.io/docs/setup/install.html'));
        }
        return;
    }
    const origin = {
        setting: 'bowire.cliPath',
        manifest: resolution.manifest,
        path: 'PATH',
        managed: 'the extension\'s managed download',
    }[resolution.source];
    log(channel, `Using ${[resolution.command, ...resolution.prefixArgs].join(' ')} (from ${origin}).`);
    // The version a manifest pins is read straight out of the file. It is the
    // question the manifest exists to answer, and the probe below never runs
    // for this case — so without this the channel would name the manifest and
    // then say nothing about what it pins.
    if (resolution.source === 'manifest') {
        log(channel, `Pinned version: ${resolution.pinnedVersion ?? 'not stated in the manifest'}`);
    }

    // Check the version before spawning. A CLI too old to understand the
    // arguments below exits immediately, and the resulting "exited before it
    // started serving" says nothing about why — which is exactly the kind of
    // failure that costs an afternoon.
    //
    // Skipped for a manifest: asking a not-yet-restored tool for its version
    // fails for a reason that has nothing to do with the version, and the
    // start path already reports that case with the command that fixes it.
    if (resolution.source !== 'manifest') {
        const version = checkCliVersion(readCliVersion(resolution.command));
        log(channel, `Version: ${version.version ?? 'not reported'}`);
        if (!version.ok) {
            await vscode.window.showErrorMessage(version.message);
            return;
        }
    }

    let url;
    try {
        url = await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: 'Starting Bowire…' },
            () => startWorkbench(resolution, cwd, port, channel));
    } catch (err) {
        stopProcess(channel);
        await vscode.window.showErrorMessage(err.message);
        return;
    }

    currentUrl = url;
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
        currentUrl = null;
        stopProcess(channel);
    }, null, context.subscriptions);
}

function activate(context) {
    const channel = vscode.window.createOutputChannel('Bowire');
    context.subscriptions.push(channel);

    context.subscriptions.push(
        vscode.commands.registerCommand('bowire.openWorkbench', () => openWorkbench(context, channel)));

    context.subscriptions.push({ dispose: () => stopProcess(channel) });

    // The extension's public API. It exists so the smoke test can find the
    // workbench without a workspace folder to derive the port from — that
    // case has no other way to learn where the CLI bound.
    return { currentUrl: () => currentUrl };
}

function deactivate() {
    stopProcess(null);
}

module.exports = { activate, deactivate };

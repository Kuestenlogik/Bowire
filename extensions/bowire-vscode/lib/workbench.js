// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #101 — the pieces of the VS Code extension that are worth testing without
// VS Code running: finding the CLI, building its arguments, recognising the
// line that says it is listening, and building the webview document.
//
// extension.js keeps only the parts that genuinely need the vscode API
// (panels, workspace folders, notifications), so the logic below stays
// reachable from `node --test`.

'use strict';

const { spawnSync } = require('node:child_process');

/** Name the CLI is installed under on each platform. */
const BINARY = process.platform === 'win32' ? 'bowire.exe' : 'bowire';

/**
 * Is `bowire` reachable on PATH?
 *
 * The extension deliberately does not bundle Bowire: a self-contained build
 * per platform is ~100 MB per marketplace package, three builds to keep in
 * step, and an extension release tied to every Bowire release. Requiring the
 * CLI keeps the extension thin and lets it host whichever Bowire the
 * developer already runs.
 */
function findCli(runner = spawnSync) {
    const probe = process.platform === 'win32' ? 'where' : 'which';
    try {
        const result = runner(probe, [BINARY], { encoding: 'utf8' });
        if (result.status !== 0) return null;
        const first = String(result.stdout || '').split(/\r?\n/).find(l => l.trim().length > 0);
        return first ? first.trim() : null;
    } catch {
        return null;
    }
}

/**
 * Arguments for the hosted workbench.
 *
 * `--port 0` is not used: the CLI prints the port it bound, and parsing that
 * is more robust than pre-picking a free port and racing another process for
 * it between the check and the bind.
 */
function buildArgs(port) {
    return ['--port', String(port), '--auto-create-initial-workspace'];
}

/**
 * The URL from the CLI's startup banner, or null while it is still starting.
 *
 * Matching the banner rather than assuming the port we asked for means a
 * fallback binding still gets picked up instead of leaving the panel pointed
 * at a port nobody is listening on.
 */
function parseListeningUrl(line) {
    if (typeof line !== 'string') return null;
    const match = line.match(/https?:\/\/(?:localhost|127\.0\.0\.1)(?::(\d+))?\/?/i);
    return match ? match[0].replace(/\/$/, '') : null;
}

/**
 * Pick a port to ask for. Deterministic per workspace so reopening the panel
 * reuses the same one, which keeps a stale process from stacking up ports.
 */
function portForWorkspace(workspacePath, base = 5099) {
    if (!workspacePath) return base;
    let hash = 0;
    for (const ch of String(workspacePath)) {
        hash = (hash * 31 + ch.charCodeAt(0)) >>> 0;
    }
    // 5099..6098 — above the CLI's own default (5080) so a workbench the
    // developer started by hand keeps its port.
    return base + (hash % 1000);
}

/**
 * Reduce a URL to `scheme://host:port` if — and only if — it is a local
 * workbench address, else null.
 *
 * Rebuilding from parsed components rather than escaping the string is the
 * point: the result cannot carry a path, a query or markup, so nothing a URL
 * might contain can reach the document as anything but a host and a port.
 * Escaping quotes alone left `&lt;script&gt;` text sitting inside an
 * attribute — inert, but inert for one reason only, which is a thin place to
 * rest a webview on.
 */
function normaliseWorkbenchUrl(url) {
    let parsed;
    try { parsed = new URL(String(url)); }
    catch { return null; }

    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return null;
    if (parsed.hostname !== 'localhost' && parsed.hostname !== '127.0.0.1') return null;
    if (!/^\d*$/.test(parsed.port)) return null;

    return parsed.port
        ? `${parsed.protocol}//${parsed.hostname}:${parsed.port}`
        : `${parsed.protocol}//${parsed.hostname}`;
}

/**
 * The webview document: an iframe onto the local workbench.
 *
 * VS Code maps the port into the webview's origin, so the iframe URL is the
 * mapped one rather than the real localhost port. The workbench needs no
 * bridge for files — the CLI process owns the workspace folder as its working
 * directory, so `.bowire/` lands next to the code and travels with the repo.
 */
function buildWebviewHtml(url) {
    const safe = normaliseWorkbenchUrl(url);
    if (!safe) throw new Error(`Refusing to frame ${url}: not a local workbench URL.`);
    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta http-equiv="Content-Security-Policy"
      content="default-src 'none'; frame-src ${safe}; style-src 'unsafe-inline';" />
<style>
  html, body { margin: 0; padding: 0; height: 100%; overflow: hidden; }
  iframe { border: 0; width: 100%; height: 100%; display: block; }
</style>
</head>
<body>
<iframe src="${safe}" title="Bowire workbench" allow="clipboard-read; clipboard-write"></iframe>
</body>
</html>`;
}

/** The message shown when the CLI is missing, with the install routes that work today. */
function missingCliMessage() {
    return 'Bowire was not found on your PATH. Install it with '
        + '`winget install Kuestenlogik.Bowire`, `choco install bowire`, '
        + 'or `dotnet tool install -g Kuestenlogik.Bowire.Tool`.';
}

module.exports = {
    BINARY,
    findCli,
    normaliseWorkbenchUrl,
    buildArgs,
    parseListeningUrl,
    portForWorkspace,
    buildWebviewHtml,
    missingCliMessage,
};

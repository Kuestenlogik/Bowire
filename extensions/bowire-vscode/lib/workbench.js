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
const { existsSync } = require('node:fs');

/** Name the CLI is installed under on each platform. */
const BINARY = process.platform === 'win32' ? 'bowire.exe' : 'bowire';

/**
 * Oldest CLI whose command-line contract this extension can drive.
 *
 * `--port` together with `--auto-create-initial-workspace` — the two arguments
 * buildArgs emits — have both existed since before 2.0.0, so that is the honest
 * floor. Anything older does not fail cleanly: the CLI rejects the unknown
 * argument and exits, and startWorkbench reports "exited before it started
 * serving", which says nothing about the actual cause.
 */
const MINIMUM_CLI_VERSION = '2.0.0';

/**
 * Is `bowire` reachable on PATH?
 *
 * The extension deliberately does not bundle Bowire: a self-contained build
 * per platform is ~120 MB per marketplace package, one per platform to keep in
 * step, and an extension release tied to every Bowire release. Requiring the
 * CLI keeps the extension thin and lets it host whichever Bowire the
 * developer already runs — the same one their CI and their terminal use.
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
 * Substitute the variables VS Code users expect to work in a path setting.
 *
 * Without this a workspace-relative CLI has to be written as an absolute path,
 * which cannot be committed to `.vscode/settings.json` and shared — the one
 * place a per-project CLI path is actually worth putting.
 */
function expandPathVariables(value, variables = {}) {
    return String(value ?? '').replace(/\$\{(\w+)\}/g, (whole, name) =>
        (Object.hasOwn(variables, name) ? String(variables[name]) : whole));
}

/**
 * Where the CLI comes from: the explicit setting if there is one, PATH
 * otherwise.
 *
 * The setting wins deliberately. Someone who names a path has a reason — a
 * local build, a portable copy, a second version alongside the installed one —
 * and silently preferring PATH would run a different binary than the one they
 * asked for.
 *
 * A configured path that does not exist is reported rather than skipped: a
 * typo falling through to PATH would look like the setting was ignored, which
 * is the harder failure to diagnose of the two.
 */
function resolveCli(options = {}) {
    const {
        configuredPath = '',
        variables = {},
        runner = spawnSync,
        exists = existsSync,
    } = options;

    const configured = expandPathVariables(configuredPath, variables).trim();
    if (configured) {
        return exists(configured)
            ? { path: configured, source: 'setting' }
            : { path: null, source: 'setting', configured };
    }

    const found = findCli(runner);
    return found ? { path: found, source: 'path' } : { path: null, source: 'path' };
}

/**
 * Major/minor/patch and the prerelease tag out of `bowire --version`, whose
 * output looks like `2.4.1-alpha.0.104+d86f781…`.
 */
function parseCliVersion(text) {
    const match = String(text ?? '').match(/(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?/);
    if (!match) return null;
    return {
        major: Number(match[1]),
        minor: Number(match[2]),
        patch: Number(match[3]),
        prerelease: match[4] ?? null,
        raw: match[0],
    };
}

/**
 * Order two parsed versions. Semver's rule that a prerelease sorts below the
 * release it leads to matters here: 2.0.0-alpha is not yet 2.0.0, so it must
 * not satisfy a 2.0.0 minimum.
 */
function compareCliVersions(a, b) {
    for (const part of ['major', 'minor', 'patch']) {
        if (a[part] !== b[part]) return a[part] < b[part] ? -1 : 1;
    }
    if (Boolean(a.prerelease) === Boolean(b.prerelease)) return 0;
    return a.prerelease ? -1 : 1;
}

/**
 * Decide whether a CLI is new enough, from the text it printed.
 *
 * Unreadable output is treated as acceptable on purpose. A future CLI could
 * word its banner differently, and refusing to start over an unrecognised
 * string would turn a cosmetic change into an outage; a version that is
 * legibly too old is the only case worth blocking.
 */
function checkCliVersion(versionText, minimum = MINIMUM_CLI_VERSION) {
    const found = parseCliVersion(versionText);
    const floor = parseCliVersion(minimum);
    if (!found) return { ok: true, version: null };
    if (compareCliVersions(found, floor) >= 0) return { ok: true, version: found.raw };
    return {
        ok: false,
        version: found.raw,
        message: `Bowire ${found.raw} is too old for this extension, which needs ${minimum} or newer. `
            + 'Update it (`winget upgrade Kuestenlogik.Bowire`, `choco upgrade bowire`, '
            + 'or `dotnet tool update -g Kuestenlogik.Bowire.Tool`), or point `bowire.cliPath` at a newer build.',
    };
}

/** Ask a CLI for its version; null when it cannot be run. */
function readCliVersion(cli, runner = spawnSync) {
    try {
        const result = runner(cli, ['--version'], { encoding: 'utf8', timeout: 15_000 });
        if (result.status !== 0) return null;
        return `${result.stdout ?? ''}${result.stderr ?? ''}`.trim() || null;
    } catch {
        return null;
    }
}

/**
 * Turn a spawn failure into something that names the actual cause.
 *
 * Node reports a missing working directory as ENOENT against the *command*,
 * so a perfectly present executable gets blamed for a directory that is not
 * there — "Could not spawn C:\…\bowire.exe ENOENT" while that file plainly
 * exists. Distinguishing the two costs one stat and saves the reader from
 * checking the wrong thing entirely.
 */
function describeSpawnError(err, options = {}) {
    const { cli = '', cwd = '', exists = existsSync } = options;
    const code = err && err.code;

    if (code === 'ENOENT' && cwd && !exists(cwd)) {
        return `Could not start Bowire: its working directory does not exist (${cwd}).`;
    }
    if (code === 'ENOENT') {
        return `Could not start Bowire: ${cli} could not be executed. `
            + 'Check that the file exists and is not blocked by policy.';
    }
    if (code === 'EACCES') {
        return `Could not start Bowire: ${cli} is not executable.`;
    }
    return `Could not start Bowire: ${err && err.message ? err.message : String(err)}`;
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

/**
 * What to say when there is no CLI to run.
 *
 * The two cases need different answers. A CLI that is simply not installed
 * needs install routes; a configured path that does not resolve needs the path
 * quoted back, because the fault is a typo or a moved file and no amount of
 * installing will fix it. The earlier single message offered install commands
 * to both, which is advice that cannot help the second.
 */
function missingCliMessage(resolution = { source: 'path' }) {
    if (resolution && resolution.source === 'setting') {
        return `Bowire was not found at \`${resolution.configured}\`, the path set in \`bowire.cliPath\`. `
            + 'Correct the setting, or clear it to search PATH instead.';
    }
    return 'Bowire was not found on your PATH. Install it with '
        + '`winget install Kuestenlogik.Bowire`, `choco install bowire`, '
        + 'or `dotnet tool install -g Kuestenlogik.Bowire.Tool` — '
        + 'or set `bowire.cliPath` if it is already installed somewhere else.';
}

module.exports = {
    BINARY,
    MINIMUM_CLI_VERSION,
    findCli,
    resolveCli,
    describeSpawnError,
    expandPathVariables,
    parseCliVersion,
    compareCliVersions,
    checkCliVersion,
    readCliVersion,
    normaliseWorkbenchUrl,
    buildArgs,
    parseListeningUrl,
    portForWorkspace,
    buildWebviewHtml,
    missingCliMessage,
};

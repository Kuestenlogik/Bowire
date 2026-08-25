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
const { existsSync, readFileSync } = require('node:fs');
const { join: joinPath, dirname: dirnamePath, resolve: resolvePath } = require('node:path');
const { ridFor, managedCliPath, PINNED_CLI_VERSION } = require('./download');

/** Name the CLI is installed under on each platform. */
const BINARY = process.platform === 'win32' ? 'bowire.exe' : 'bowire';

/**
 * Oldest CLI whose command-line contract this extension can drive.
 *
 * `--port`, `--auto-create-initial-workspace` and `--no-browser` — the three
 * arguments buildArgs emits — have all existed since before 2.0.0, so that is
 * the honest floor. Anything older does not fail cleanly: the CLI rejects the unknown
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
/**
 * Where a .NET tool manifest can sit, relative to a directory, in the order
 * .NET itself prefers.
 *
 * Both are real. `.config/dotnet-tools.json` is the documented location and
 * what most repos have; a bare `dotnet-tools.json` beside it also resolves,
 * and is what `dotnet new tool-manifest` produced when this was tested against
 * a real SDK. Looking only in `.config/` silently missed a manifest the SDK
 * itself honours — the kind of gap only an end-to-end run surfaces.
 */
const TOOL_MANIFEST_NAMES = [
    ['.config', 'dotnet-tools.json'],
    ['dotnet-tools.json'],
];

/** The package id a Bowire entry uses in a tool manifest. */
const TOOL_PACKAGE_ID = 'kuestenlogik.bowire.tool';

/**
 * Walk up from `startDir` for a `.config/dotnet-tools.json` that lists Bowire.
 *
 * The same search .NET itself performs, for the same reason: a manifest at the
 * repository root should govern a command run three directories down.
 *
 * Returns the manifest path when one lists Bowire, else null. A manifest that
 * exists but lists other tools is not a match — falling through to PATH is
 * right there, because the repo simply does not pin Bowire.
 */
function findToolManifest(startDir, exists = existsSync, read = readFileSync) {
    if (!startDir) return null;

    let dir = resolvePath(startDir);
    for (;;) {
        for (const parts of TOOL_MANIFEST_NAMES) {
            const candidate = joinPath(dir, ...parts);
            if (!exists(candidate)) continue;
            try {
                const manifest = JSON.parse(String(read(candidate, 'utf8')));
                const tools = manifest?.tools ?? {};
                const listed = Object.keys(tools)
                    .some(id => id.toLowerCase() === TOOL_PACKAGE_ID);
                if (listed) return candidate;
            } catch {
                // A malformed manifest is not this extension's problem to
                // report — `dotnet tool restore` says it far better. Treat it
                // as "no pin here" and keep looking.
            }
        }

        const parent = dirnamePath(dir);
        if (parent === dir) return null;
        dir = parent;
    }
}

/**
 * The version a manifest pins Bowire to, or null when it cannot be read.
 *
 * Worth logging even though the version *check* is skipped for manifests:
 * "which Bowire is this repo pinned to" is the question the manifest exists to
 * answer, and reading it out of the file costs nothing — unlike asking a tool
 * that may not be restored yet, which fails for an unrelated reason.
 */
function pinnedToolVersion(manifestPath, read = readFileSync) {
    try {
        const manifest = JSON.parse(String(read(manifestPath, 'utf8')));
        const entry = Object.entries(manifest?.tools ?? {})
            .find(([id]) => id.toLowerCase() === TOOL_PACKAGE_ID);
        return entry?.[1]?.version ?? null;
    } catch {
        return null;
    }
}

/**
 * Can this machine run a manifest-pinned tool at all?
 *
 * `dotnet tool run` needs the SDK, not just the runtime, but the runtime alone
 * still answers `--version` — so the probe asks for the SDK list instead, which
 * a runtime-only install reports as empty or fails outright.
 */
function hasDotnetSdk(runner = spawnSync) {
    try {
        const result = runner('dotnet', ['--list-sdks'], { encoding: 'utf8', timeout: 15_000 });
        if (result.status !== 0) return false;
        return String(result.stdout || '').trim().length > 0;
    } catch {
        // No `dotnet` on this machine at all.
        return false;
    }
}

/**
 * How to start Bowire: the explicit setting, then a workspace tool manifest,
 * then PATH, then a CLI this extension downloaded earlier.
 *
 * Each step answers a different question, which is why the order is not
 * arbitrary. The setting is "this exact binary, because I said so". The
 * manifest is "the version this repository is tested with", pinned in git and
 * shared with everyone who clones it. PATH is "whatever this machine has". The
 * managed download is "nothing else was here, so we brought our own".
 * Specific beats shared beats ambient beats fallback.
 *
 * Returns `{ command, prefixArgs, source }`. The manifest case runs
 * `dotnet tool run bowire`, which is why the caller cannot assume the command
 * is the CLI itself — that shape is the whole reason for `prefixArgs`.
 */
function resolveCli(options = {}) {
    const {
        configuredPath = '',
        variables = {},
        workspaceDir = '',
        managedRoot = '',
        runner = spawnSync,
        exists = existsSync,
        read = readFileSync,
    } = options;

    const configured = expandPathVariables(configuredPath, variables).trim();
    if (configured) {
        return exists(configured)
            ? { command: configured, prefixArgs: [], source: 'setting' }
            : { command: null, prefixArgs: [], source: 'setting', configured };
    }

    const manifest = findToolManifest(workspaceDir, exists, read);
    // A pin is only worth honouring if there is an SDK to honour it with.
    // Without one the manifest names a version nothing on this machine can
    // produce, and stopping there would refuse to start over a file the user
    // may not even know is in the repo — while a perfectly good Bowire sits on
    // PATH. Probing costs one spawn, and only when a manifest actually matched.
    if (manifest && hasDotnetSdk(runner)) {
        // `dotnet` rather than a resolved path: the manifest pins a version,
        // and letting the SDK honour that pin is the point. It also means the
        // tool need not be installed yet — `dotnet tool restore` fetches it,
        // which is what the caller offers when this fails to start.
        return {
            command: 'dotnet',
            prefixArgs: ['tool', 'run', 'bowire'],
            source: 'manifest',
            manifest,
            pinnedVersion: pinnedToolVersion(manifest, read),
        };
    }

    const found = findCli(runner);
    if (found) return { command: found, prefixArgs: [], source: 'path' };

    // Last: a CLI this extension downloaded earlier (#590). Below PATH on
    // purpose — an installed Bowire is the one the developer's terminal and CI
    // use, and quietly preferring our private copy would mean the editor
    // silently drives a different version than everything else on the machine.
    if (managedRoot) {
        const rid = ridFor();
        const managed = rid ? managedCliPath(managedRoot, PINNED_CLI_VERSION, rid) : null;
        if (managed && exists(managed)) {
            return { command: managed, prefixArgs: [], source: 'managed' };
        }
    }

    return { command: null, prefixArgs: [], source: 'path' };
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
 * `--no-browser` is not optional here. The CLI opens a browser window on
 * startup by default, which is right when a human runs `bowire` in a terminal
 * and wrong when the extension is about to show the same workbench in a
 * webview: without it the operator gets both, and the one they did not ask for
 * is the one that steals focus.
 *
 * `--port 0` asks the OS for a free port and `--port-file` reports which one
 * it got (#615). Picking a port ourselves meant racing every other process on
 * the machine between choosing it and binding it — including a second window
 * on the same folder, which derived the same number. The OS does not race
 * with itself.
 *
 * That also settles what happens when a Bowire is already running: nothing.
 * A workbench the developer started by hand keeps its port and its lifetime,
 * and we start our own alongside it. Adopting theirs would be wrong anyway —
 * it has a different working directory, so it reads a different
 * `.bowire/project.json`, and closing our panel would kill their process.
 */
function buildArgs(portFilePath) {
    return [
        '--port', '0',
        '--port-file', String(portFilePath),
        '--auto-create-initial-workspace',
        '--no-browser',
    ];
}

/**
 * The URL from a port file's contents, or null if it is not one we understand.
 *
 * Deliberately strict. This value is handed to a webview, and a file on disk
 * is not a trusted channel just because we named the path: a half-written
 * document, a leftover from an older format, or a file someone else put there
 * should all read as "not ready" rather than as an address to navigate to.
 */
function parsePortFile(text) {
    let doc;
    try {
        doc = JSON.parse(text);
    } catch {
        // Mid-write is possible in principle — the CLI writes through a temp
        // file and renames, so it should not be — and unparseable means the
        // same thing either way: keep waiting.
        return null;
    }

    if (!doc || typeof doc !== 'object') return null;
    if (doc.version !== 1) return null;
    if (typeof doc.url !== 'string') return null;

    // Same reduction the webview URL goes through: a loopback origin and
    // nothing else. A port file naming a remote host is not a workbench we
    // started, and must not become a page we render.
    return normaliseWorkbenchUrl(doc.url);
}

/**
 * Wait for the port file to appear and name a URL, or give up.
 *
 * Existence is the readiness signal: the CLI clears the path before it binds
 * and writes it only after Kestrel is listening, so seeing the file means the
 * bind succeeded. That is the whole reason this replaced banner-scraping —
 * the banner is a log line, so it vanishes at a quieter log level, and it is
 * printed before the bind is known to have worked.
 *
 * `readFile`, `sleep` and `now` are injected so this is testable without a
 * disk or a real clock.
 */
async function waitForPortFile(path, {
    timeoutMs = 60_000,
    intervalMs = 100,
    readFile = (p) => require('node:fs/promises').readFile(p, 'utf8'),
    sleep = (ms) => new Promise((r) => setTimeout(r, ms)),
    now = () => Date.now(),
} = {}) {
    const deadline = now() + timeoutMs;
    for (;;) {
        try {
            const url = parsePortFile(await readFile(path));
            if (url) return url;
        } catch {
            // ENOENT for as long as it is still binding — the normal case.
        }
        if (now() >= deadline) return null;
        await sleep(intervalMs);
    }
}

/**
 * Wait until the workbench actually answers, not merely until it said it
 * would.
 *
 * The CLI prints its banner when Kestrel has bound the port, and the extension
 * used to load the webview on that line alone. Binding is not serving: the
 * first request can still arrive before the pipeline is ready, and what the
 * operator then sees is a webview showing an error page for a workbench that
 * came up fine a moment later. A panel that is briefly empty is fine; one
 * showing 404 looks broken.
 *
 * Anything below 400 counts as up — a redirect is a served response, and the
 * root may well be one.
 *
 * `fetchImpl`, `sleep` and `now` are injected so this is testable without a
 * socket or a real clock.
 */
async function waitUntilServing(url, {
    timeoutMs = 30_000,
    intervalMs = 150,
    fetchImpl = globalThis.fetch,
    sleep = (ms) => new Promise((r) => setTimeout(r, ms)),
    now = () => Date.now(),
} = {}) {
    const deadline = now() + timeoutMs;
    for (;;) {
        try {
            const res = await fetchImpl(url, { method: 'GET' });
            if (res && typeof res.status === 'number' && res.status < 400) return true;
        } catch {
            // Connection refused while it is still coming up — expected.
        }
        if (now() >= deadline) return false;
        await sleep(intervalMs);
    }
}

/**
 * Where this workspace's port file goes.
 *
 * Under the extension's own storage rather than the workspace: it is our
 * bookkeeping, it would otherwise show up in the explorer and in `git status`
 * of the repo the developer is working on, and a read-only or remote
 * workspace folder still has to work.
 *
 * Named per workspace so two windows cannot overwrite each other's file. The
 * hash is only for a stable, filesystem-safe name — nothing depends on it
 * being collision-free the way a port number did, because the file is deleted
 * before each start and the URL is read from its contents.
 */
function portFilePathFor(storageRoot, workspacePath) {
    let hash = 0;
    for (const ch of String(workspacePath || 'no-folder')) {
        hash = (hash * 31 + ch.charCodeAt(0)) >>> 0;
    }
    return require('node:path').join(storageRoot, 'run', `workbench-${hash.toString(16)}.json`);
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

/**
 * What to say when a manifest-pinned Bowire will not start.
 *
 * Almost always one of two things, and they need opposite answers: the tool is
 * listed but not fetched yet (`dotnet tool restore`, one command), or there is
 * no .NET SDK on this machine at all (nothing to restore with — install the
 * CLI normally instead). Telling someone to run `restore` when they have no
 * SDK sends them in a circle.
 */
function manifestStartFailureMessage(manifestPath, detail) {
    const looksLikeMissingSdk = /not (recognized|found)|no such file|ENOENT/i.test(String(detail ?? ''));
    if (looksLikeMissingSdk) {
        return `\`${manifestPath}\` pins a Bowire version, but the .NET SDK is not available to run it. `
            + 'Install the SDK, or install Bowire directly and clear the manifest entry.';
    }
    return `Bowire is pinned in \`${manifestPath}\` but is not restored yet. `
        + 'Run `dotnet tool restore` in the workspace, then try again.';
}

module.exports = {
    BINARY,
    MINIMUM_CLI_VERSION,
    findCli,
    resolveCli,
    findToolManifest,
    pinnedToolVersion,
    hasDotnetSdk,
    manifestStartFailureMessage,
    describeSpawnError,
    expandPathVariables,
    parseCliVersion,
    compareCliVersions,
    checkCliVersion,
    readCliVersion,
    normaliseWorkbenchUrl,
    buildArgs,
    waitUntilServing,
    parsePortFile,
    waitForPortFile,
    portFilePathFor,
    buildWebviewHtml,
    missingCliMessage,
};

#!/usr/bin/env node
// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #101 — run test/smoke.js inside a real VS Code.
//
// The unit tests cover the extension's logic without VS Code; they cannot tell
// you whether VS Code can load the manifest at all, whether the command really
// registers, or whether invoking it leaves a workbench listening. That needs a
// real editor, which is what this script arranges.
//
//   node extensions/bowire-vscode/scripts/run-smoke.mjs
//
// Three details are not incidental:
//
//   * A dedicated --user-data-dir and --extensions-dir. Without them the run
//     joins whatever VS Code instance the developer already has open, which
//     both pollutes their profile and makes the result depend on their
//     installed extensions. It also means the processes this script starts can
//     be identified by that directory — so cleanup can never reach an editor
//     the developer is working in.
//   * `Code.exe` / the `code` shell script are launchers that hand off and
//     exit; their exit code says nothing. On Windows the real binary must be
//     invoked directly to stay in the foreground.
//   * The result is read from a file, not from stdout. The extension host is a
//     separate process whose output does not reliably reach this one, so a run
//     that proved everything would otherwise look like a run that never
//     started.

import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const extensionPath = resolve(here, '..');

/** Candidate locations for an installed VS Code, most specific first. */
function findVsCode() {
    const fromEnv = process.env.VSCODE_BIN;
    if (fromEnv) return existsSync(fromEnv) ? fromEnv : null;

    const candidates = process.platform === 'win32'
        ? [
            join(process.env.LOCALAPPDATA ?? '', 'Programs', 'Microsoft VS Code', 'Code.exe'),
            join(process.env.ProgramFiles ?? '', 'Microsoft VS Code', 'Code.exe'),
        ]
        : process.platform === 'darwin'
            ? ['/Applications/Visual Studio Code.app/Contents/MacOS/Electron']
            : ['/usr/share/code/code', '/usr/bin/code', '/snap/bin/code'];

    return candidates.find(c => c && existsSync(c)) ?? null;
}

/**
 * A throwaway workspace, so the run cannot touch anything that matters.
 *
 * It carries a `.bowire/project.json` opting into project-local storage
 * (#591), which is what lets the smoke test assert where the data lands.
 * Without the manifest the CLI would use the machine-wide `~/.bowire/` — the
 * correct default, and the reason the assertion has to set this up rather than
 * assume it.
 */
function makeWorkspace() {
    const dir = mkdtempSync(join(tmpdir(), 'bowire-smoke-'));
    mkdirSync(join(dir, 'reports'), { recursive: true });
    writeFileSync(join(dir, 'README.md'), '# Bowire smoke workspace\n', 'utf8');

    mkdirSync(join(dir, '.bowire'), { recursive: true });
    writeFileSync(
        join(dir, '.bowire', 'project.json'),
        JSON.stringify({ version: 1, name: 'bowire-smoke', storage: 'project' }, null, 2) + '\n',
        'utf8');

    // Point the extension at THIS checkout's CLI when one is built, via the
    // `bowire.cliPath` setting.
    //
    // Otherwise the run silently tests whatever `bowire` happens to be
    // installed on the machine — which is how this test first failed after the
    // storage work landed: PATH still held a build from earlier the same day,
    // so the assertion measured an old binary and reported a feature as
    // broken. Pinning it also means the test exercises `bowire.cliPath`
    // itself, which is otherwise only covered by unit tests.
    const localCli = resolve(extensionPath, '..', '..', 'artifacts', 'bin',
        'Kuestenlogik.Bowire.Tool', 'debug', process.platform === 'win32' ? 'bowire.exe' : 'bowire');
    if (existsSync(localCli)) {
        mkdirSync(join(dir, '.vscode'), { recursive: true });
        writeFileSync(
            join(dir, '.vscode', 'settings.json'),
            JSON.stringify({ 'bowire.cliPath': localCli }, null, 2) + '\n',
            'utf8');
        process.stdout.write(`CLI:       ${localCli}\n`);
    } else {
        process.stdout.write(`CLI:       (not built here — falling back to PATH)\n`);
    }

    return dir;
}

const code = findVsCode();
if (!code) {
    process.stderr.write(
        'No VS Code found. Install it, or point VSCODE_BIN at the executable.\n');
    process.exit(2);
}

/**
 * One run of the smoke test. `withFolder: false` opens VS Code with no
 * workspace at all, which is its own code path: the working directory then
 * comes from globalStorageUri, a directory VS Code does not create. That case
 * failed with "ENOENT" naming an executable that was plainly present, so it is
 * worth a scenario of its own rather than an assumption.
 */
function runScenario({ withFolder }) {
    const label = withFolder ? 'with a workspace folder' : 'without a workspace folder';
    const profile = mkdtempSync(join(tmpdir(), 'bowire-smoke-profile-'));
    const resultFile = join(profile, 'smoke-result.txt');
    const workspace = withFolder ? makeWorkspace() : null;

    const args = [
        `--extensionDevelopmentPath=${extensionPath}`,
        `--extensionTestsPath=${resolve(extensionPath, 'test', 'smoke.js')}`,
        // Isolation — see the header. Also what makes stray processes from
        // this run identifiable without touching the developer's own editor.
        `--user-data-dir=${profile}`,
        `--extensions-dir=${join(profile, 'extensions')}`,
        '--disable-workspace-trust',
        '--disable-gpu',
        '--skip-welcome',
        '--skip-release-notes',
        ...(workspace ? [workspace] : []),
    ];

    process.stdout.write(`\n=== ${label} ===\nProfile: ${profile}\n`);
    if (workspace) process.stdout.write(`Workspace: ${workspace}\n`);
    process.stdout.write('\n');

    const run = spawnSync(code, args, {
        encoding: 'utf8',
        stdio: 'inherit',
        // The extension host can wedge — a panel that never disposes keeps
        // VS Code alive forever. A bounded run that fails is worth more than
        // one that hangs a CI job or a terminal.
        timeout: 5 * 60_000,
        env: { ...process.env, BOWIRE_SMOKE_LOG: resultFile },
    });

    const passed = run.status === 0 && existsSync(resultFile);
    if (existsSync(resultFile)) process.stdout.write('\n' + readFileSync(resultFile, 'utf8'));

    if (run.error?.code === 'ETIMEDOUT') {
        process.stdout.write('\nVS Code did not exit within 5 minutes.\n');
    } else if (!passed) {
        process.stdout.write(`\nVS Code exited with ${run.status}, and no result file was written.\n`);
    }

    // Leave the profile behind on failure — it holds the logs that explain why.
    if (passed) {
        rmSync(profile, { recursive: true, force: true });
        if (workspace) rmSync(workspace, { recursive: true, force: true });
    } else {
        process.stdout.write(`Logs kept at ${profile}\n`);
    }

    process.stdout.write(`${label}: ${passed ? 'PASS' : 'FAIL'}\n`);
    return passed;
}

process.stdout.write(`VS Code: ${code}\n`);

// Sequential on purpose. Two editors starting at once on a cold machine race
// for CPU and make the 5-minute bound mean something different each run.
const results = [
    runScenario({ withFolder: true }),
    runScenario({ withFolder: false }),
];

const allPassed = results.every(Boolean);
process.stdout.write(allPassed ? '\nsmoke: PASS\n' : '\nsmoke: FAIL\n');
process.exit(allPassed ? 0 : 1);

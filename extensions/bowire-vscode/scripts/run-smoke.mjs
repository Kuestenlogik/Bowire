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

/** A throwaway workspace, so the run cannot touch anything that matters. */
function makeWorkspace() {
    const dir = mkdtempSync(join(tmpdir(), 'bowire-smoke-'));
    mkdirSync(join(dir, 'reports'), { recursive: true });
    writeFileSync(join(dir, 'README.md'), '# Bowire smoke workspace\n', 'utf8');
    return dir;
}

const code = findVsCode();
if (!code) {
    process.stderr.write(
        'No VS Code found. Install it, or point VSCODE_BIN at the executable.\n');
    process.exit(2);
}

const workspace = makeWorkspace();
const profile = mkdtempSync(join(tmpdir(), 'bowire-smoke-profile-'));
const resultFile = join(profile, 'smoke-result.txt');

const args = [
    `--extensionDevelopmentPath=${extensionPath}`,
    `--extensionTestsPath=${resolve(extensionPath, 'test', 'smoke.js')}`,
    // Isolation — see the header. Also what makes stray processes from this
    // run identifiable without touching the developer's own editor.
    `--user-data-dir=${profile}`,
    `--extensions-dir=${join(profile, 'extensions')}`,
    '--disable-workspace-trust',
    '--disable-gpu',
    '--skip-welcome',
    '--skip-release-notes',
    workspace,
];

process.stdout.write(`VS Code:   ${code}\nWorkspace: ${workspace}\nProfile:   ${profile}\n\n`);

const run = spawnSync(code, args, {
    encoding: 'utf8',
    stdio: 'inherit',
    // The extension host can wedge — a panel that never disposes keeps VS Code
    // alive forever. A bounded run that fails is worth more than one that
    // hangs a CI job or a terminal.
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
    rmSync(workspace, { recursive: true, force: true });
} else {
    process.stdout.write(`Logs kept at ${profile}\n`);
}

process.stdout.write(passed ? '\nsmoke: PASS\n' : '\nsmoke: FAIL\n');
process.exit(passed ? 0 : 1);

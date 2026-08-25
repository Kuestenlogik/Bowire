// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

'use strict';

const path = require('node:path');
const os = require('node:os');
const fs = require('node:fs/promises');

const EXTENSION_ID = 'kuestenlogik.bowire-vscode';

/**
 * Where the managed CLI might be sitting, most likely first.
 *
 * Run as `vscode:uninstall`, this script is a plain Node process: there is no
 * extension host, so no `context.globalStorageUri` to ask. The path has to be
 * reconstructed, and it varies by platform, by build (stable, Insiders,
 * VSCodium), and by whether the install is portable. Hence a candidate list
 * rather than a single answer — every one that exists gets cleaned, because a
 * developer with both stable and Insiders has genuinely paid for two copies.
 *
 * Pure, so the layout can be asserted without touching a disk.
 */
function candidateStorageDirs({
    platform = process.platform,
    env = process.env,
    home = os.homedir(),
} = {}) {
    // Portable mode puts everything under one directory and is unambiguous,
    // so it answers on its own rather than joining the guesswork.
    if (env.VSCODE_PORTABLE)
        return [path.join(env.VSCODE_PORTABLE, 'user-data', 'User', 'globalStorage', EXTENSION_ID)];

    const products = ['Code', 'Code - Insiders', 'VSCodium', 'Cursor', 'Windsurf'];

    const base = platform === 'win32'
        ? (env.APPDATA || path.join(home, 'AppData', 'Roaming'))
        : platform === 'darwin'
            ? path.join(home, 'Library', 'Application Support')
            : (env.XDG_CONFIG_HOME || path.join(home, '.config'));

    return products.map((p) => path.join(base, p, 'User', 'globalStorage', EXTENSION_ID));
}

/**
 * Guard against deleting anything that is not ours.
 *
 * This script runs unattended with whatever the user's shell can reach, and
 * it deletes recursively. A wrong `base` — an empty APPDATA, a home directory
 * that resolved to `/` — would otherwise turn into a recursive delete of
 * something that matters. Nothing is removed unless the path ends in exactly
 * the two segments this extension owns.
 */
function isOurStorageDir(dir) {
    if (typeof dir !== 'string' || !dir) return false;

    // Deliberately no path.resolve(): it answers for the host platform, and
    // this predicate has to judge a POSIX path correctly while running on
    // Windows (and the reverse) or its tests prove nothing. `/home/x/...`
    // through resolve() on Windows comes back as a UNC path with a different
    // segment count. The shape is what matters, so read the shape.
    const parts = dir.split(/[\\/]+/).filter(Boolean);

    // A relative path is not something we reconstructed, and `..` could walk
    // out of the directory the rest of this check just approved.
    if (parts.includes('..')) return false;

    return parts.length >= 4
        && parts[parts.length - 1] === EXTENSION_ID
        && parts[parts.length - 2] === 'globalStorage';
}

/**
 * Remove the downloaded CLI copies this extension made.
 *
 * Only the `cli/` subtree, not the whole storage directory: settings-adjacent
 * state we may keep there later is the user's, and an uninstall is not
 * necessarily permanent. The CLI is different — it is ~120 MB the extension
 * fetched on the user's behalf, in a directory they will never think to look
 * in, and it is worthless without the extension that pinned it.
 *
 * A CLI the user installed themselves (winget, choco, dotnet tool, a path in
 * `bowire.cliPath`) is never touched: it lives outside this directory
 * entirely, and it was not ours to install or to remove.
 */
async function removeManagedCli({ dirs = candidateStorageDirs(), rm = fs.rm } = {}) {
    const removed = [];
    for (const dir of dirs) {
        if (!isOurStorageDir(dir)) continue;
        const cliDir = path.join(dir, 'cli');
        try {
            await rm(cliDir, { recursive: true, force: true });
            removed.push(cliDir);
        } catch {
            // Best effort by design. An uninstall that fails loudly because a
            // file was locked helps nobody: VS Code has already gone, there is
            // no UI to report into, and a non-zero exit here is a scary
            // message about an operation the user asked to be over.
        }
    }
    return removed;
}

module.exports = { EXTENSION_ID, candidateStorageDirs, isOurStorageDir, removeManagedCli };

if (require.main === module) removeManagedCli();

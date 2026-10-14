#!/usr/bin/env node
// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #647 - everything in this repository is Apache-2.0. Bowire is a security tool, and for a
// security tool inspectable checking logic is the strongest argument for trust; paid parts, if
// they ever come, would be separate modules with a licence of their own, decided when they exist.
//
// With one licence there is nothing to keep apart, so this is not the per-module table other
// Kuestenlogik repositories carry. It keeps the one decision true:
//   - every tracked C# file names Apache-2.0 in an SPDX header at its top;
//   - no project or props file packs a licence of its own - the root Directory.Build.props packs
//     the root LICENSE into every package;
//   - the root LICENSE is the Apache License, Version 2.0.
//
//   node scripts/ci/check-license.mjs            # the whole repository
//   node scripts/ci/check-license.mjs <file>...  # just these files

import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..');

const LICENSE_ID = 'Apache-2.0';
const HEADER_LINES = 10;
const SPDX = /SPDX-License-Identifier:\s*(\S+)/;
const BUILD_FILE = /(\.csproj|\.props|\.targets)$/i;
const LICENSE_PROPERTY = /<(PackageLicenseExpression|PackageLicenseFile)>\s*([^<]*?)\s*</;

/** Why a C# file's header is wrong, or null when it names Apache-2.0. */
export function sourceFinding(text) {
    const top = text.replace(/^﻿/, '').split('\n').slice(0, HEADER_LINES).join('\n');
    const match = SPDX.exec(top);
    if (!match) return `no SPDX-License-Identifier in the first ${HEADER_LINES} lines; add \`// SPDX-License-Identifier: ${LICENSE_ID}\``;
    return match[1] === LICENSE_ID ? null : `declares ${match[1]}; this repository is ${LICENSE_ID} (#647)`;
}

/** Why a project or props file's licence properties are wrong, or null. */
export function buildFileFinding(file, text) {
    const match = LICENSE_PROPERTY.exec(text);
    if (file === 'Directory.Build.props') {
        return match && match[1] === 'PackageLicenseFile' && match[2] === 'LICENSE'
            ? null
            : 'the root Directory.Build.props has to pack the root LICENSE: <PackageLicenseFile>LICENSE</PackageLicenseFile>';
    }
    return match
        ? `sets ${match[1]} (${match[2]}); every package ships the root LICENSE from Directory.Build.props (#647)`
        : null;
}

/** Why the root LICENSE is wrong, or null when it is the Apache licence. */
export function licenseFileFinding(text) {
    return /Apache License/.test(text) && /Version 2\.0/.test(text)
        ? null
        : 'the root LICENSE is not the Apache License, Version 2.0';
}

/** All findings over the given files (repository-relative paths). */
export function scan(files, { root = repoRoot } = {}) {
    const findings = [];
    for (const file of files) {
        let check;
        if (file === 'LICENSE') check = text => licenseFileFinding(text);
        else if (file.endsWith('.cs')) check = text => sourceFinding(text);
        else if (BUILD_FILE.test(file)) check = text => buildFileFinding(file, text);
        else continue;

        let text;
        try { text = readFileSync(path.join(root, file), 'utf8'); } catch { continue; }
        const reason = check(text);
        if (reason) findings.push({ file, reason });
    }
    return findings;
}

export function trackedFiles(root = repoRoot) {
    return execFileSync('git', ['ls-files', '-z'], { cwd: root, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 })
        .split('\0').filter(Boolean);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const args = process.argv.slice(2);
    const files = args.length ? args.map(a => path.relative(repoRoot, path.resolve(a)).split(path.sep).join('/')) : trackedFiles();
    const findings = scan(files);
    for (const f of findings) console.log(`${f.file}: ${f.reason}`);
    if (findings.length) {
        console.error(`\n${findings.length} file(s) do not match the licence decision in #647: everything here is ${LICENSE_ID}.`);
        process.exit(1);
    }
    console.log(`${LICENSE_ID} throughout: ${files.length} file(s) checked.`);
}

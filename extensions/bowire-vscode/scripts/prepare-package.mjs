#!/usr/bin/env node
// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #101 — put the files vsce needs inside the extension folder at package time.
//
// Two assets, same reasoning: vsce only packs what lives under the extension
// directory, but the repo already has one canonical copy of each, and a
// committed duplicate is a second file to keep in step by hand. So they are
// copied in at package time and .gitignore keeps the copies out of the tree.
//
//   images/bowire_logo_small.png → icon.png   (the same file NuGet packs)
//   LICENSE                      → LICENSE    (vsce warns without it, and a
//                                              marketplace listing with no
//                                              licence is a listing nobody
//                                              in a company can adopt)
//
// Fails loudly rather than shipping something incomplete: a moved or renamed
// source should break the build, not quietly produce a blank marketplace tile
// or an unlicensed package nobody notices until it is published.
//
// This was `copy-icon.mjs` until the licence joined it; the name now says what
// it does rather than what it did first.

import { copyFileSync, existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));

/** The one canonical icon, shared with the NuGet packages. */
export const SOURCE = resolve(here, '../../../images/bowire_logo_small.png');

/** Where vsce expects it. */
export const TARGET = resolve(here, '../icon.png');

/** The repository licence — the same Apache-2.0 text every package ships. */
export const LICENSE_SOURCE = resolve(here, '../../../LICENSE');
export const LICENSE_TARGET = resolve(here, '../LICENSE');

/** Width and height from a PNG's IHDR chunk. */
export function pngSize(path) {
    const head = readFileSync(path).subarray(0, 24);
    const isPng = head.subarray(0, 8).equals(Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]));
    if (!isPng || head.subarray(12, 16).toString('ascii') !== 'IHDR') return null;
    return { width: head.readUInt32BE(16), height: head.readUInt32BE(20) };
}

export function copyIcon() {
    if (!existsSync(SOURCE)) {
        throw new Error(`Marketplace icon source is missing: ${SOURCE}`);
    }
    const size = pngSize(SOURCE);
    if (!size) throw new Error(`${SOURCE} is not a PNG.`);
    // The marketplace rejects anything under 128x128 and squashes non-square
    // art, so check here rather than discovering it during publish.
    if (size.width < 128 || size.height < 128 || size.width !== size.height) {
        throw new Error(`Marketplace icon must be square and at least 128x128; got ${size.width}x${size.height}.`);
    }
    copyFileSync(SOURCE, TARGET);
    return { source: SOURCE, target: TARGET, ...size };
}

export function copyLicense() {
    if (!existsSync(LICENSE_SOURCE)) {
        throw new Error(`Licence source is missing: ${LICENSE_SOURCE}`);
    }
    copyFileSync(LICENSE_SOURCE, LICENSE_TARGET);
    return { source: LICENSE_SOURCE, target: LICENSE_TARGET };
}

// Only act when run directly, so the tests can import the checks.
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const icon = copyIcon();
    process.stdout.write(`icon ${icon.width}x${icon.height} copied to ${icon.target}\n`);
    const licence = copyLicense();
    process.stdout.write(`licence copied to ${licence.target}\n`);
}

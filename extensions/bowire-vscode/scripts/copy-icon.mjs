#!/usr/bin/env node
// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #101 — put the marketplace icon in place at package time.
//
// vsce requires the icon to live inside the extension folder, but committing
// a second copy of the logo would be a duplicate to keep in step by hand.
// The repo already has one canonical image — images/bowire_logo_small.png,
// the same file NuGet packs as icon.png — so this copies it in at package
// time and .gitignore keeps the copy out of the tree.
//
// Fails loudly rather than shipping an icon-less package: a moved or renamed
// source should break the build, not quietly produce a blank marketplace
// tile nobody notices until it is published.

import { copyFileSync, existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));

/** The one canonical icon, shared with the NuGet packages. */
export const SOURCE = resolve(here, '../../../images/bowire_logo_small.png');

/** Where vsce expects it. */
export const TARGET = resolve(here, '../icon.png');

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

// Only act when run directly, so the test can import the checks.
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const result = copyIcon();
    process.stdout.write(`icon ${result.width}x${result.height} copied to ${result.target}\n`);
}

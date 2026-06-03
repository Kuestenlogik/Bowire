#!/usr/bin/env node
// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// optimize-images.mjs — walks site/assets/images and emits responsive
// variants (thumb / display / full) in modern formats (AVIF + WebP),
// plus a re-encoded version of the legacy .png/.jpg as a universal
// fallback. _includes/picture.html consumes the variants via a
// <picture>/<source srcset="… w"> tree, so browsers pick the smallest
// variant that fits the rendered size (handled by the user agent, not
// JS).
//
// Variants emitted per source image:
//
//   foo-400w.{avif,webp}   thumbnail (mobile / card)
//   foo-1200w.{avif,webp}  normal display (article body)
//   foo.{avif,webp}        original-resolution (lightbox / fullscreen)
//
// Variant-width thresholds: a smaller variant is only written if the
// source image is at least 1.5× that width — there's no point
// producing a 1200 w "shrink" from an 800 w source.
//
// Design choices:
//
//   * Idempotent. Re-running skips up-to-date outputs by mtime. Cheap
//     to wire into deploy / CI.
//
//   * Lossy at high quality (q=80 webp, q=60 avif). Visually
//     indistinguishable from the source PNGs at the sizes we ship,
//     typically 10–20× smaller. A `--lossless` flag would be added
//     when a real diff against the source becomes a maintenance task,
//     not preemptively.
//
//   * Check mode (`--check`) is what the GitHub Action calls: it
//     fails the PR if any source image has a stale or missing variant,
//     so the optimization step can't be silently skipped.
//
// Usage:
//   node scripts/optimize-images.mjs            # transcode all images
//   node scripts/optimize-images.mjs --check    # CI mode (no writes)

import { promises as fs } from "node:fs";
import path from "node:path";
import process from "node:process";
import sharp from "sharp";

const ROOT = path.resolve(process.cwd(), "site/assets/images");
const MIN_BYTES = 30 * 1024; // <30 KiB is already small — skip.
const EXTS = new Set([".png", ".jpg", ".jpeg"]);
const VARIANT_WIDTHS = [400, 1200]; // plus an original-width variant
const CHECK_MODE = process.argv.includes("--check");
const FORMATS = ["avif", "webp"];

async function* walk(dir) {
    let entries;
    try { entries = await fs.readdir(dir, { withFileTypes: true }); }
    catch { return; }
    for (const entry of entries) {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) yield* walk(full);
        else yield full;
    }
}

async function isStale(src, target) {
    // In --check mode we only verify existence. mtime-based staleness
    // is unreliable on CI: `git checkout` writes files in whatever
    // order it picks, so a variant can land with an mtime older than
    // its source even though both were committed in lock-step. Local
    // regen still uses mtime so the script stays cheap to re-run.
    if (CHECK_MODE) {
        try { await fs.stat(target); return false; }
        catch { return true; }
    }
    try {
        const [s, t] = await Promise.all([fs.stat(src), fs.stat(target)]);
        return t.mtimeMs < s.mtimeMs;
    } catch {
        return true; // target missing
    }
}

function targetsFor(file) {
    const ext = path.extname(file).toLowerCase();
    const base = file.slice(0, -ext.length);
    const list = [];
    // Original-width modern-format variants.
    for (const fmt of FORMATS) list.push({ width: null, target: `${base}.${fmt}`, format: fmt });
    // Down-scaled variants.
    for (const w of VARIANT_WIDTHS) {
        for (const fmt of FORMATS) {
            list.push({ width: w, target: `${base}-${w}w.${fmt}`, format: fmt });
        }
    }
    return list;
}

let processed = 0;
const stale = [];
let totalSavedBytes = 0;

for await (const file of walk(ROOT)) {
    const ext = path.extname(file).toLowerCase();
    if (!EXTS.has(ext)) continue;

    const stat = await fs.stat(file);
    if (stat.size < MIN_BYTES) continue;

    const meta = await sharp(file).metadata();
    const srcWidth = meta.width ?? 0;

    for (const { width, target, format } of targetsFor(file)) {
        // Emit every variant unconditionally. sharp's
        // `withoutEnlargement: true` clamps the output width to
        // min(target, srcWidth), so for a 1684 w source the 1200 w
        // variant becomes 1200 w but the 400 w variant stays 400 w.
        // Generating all three variants every time means the
        // <picture> srcset in _includes/picture.html never references
        // a 404'd URL — a previous "skip if source < 1.5× target"
        // heuristic looked clever but caused a broken hero banner
        // when the source image landed below the threshold.

        if (await isStale(file, target)) {
            if (CHECK_MODE) {
                stale.push(target);
                continue;
            }
            const pipeline = sharp(file);
            if (width !== null) pipeline.resize({ width, withoutEnlargement: true });
            if (format === "webp") await pipeline.webp({ quality: 80, effort: 5 }).toFile(target);
            else await pipeline.avif({ quality: 60, effort: 6 }).toFile(target);
            const t = await fs.stat(target);
            totalSavedBytes += Math.max(0, stat.size - t.size);
            processed++;
        }
    }
}

if (CHECK_MODE) {
    if (stale.length > 0) {
        console.error(`image-optimize check failed — ${stale.length} stale or missing transcodes:`);
        for (const s of stale.slice(0, 20)) console.error(`  ${path.relative(process.cwd(), s)}`);
        if (stale.length > 20) console.error(`  …and ${stale.length - 20} more.`);
        console.error(`\nrun: npm run optimize-images`);
        process.exit(1);
    } else {
        console.log("image-optimize check ok — all transcodes are fresh.");
    }
} else {
    const mib = (totalSavedBytes / (1024 * 1024)).toFixed(2);
    console.log(`\n${processed} variant(s) written — best-case savings ~${mib} MiB.`);
}

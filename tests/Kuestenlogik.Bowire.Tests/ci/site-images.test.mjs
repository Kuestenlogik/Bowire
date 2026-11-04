// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #969 - the site serves its raster images through the AVIF/WebP pipeline
// (scripts/site/optimize-images.mjs): every <picture> the includes render
// points at files that exist, and no page puts a PNG or JPEG in a bare <img>.
// The link checker reads the sources and cannot see a srcset that Liquid
// builds, so a missing variant would otherwise surface only as a 404 on
// bowire.io and a failed Lighthouse audit.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, relative, resolve } from 'node:path';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const site = join(root, 'site');

// The two includes hold the only <img> with a raster fallback, by design.
const INCLUDES = new Set(['_includes/picture.html', '_includes/themed-img.html']);

function pages(dir = site) {
    const out = [];
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
        if (entry.name.startsWith('_site') || entry.name === 'node_modules' || entry.name === '.jekyll-cache') continue;
        const full = join(dir, entry.name);
        if (entry.isDirectory()) out.push(...pages(full));
        else if (/\.(html|md)$/.test(entry.name)) out.push(full);
    }
    return out;
}

const sources = pages().map(file => ({
    file: relative(site, file).split('\\').join('/'),
    text: readFileSync(file, 'utf8'),
}));

const onDisk = url => existsSync(join(site, url));

function uses(include) {
    const pattern = new RegExp(`\\{%-?\\s*include ${include.replace('.', '\\.')}\\s+src="([^"]+)"`, 'g');
    // The includes' own usage comments are examples, not images.
    return sources
        .filter(({ file }) => !INCLUDES.has(file))
        .flatMap(({ file, text }) => [...text.matchAll(pattern)].map(m => ({ file, src: m[1] })));
}

describe('site images (#969)', () => {
    it('themed-img.html finds both themes in AVIF and WebP at 400 and 1200 px', () => {
        const found = uses('themed-img.html');
        assert.ok(found.length > 20, 'the screenshots are rendered through themed-img.html');
        const missing = [];
        for (const { file, src } of found) {
            const stem = src.replace(/\.png$/, '');
            const needed = [`${stem}-light.png`];
            for (const theme of ['dark', 'light'])
                for (const width of ['400w', '1200w'])
                    for (const format of ['avif', 'webp']) needed.push(`${stem}-${theme}-${width}.${format}`);
            for (const url of needed) if (!onDisk(url)) missing.push(`${file}: ${url}`);
        }
        assert.deepEqual(missing, [], 'run `npm run optimize-images` for the new screenshot');
    });

    it('picture.html finds AVIF and WebP at 400 and 1200 px', () => {
        const found = uses('picture.html');
        assert.ok(found.length > 5, 'the photos are rendered through picture.html');
        const missing = [];
        for (const { file, src } of found) {
            const stem = src.replace(/\.(png|jpe?g)$/, '');
            const needed = [src];
            for (const width of ['400w', '1200w'])
                for (const format of ['avif', 'webp']) needed.push(`${stem}-${width}.${format}`);
            for (const url of needed) if (!onDisk(url)) missing.push(`${file}: ${url}`);
        }
        assert.deepEqual(missing, [], 'run `npm run optimize-images` for the new image');
    });

    it('no page shows a PNG or JPEG in a bare <img>', () => {
        const bare = sources
            .filter(({ file }) => !INCLUDES.has(file))
            .flatMap(({ file, text }) =>
                [...text.matchAll(/<img\b[^>]*\bsrc="[^"]*\.(png|jpe?g)\b[^"]*"/gi)].map(m => `${file}: ${m[0]}`));
        assert.deepEqual(bare, [],
            'render it with {% include picture.html %} (or themed-img.html for a screenshot with two themes)');
    });

    it('a video poster is a WebP variant, not the PNG', () => {
        const posters = sources.flatMap(({ file, text }) =>
            [...text.matchAll(/poster="\{\{\s*'([^']+)'/g)].map(m => ({ file, url: m[1] })));
        for (const { file, url } of posters) {
            assert.match(url, /\.webp$/, `${file}: ${url}`);
            assert.ok(onDisk(url), `${file}: ${url} exists`);
        }
    });
});

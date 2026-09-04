#!/usr/bin/env node
// Pull each protocol plugin's own page into docs/protocols/ before DocFX runs.
//
// Run from the repository root:
//   node scripts/docs/fetch-plugin-docs.mjs
//
// What it guarantees, and why each one matters:
//
//   * **One file per repository, at a path this script chooses.** The
//     destination comes from the repository name via slugFor(); nothing the
//     plugin sends can redirect it. A plugin cannot overwrite another plugin's
//     page, or anything else under docs/, however its own repository is laid
//     out.
//   * **A failure degrades, it does not block.** An unreachable repository
//     leaves the copy already committed here in place and emits a warning. One
//     plugin having a bad day must not stop the whole site from deploying —
//     the same lesson the publish workflows learned the hard way, where a
//     guard that could not skip turned an unconfigured channel into a red
//     release.
//   * **It says what it did.** Every page reports fetched / unchanged /
//     fallback, so "the site is stale" is answerable from the log rather than
//     by diffing the deployed HTML.

import { writeFile, readFile, access } from 'node:fs/promises';
import { join } from 'node:path';
import { discoverPluginRepos, SOURCE_PATH, ORG } from './plugin-docs.mjs';

const DEST_DIR = join('docs', 'protocols');

async function readIfPresent(path) {
    try {
        await access(path);
        return await readFile(path, 'utf8');
    } catch {
        return null;
    }
}

async function main() {
    const token = process.env.GITHUB_TOKEN;
    const headers = token ? { Authorization: `Bearer ${token}` } : {};

    let repos;
    try {
        repos = await discoverPluginRepos(fetch, token);
    } catch (err) {
        // Discovery itself failing is the one case where continuing is
        // pointless — every page would fall back at once. Still not fatal: the
        // committed copies are all present, so the site builds as it did
        // before this script existed.
        console.log(`::warning::plugin-doc discovery failed (${err.message}) — every page falls back to its committed copy`);
        return;
    }

    console.log(`Discovered ${repos.length} protocol plugin repositories.`);

    let fetched = 0, unchanged = 0, fallback = 0;

    for (const { repo, slug, branch } of repos) {
        const url = `https://raw.githubusercontent.com/${ORG}/${repo}/${branch}/${SOURCE_PATH}`;
        const dest = join(DEST_DIR, `${slug}.md`);
        const committed = await readIfPresent(dest);

        let res;
        try {
            res = await fetch(url, { headers });
        } catch (err) {
            console.log(`::warning::${repo}: ${err.message} — keeping the committed ${slug}.md`);
            fallback++;
            continue;
        }

        if (res.status === 404) {
            // Not every plugin repository documents itself yet. That is a gap,
            // not an error: say so once and move on.
            console.log(`  ${slug.padEnd(12)} no ${SOURCE_PATH} in ${repo} — keeping the committed copy`);
            fallback++;
            continue;
        }
        if (!res.ok) {
            console.log(`::warning::${repo}: ${res.status} ${res.statusText} — keeping the committed ${slug}.md`);
            fallback++;
            continue;
        }

        const body = await res.text();
        if (!body.trimStart().startsWith('---')) {
            // The toc generator reads `title` out of the front matter. A page
            // without it would land in the navigation as a bare slug, which
            // looks like a broken build rather than a missing header.
            console.log(`::warning::${repo}: ${SOURCE_PATH} has no YAML front matter — keeping the committed ${slug}.md`);
            fallback++;
            continue;
        }

        if (body === committed) {
            console.log(`  ${slug.padEnd(12)} unchanged`);
            unchanged++;
            continue;
        }

        await writeFile(dest, body, 'utf8');
        console.log(`  ${slug.padEnd(12)} fetched from ${repo}@${branch}`);
        fetched++;
    }

    console.log(`\n${fetched} fetched, ${unchanged} unchanged, ${fallback} fell back to the committed copy.`);
}

await main();

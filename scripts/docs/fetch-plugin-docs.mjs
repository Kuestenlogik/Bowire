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
//   * **A failure is visible, and temporary.** An unreachable repository gets a
//     placeholder page saying so, with a link to its source. The URL and the
//     navigation entry survive, and the next build that reaches the repository
//     replaces it with the real page — nothing about the failure is written
//     down, so nothing about it persists.
//
//     There used to be a committed copy of each page here to fall back on.
//     That failed *silently into staleness*: an unreachable plugin that had
//     changed served old content with nothing going red. It also drifted
//     within hours of being introduced — raw.githubusercontent.com serves a
//     cached response for a few minutes after a push, so a re-fetch reported
//     "unchanged" while the plugin had in fact been edited. Two copies of one
//     truth is the very thing this move exists to remove.
//   * **It says what it did.** Every page reports fetched or placeholder, so
//     "is the site showing the real page?" is answerable from the build log
//     rather than by reading the deployed HTML.

import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { discoverPluginRepos, SOURCE_PATH, ORG } from './plugin-docs.mjs';

const DEST_DIR = join('docs', 'protocols');

/**
 * The page that stands in when a plugin's own copy cannot be read.
 *
 * Deliberately not a copy of anything: it says what happened, names the
 * repository, and links to the source. A reader learns the page is missing
 * rather than reading something that quietly stopped being true, and the next
 * successful build overwrites it.
 */
function placeholder(slug, repo, reason) {
    return `---
title: ${slug}
summary: 'This page is contributed by ${repo} and could not be fetched for this build.'
---

# ${slug}

> [!WARNING]
> **This page could not be fetched for this build.**
>
> It is contributed by
> [\`${ORG}/${repo}\`](https://github.com/${ORG}/${repo}/blob/main/${SOURCE_PATH}),
> which was unreachable when the site was last built (${reason}).
>
> Nothing is wrong with the plugin — only with this build's ability to read
> its documentation. Follow the link above for the current text; the next
> build that reaches the repository restores this page automatically.
`;
}

async function main() {
    const token = process.env.GITHUB_TOKEN;
    const headers = token ? { Authorization: `Bearer ${token}` } : {};

    let repos;
    try {
        repos = await discoverPluginRepos(fetch, token);
    } catch (err) {
        // Without discovery there is no list of slugs, so not even placeholders
        // can be written — the pages simply will not exist for this build, and
        // the navigation will not list them. Loud, and self-correcting on the
        // next run.
        console.log(`::error::plugin-doc discovery failed (${err.message}) — no plugin protocol pages in this build`);
        return;
    }

    console.log(`Discovered ${repos.length} protocol plugin repositories.`);

    let fetched = 0, missing = 0;

    for (const { repo, slug, branch } of repos) {
        const url = `https://raw.githubusercontent.com/${ORG}/${repo}/${branch}/${SOURCE_PATH}`;
        const dest = join(DEST_DIR, `${slug}.md`);

        let res;
        try {
            res = await fetch(url, { headers });
        } catch (err) {
            await writeFile(dest, placeholder(slug, repo, err.message), 'utf8');
            console.log(`::warning::${repo}: ${err.message} — ${slug}.md is a placeholder for this build`);
            missing++;
            continue;
        }

        if (res.status === 404) {
            // Not every plugin repository documents itself yet. That is a gap,
            // not an error — but it still gets a page, because the navigation
            // entry exists either way and a 404 explains less than a sentence.
            await writeFile(dest, placeholder(slug, repo, `no ${SOURCE_PATH} in that repository`), 'utf8');
            console.log(`  ${slug.padEnd(12)} no ${SOURCE_PATH} in ${repo} — placeholder`);
            missing++;
            continue;
        }
        if (!res.ok) {
            await writeFile(dest, placeholder(slug, repo, `${res.status} ${res.statusText}`), 'utf8');
            console.log(`::warning::${repo}: ${res.status} ${res.statusText} — ${slug}.md is a placeholder for this build`);
            missing++;
            continue;
        }

        const body = await res.text();
        if (!body.trimStart().startsWith('---')) {
            // The toc generator reads `title` out of the front matter, so a
            // page without it would appear in the navigation as a bare slug —
            // indistinguishable from a broken build. Say which it is.
            await writeFile(dest, placeholder(slug, repo, `${SOURCE_PATH} has no YAML front matter`), 'utf8');
            console.log(`::warning::${repo}: ${SOURCE_PATH} has no YAML front matter — placeholder`);
            missing++;
            continue;
        }

        await writeFile(dest, body, 'utf8');
        console.log(`  ${slug.padEnd(12)} fetched from ${repo}@${branch}`);
        fetched++;
    }

    console.log(`\n${fetched} fetched, ${missing} placeholder(s).`);
    if (missing > 0) {
        console.log('::warning::Some protocol pages are placeholders. They are regenerated every '
            + 'build, so the next run that reaches those repositories restores them.');
    }
}

await main();

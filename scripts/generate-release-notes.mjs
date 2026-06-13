#!/usr/bin/env node
// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// generate-release-notes.mjs — render RELEASE_NOTES.md from GitHub Releases.
//
// GitHub Releases at https://github.com/Kuestenlogik/Bowire/releases are
// the single source of truth for the curated release notes. This script
// queries the REST API, walks every published release, and writes a
// human-readable Markdown mirror so the notes are readable offline
// (in `git`, in editors, in renders that don't pull live API data).
//
// Mirror, not edit:
//   - Editorial content lives in the GitHub Release body — set via
//     the UI, `gh release edit`, or the publish workflow.
//   - Pre-release drafts can be staged ahead of a tag with
//     `gh release create vX.Y.Z --draft --notes-file <path>` and
//     they're picked up by this generator when --include-drafts is set
//     so the file mirror surfaces the upcoming release for review.
//   - Auto-generated commit / PR lists from the publish workflow
//     come along with the editorial body in the same release entry.
//
// Auth: any token with `read:repo` on Kuestenlogik/Bowire. In CI the
// workflow's GITHUB_TOKEN suffices; locally use a PAT or
// `gh auth token` piped via GH_TOKEN.
//
// Usage:
//   node scripts/generate-release-notes.mjs                  # write RELEASE_NOTES.md
//   node scripts/generate-release-notes.mjs --check          # exit 1 if stale
//   node scripts/generate-release-notes.mjs --stdout         # write to stdout
//   node scripts/generate-release-notes.mjs --include-drafts # include drafts (for v2.0 staging)

import { execSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import process from "node:process";

function resolveToken() {
    if (process.env.GH_TOKEN) return process.env.GH_TOKEN;
    if (process.env.GITHUB_TOKEN) return process.env.GITHUB_TOKEN;
    try { return execSync("gh auth token", { stdio: ["ignore", "pipe", "ignore"] }).toString().trim(); }
    catch { return null; }
}

const TOKEN = resolveToken();
if (!TOKEN) {
    console.error("No GitHub token. Set GH_TOKEN or run `gh auth login` first.");
    process.exit(1);
}

const OWNER = "Kuestenlogik";
const REPO = "Bowire";
const TARGET_FILE = "RELEASE_NOTES.md";

const CHECK_MODE = process.argv.includes("--check");
const STDOUT_MODE = process.argv.includes("--stdout");
const INCLUDE_DRAFTS = process.argv.includes("--include-drafts");

async function ghRest(path) {
    const res = await fetch(`https://api.github.com${path}`, {
        headers: {
            "Authorization": `Bearer ${TOKEN}`,
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "bowire-release-notes-generator",
        },
    });
    if (!res.ok) {
        console.error(`GitHub REST ${path} → HTTP ${res.status}: ${await res.text()}`);
        process.exit(1);
    }
    return res.json();
}

async function fetchAllReleases() {
    const all = [];
    let page = 1;
    while (true) {
        const batch = await ghRest(`/repos/${OWNER}/${REPO}/releases?per_page=100&page=${page}`);
        if (!Array.isArray(batch) || batch.length === 0) break;
        all.push(...batch);
        if (batch.length < 100) break;
        page++;
    }
    return all;
}

// Sort newest first. Drafts have published_at = null, so they sort to
// the top by created_at when included.
function sortReleases(releases) {
    return releases.slice().sort((a, b) => {
        const aWhen = a.published_at || a.created_at;
        const bWhen = b.published_at || b.created_at;
        return new Date(bWhen).getTime() - new Date(aWhen).getTime();
    });
}

function formatDate(iso) {
    if (!iso) return "";
    return iso.slice(0, 10); // YYYY-MM-DD
}

function renderRelease(release) {
    const tag = release.tag_name;
    const name = (release.name || tag).trim();
    // Strip the leading tag prefix from the name if the GH title is
    // "vX.Y.Z — theme" — we render the tag + theme separately in the
    // heading so it lines up with the historical RELEASE_NOTES style.
    let theme = "";
    if (name.startsWith(tag)) {
        const rest = name.slice(tag.length).replace(/^[\s—\-:]+/, "").trim();
        if (rest) theme = rest;
    } else {
        theme = name;
    }
    const dateLabel = release.draft ? "draft" : formatDate(release.published_at);
    const heading = theme
        ? `## ${tag} — ${dateLabel} — ${theme}`
        : `## ${tag} — ${dateLabel}`;

    const body = (release.body || "").trim();
    const lines = [heading, ""];
    if (release.draft) {
        lines.push(`> _Draft release — body subject to change before publication. Source: ${release.html_url}_`);
        lines.push("");
    }
    if (body) {
        lines.push(body);
    } else {
        lines.push("_No editorial notes — see the auto-generated change list at the release page._");
    }
    lines.push("");
    lines.push("---");
    lines.push("");
    return lines.join("\n");
}

function renderHeader() {
    return [
        "# Bowire Release Notes",
        "",
        "**This file is generated.** Edit the release body on GitHub instead:",
        `https://github.com/${OWNER}/${REPO}/releases`,
        "",
        "The script `scripts/generate-release-notes.mjs` pulls every published",
        "release (and optionally drafts via `--include-drafts`) and writes",
        "the body out here so the notes are readable offline. Use the",
        "GitHub Release UI or `gh release edit <tag>` to change the editorial",
        "text; re-run the generator to refresh the mirror.",
        "",
        "---",
        "",
    ].join("\n");
}

async function main() {
    const releases = await fetchAllReleases();
    const visible = releases.filter(r => INCLUDE_DRAFTS || !r.draft);
    const sorted = sortReleases(visible);
    const body = renderHeader() + sorted.map(renderRelease).join("");

    if (STDOUT_MODE) {
        process.stdout.write(body);
        return;
    }
    if (CHECK_MODE) {
        let current = "";
        try { current = readFileSync(TARGET_FILE, "utf8"); } catch { /* missing */ }
        if (current !== body) {
            console.error(`${TARGET_FILE} is out of date — re-run scripts/generate-release-notes.mjs to refresh.`);
            process.exit(1);
        }
        console.log(`${TARGET_FILE} is up to date.`);
        return;
    }
    writeFileSync(TARGET_FILE, body, "utf8");
    console.log(`Wrote ${TARGET_FILE} — ${sorted.length} release${sorted.length === 1 ? "" : "s"} mirrored.`);
}

main().catch(err => { console.error(err); process.exit(1); });

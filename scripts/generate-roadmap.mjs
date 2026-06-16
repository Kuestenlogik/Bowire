#!/usr/bin/env node
// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// generate-roadmap.mjs — render ROADMAP.md from the Bowire GitHub Project.
//
// The project at https://github.com/orgs/Kuestenlogik/projects/2 is the
// single source of truth for the roadmap. This script queries its
// GraphQL API, groups issues by milestone, and writes a human-readable
// Markdown file so the roadmap is also readable offline (in `git`, in
// editors, in renders that don't speak the Projects API).
//
// Layout:
//   - Top: a flat per-milestone overview (just status + #number + title)
//     so you can scan the whole release in one screen.
//   - Bottom: per-milestone detail (status + tags + body excerpt) for
//     when you need context without leaving the file.
//
// Milestones are discovered dynamically from each issue's `milestone`
// field — no hardcoded list. Closed milestones drop out entirely
// (their changelog lives in GitHub Releases). Open milestones surface
// every assigned issue regardless of state, so closed issues against
// an unreleased milestone stay visible until the release ships — that
// way the operator can track progress offline without checking the
// Project board.
//
// Auth: any token with `read:project` + `repo` on Kuestenlogik/Bowire.
// In CI the workflow's GITHUB_TOKEN suffices; locally use a PAT or
// `gh auth token` piped via GH_TOKEN.
//
// Usage:
//   node scripts/generate-roadmap.mjs                # write ROADMAP.md
//   node scripts/generate-roadmap.mjs --check        # exit 1 if stale
//   node scripts/generate-roadmap.mjs --stdout       # write to stdout

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

const ORG = "Kuestenlogik";
const PROJECT_NUMBER = 2;
const TARGET_FILE = "ROADMAP.md";

const CHECK_MODE = process.argv.includes("--check");
const STDOUT_MODE = process.argv.includes("--stdout");

const QUERY = `
query($org: String!, $number: Int!, $cursor: String) {
  organization(login: $org) {
    projectV2(number: $number) {
      title
      url
      items(first: 100, after: $cursor) {
        pageInfo { hasNextPage endCursor }
        nodes {
          content {
            __typename
            ... on Issue {
              number
              title
              url
              state
              repository { nameWithOwner }
              labels(first: 20) { nodes { name } }
              milestone { title state dueOn }
              body
            }
            ... on DraftIssue { title body }
          }
          fieldValues(first: 30) {
            nodes {
              __typename
              ... on ProjectV2ItemFieldSingleSelectValue {
                name
                field { ... on ProjectV2SingleSelectField { name } }
              }
            }
          }
        }
      }
    }
  }
}
`;

async function gh(query, vars) {
    const res = await fetch("https://api.github.com/graphql", {
        method: "POST",
        headers: {
            "Authorization": `Bearer ${TOKEN}`,
            "Accept": "application/vnd.github+json",
            "Content-Type": "application/json",
            "User-Agent": "bowire-roadmap-generator",
        },
        body: JSON.stringify({ query, variables: vars }),
    });
    if (!res.ok) {
        console.error(`GitHub GraphQL HTTP ${res.status}: ${await res.text()}`);
        process.exit(1);
    }
    const json = await res.json();
    if (json.errors) {
        console.error("GitHub GraphQL errors:", JSON.stringify(json.errors, null, 2));
        process.exit(1);
    }
    return json;
}

async function fetchAllItems() {
    const items = [];
    let cursor = null;
    while (true) {
        const vars = { org: ORG, number: PROJECT_NUMBER, cursor };
        const result = await gh(QUERY, vars);
        const project = result.data.organization.projectV2;
        for (const node of project.items.nodes) items.push(node);
        const pi = project.items.pageInfo;
        if (!pi.hasNextPage) break;
        cursor = pi.endCursor;
    }
    return items;
}

// Field extraction — Project field values come back as a mixed array,
// so we look each one up by field-name.
function fieldValue(item, fieldName) {
    for (const fv of item.fieldValues.nodes) {
        if (fv.__typename !== "ProjectV2ItemFieldSingleSelectValue") continue;
        if (fv.field?.name === fieldName) return fv.name;
    }
    return null;
}

// Status icon — quick visual scan of progress without reading text.
// CLOSED issues against open milestones still surface so you can see
// what's already done toward the next release.
function statusIcon(item) {
    if (item.content.state === "CLOSED") return "✅";
    const ps = fieldValue(item, "Status");
    if (ps === "In progress") return "🟡";
    if (ps === "Next up") return "🟢";
    return "⬜";
}

function statusLabel(item) {
    if (item.content.state === "CLOSED") return "Done";
    return fieldValue(item, "Status") || "Open";
}

// Convention: `vX.Y[.Z][-rc.N] — <theme>` where the em-dash separator
// and theme tail are optional. Falls back to `{ version: title }` for
// plain version-only titles so legacy milestones still bucket cleanly.
function parseMilestoneTitle(title) {
    if (!title) return { version: null, theme: null };
    const m = title.match(/^(v[\d.]+(?:-[\w.]+)?)\s*(?:[—-]\s*(.+))?$/);
    if (!m) return { version: title, theme: null };
    return { version: m[1], theme: m[2] ? m[2].trim() : null };
}

// Semver-ish sort key for milestone versions. Drives the order in
// which milestones appear in both the overview and detail sections.
function semverKey(v) {
    if (!v) return [Number.MAX_SAFE_INTEGER];
    const m = v.match(/^v(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:-(\w+)\.(\d+))?/);
    if (!m) return [Number.MAX_SAFE_INTEGER];
    return [
        parseInt(m[1] || "0", 10),
        parseInt(m[2] || "0", 10),
        parseInt(m[3] || "0", 10),
        m[4] ? 0 : 1, // pre-release sorts before final
        parseInt(m[5] || "0", 10),
    ];
}

function compareKeys(a, b) {
    for (let i = 0; i < Math.max(a.length, b.length); i++) {
        const av = a[i] ?? 0;
        const bv = b[i] ?? 0;
        if (av !== bv) return av - bv;
    }
    return 0;
}

// Group items by open milestone. Closed milestones drop out (their
// items are part of a shipped release — changelog lives in GH
// Releases). Items without any milestone go to the "Backlog" bucket
// at the bottom.
function classify(items) {
    const byMs = new Map(); // milestone title → { title, dueOn, issues: [] }
    const noMs = [];

    for (const item of items) {
        if (!item.content || item.content.__typename !== "Issue") continue;
        const ms = item.content.milestone;

        if (!ms) {
            // Truly unscheduled — only surface open ones; closed
            // un-milestoned issues are noise (would otherwise pile up).
            if (item.content.state === "OPEN") noMs.push(item);
            continue;
        }

        if (ms.state === "CLOSED") continue; // shipped — see Releases

        if (!byMs.has(ms.title)) {
            byMs.set(ms.title, { title: ms.title, dueOn: ms.dueOn, issues: [] });
        }
        byMs.get(ms.title).issues.push(item);
    }

    // Sort issues inside each milestone: open first (in In-progress /
    // Next-up / Backlog order), closed last. Within a status, by
    // ascending issue number so the order is stable.
    const statusOrder = { "In progress": 0, "Next up": 1, "Backlog": 2 };
    function issueSortKey(item) {
        const closed = item.content.state === "CLOSED" ? 1 : 0;
        const ps = fieldValue(item, "Status") || "Backlog";
        return [closed, statusOrder[ps] ?? 99, item.content.number];
    }
    for (const ms of byMs.values()) {
        ms.issues.sort((a, b) => compareKeys(issueSortKey(a), issueSortKey(b)));
    }
    noMs.sort((a, b) => compareKeys(issueSortKey(a), issueSortKey(b)));

    return { byMilestone: byMs, noMilestone: noMs };
}

// Build the tag chip list — area / track / kind / priority. Shared
// between the overview-row formatter and the detail-block formatter
// so they stay consistent.
function buildTags(item) {
    const c = item.content;
    const area = fieldValue(item, "Area");
    const track = fieldValue(item, "Track");
    const priority = fieldValue(item, "Priority");
    const kind = fieldValue(item, "Kind");
    const tags = [];
    if (kind) tags.push(`\`kind:${kind}\``);
    if (area) tags.push(`\`area:${area}\``);
    if (track && track !== "none") tags.push(`\`track:${track}\``);
    if (priority) tags.push(`\`${priority}\``);
    // Repo-cross-link: render as `org/repo#NNN` when the issue lives
    // outside the main Bowire repo. Doesn't add a tag to the chip
    // strip; just changes how the issue itself is referenced upstream.
    return tags;
}

function issueRef(item) {
    const c = item.content;
    return c.repository.nameWithOwner === `${ORG}/Bowire`
        ? `#${c.number}`
        : `${c.repository.nameWithOwner}#${c.number}`;
}

// Short repo name for the Project column of the overview table.
// Strips the `Kuestenlogik/` prefix and the `Bowire.` namespace so a
// sibling-repo row reads as e.g. `Protocol.Surgewave` instead of
// `Kuestenlogik/Bowire.Protocol.Surgewave`. The main repo shows just
// `Bowire`.
function shortProject(item) {
    const repo = item.content.repository.nameWithOwner;
    const tail = repo.replace(`${ORG}/`, "");
    if (tail === "Bowire") return "Bowire";
    return tail.replace(/^Bowire\./, "");
}

// Escape pipes in a cell so a title containing `|` doesn't break the
// markdown table layout. Backslashes have to be escaped FIRST so an
// input like `\|` doesn't survive as `\\|` and re-introduce a literal
// pipe through the markdown reader's own unescape pass — that's the
// js/incomplete-sanitization footgun. Newlines also need taming since
// a raw `\n` in a cell breaks the row.
function tableCell(s) {
    return String(s ?? "")
        .replace(/\\/g, "\\\\")
        .replace(/\|/g, "\\|")
        .replace(/\r?\n/g, "<br>");
}

// Stable anchor id for in-file cross-links between the Overview
// table and the Details section. Issues are unique by repo + number;
// the anchor stamps both so a sibling-repo issue with the same number
// as a Bowire-main one doesn't collide.
function detailAnchorId(item) {
    const repo = item.content.repository.nameWithOwner.replace(/[^a-z0-9]+/gi, "-").toLowerCase();
    return `issue-${repo}-${item.content.number}`;
}

// Overview table row — one per issue. Layout:
//   | # | Project | Title | Status | Tags |
// # cell links OUT to the GitHub issue (matches the universal "click
// the number to open the ticket" expectation), Title cell links DOWN
// to the detail block inside the same file (jump within the doc to
// read the excerpt without leaving the page). Two click targets per
// row: one external, one internal.
function fmtOverviewRow(item) {
    const c = item.content;
    const status = `${statusIcon(item)} ${statusLabel(item)}`;
    const num = `[${c.number}](${c.url})`;
    const project = shortProject(item);
    const title = `[${tableCell(c.title)}](#${detailAnchorId(item)})`;
    const tags = buildTags(item).join(" ");
    return `| ${num} | ${project} | ${title} | ${status} | ${tags} |`;
}

// Header row for the overview table — emitted once per milestone
// section so each table is self-contained.
function overviewTableHeader() {
    return [
        "| # | Project | Title | Status | Tags |",
        "|---|---|---|---|---|",
    ];
}

// Detail block: status icon + status label + ref + title heading, tag
// strip, body excerpt. The excerpt is the first paragraph trimmed to
// ~300 chars so the file stays manageable for very long issue bodies.
function fmtDetailBlock(item) {
    const c = item.content;
    const status = statusIcon(item);
    const label = statusLabel(item);
    const tags = buildTags(item);
    const anchor = `<a id="${detailAnchorId(item)}"></a>`;
    const lines = [];
    lines.push(`#### ${anchor}${status} ${label} · [${issueRef(item)}](${c.url}) ${c.title}`);
    if (tags.length > 0) {
        lines.push("");
        lines.push(`> ${tags.join(" · ")}`);
    }
    const { excerpt, truncated } = firstContentParagraph(c.body || "");
    if (excerpt) {
        lines.push("");
        // Collapse internal newlines so the paragraph reads as one
        // markdown paragraph (issue bodies often soft-wrap). When
        // the excerpt was truncated, append a tail link to the full
        // issue so the reader can jump straight to the source.
        const tail = truncated ? ` [[more]](${c.url})` : "";
        lines.push(excerpt.replace(/\n+/g, " ") + tail);
    }
    return lines.join("\n");
}

// Extract the first "real" paragraph from an issue body. Skips
// leading markdown headings ("## Problem", "### Why", etc.) and
// horizontal rules so the excerpt is actual prose, not just the
// section label. Soft-trims at ~300 chars on a sentence boundary;
// also flags whether the issue body had MORE content beyond the
// returned excerpt so the caller can append a "more" link.
function firstContentParagraph(body) {
    if (!body) return { excerpt: "", truncated: false };
    const blocks = body.trim().split(/\n\s*\n/);
    let para = "";
    let blockIdx = -1;
    for (let i = 0; i < blocks.length; i++) {
        const block = blocks[i].trim();
        if (!block) continue;
        if (/^#+\s/.test(block) && !/\n/.test(block)) continue;
        if (/^[-=*]{3,}$/.test(block)) continue;
        if (/^#+\s/.test(block)) {
            const stripped = block.replace(/^#+\s.*?\n+/, "").trim();
            if (stripped) { para = stripped; blockIdx = i; break; }
            continue;
        }
        para = block;
        blockIdx = i;
        break;
    }
    if (!para) return { excerpt: "", truncated: false };
    let truncated = false;
    if (para.length > 320) {
        const cut = para.lastIndexOf(". ", 300);
        para = cut > 80 ? para.slice(0, cut + 1) + " …" : para.slice(0, 300).trim() + " …";
        truncated = true;
    }
    // If we picked block N out of M, there's more content past the
    // excerpt — flag it so the caller can offer a "more" link even
    // if the chosen paragraph itself fit under the soft-trim.
    if (!truncated && blockIdx >= 0 && blockIdx < blocks.length - 1) {
        // Treat trailing blank or heading-only blocks as no-content
        // so a single-paragraph body doesn't get a "more" link.
        for (let j = blockIdx + 1; j < blocks.length; j++) {
            const next = blocks[j].trim();
            if (!next) continue;
            if (/^#+\s/.test(next) && !/\n/.test(next)) continue;
            if (/^[-=*]{3,}$/.test(next)) continue;
            truncated = true;
            break;
        }
    }
    return { excerpt: para, truncated };
}

function milestoneHeading(ms) {
    const { version, theme } = parseMilestoneTitle(ms.title);
    const label = version || ms.title;
    const due = ms.dueOn ? ` *(due ${ms.dueOn.slice(0, 10)})*` : "";
    return theme ? `### ${label} — ${theme}${due}` : `### ${label}${due}`;
}

function render(groups) {
    const lines = [];
    lines.push("# Bowire Roadmap");
    lines.push("");
    lines.push(`> **Auto-generated from the [Bowire Project board](https://github.com/orgs/${ORG}/projects/${PROJECT_NUMBER}).** The Project is the canonical source for roadmap items, priorities, and version targets; this file is regenerated by \`scripts/generate-roadmap.mjs\` (CI: \`.github/workflows/roadmap-sync.yml\`) so the roadmap is also readable offline. Edits to this file are overwritten on the next sync — open / triage / move items on the Project instead.`);
    lines.push("");
    lines.push("For what's already shipped, see [GitHub Releases](https://github.com/Kuestenlogik/Bowire/releases) (the authoritative changelog) and the per-feature ADRs under [`docs/architecture/`](docs/architecture/).");
    lines.push("");
    lines.push(`Field conventions live in [\`docs/contributing/project-board.md\`](docs/contributing/project-board.md).`);
    lines.push("");
    lines.push("**Status legend:** ✅ Done · 🟡 In progress · 🟢 Next up · ⬜ Backlog");
    lines.push("");

    // Sort milestones by version
    const milestones = [...groups.byMilestone.values()].sort((a, b) =>
        compareKeys(semverKey(parseMilestoneTitle(a.title).version),
                    semverKey(parseMilestoneTitle(b.title).version)));

    // ---- Pass 1: Overview ----
    // Per milestone, just a flat status + #ref + title list so you can
    // see the whole release on one screen.
    lines.push("## Overview");
    lines.push("");
    if (milestones.length === 0 && groups.noMilestone.length === 0) {
        lines.push("_No issues on the Project board yet._");
        lines.push("");
    }
    for (const ms of milestones) {
        lines.push(milestoneHeading(ms));
        lines.push("");
        const totals = countByStatus(ms.issues);
        lines.push(progressLine(totals));
        lines.push("");
        lines.push(...overviewTableHeader());
        for (const item of ms.issues) lines.push(fmtOverviewRow(item));
        lines.push("");
    }
    if (groups.noMilestone.length > 0) {
        lines.push("### Backlog (not yet scheduled)");
        lines.push("");
        lines.push(...overviewTableHeader());
        for (const item of groups.noMilestone) lines.push(fmtOverviewRow(item));
        lines.push("");
    }

    // ---- Pass 2: Details ----
    // Per milestone with tags + body excerpt so the file is also a
    // standalone reference (no clicking through to GitHub needed).
    if (milestones.length > 0 || groups.noMilestone.length > 0) {
        lines.push("## Details");
        lines.push("");
    }
    for (const ms of milestones) {
        lines.push(milestoneHeading(ms));
        lines.push("");
        for (const item of ms.issues) {
            lines.push(fmtDetailBlock(item));
            lines.push("");
        }
    }
    if (groups.noMilestone.length > 0) {
        lines.push("### Backlog (not yet scheduled)");
        lines.push("");
        for (const item of groups.noMilestone) {
            lines.push(fmtDetailBlock(item));
            lines.push("");
        }
    }

    const generatedAt = new Date().toISOString().slice(0, 10);
    lines.push("---");
    lines.push("");
    lines.push(`*Generated ${generatedAt} from [Project #${PROJECT_NUMBER}](https://github.com/orgs/${ORG}/projects/${PROJECT_NUMBER}).*`);
    lines.push("");
    return lines.join("\n");
}

function countByStatus(items) {
    const c = { done: 0, inProgress: 0, nextUp: 0, backlog: 0 };
    for (const item of items) {
        if (item.content.state === "CLOSED") { c.done++; continue; }
        const ps = fieldValue(item, "Status");
        if (ps === "In progress") c.inProgress++;
        else if (ps === "Next up") c.nextUp++;
        else c.backlog++;
    }
    return c;
}

function progressLine(c) {
    const total = c.done + c.inProgress + c.nextUp + c.backlog;
    const parts = [`**${c.done}/${total} done**`];
    if (c.inProgress) parts.push(`${c.inProgress} in progress`);
    if (c.nextUp) parts.push(`${c.nextUp} next up`);
    if (c.backlog) parts.push(`${c.backlog} backlog`);
    return parts.join(" · ");
}

const items = await fetchAllItems();
const groups = classify(items);
const rendered = render(groups);

if (STDOUT_MODE) {
    process.stdout.write(rendered);
    process.exit(0);
}

if (CHECK_MODE) {
    let existing = "";
    try { existing = readFileSync(TARGET_FILE, "utf8"); } catch { /* missing */ }
    if (existing.trim() === rendered.trim()) {
        console.log(`${TARGET_FILE} is up to date with the Project board.`);
        process.exit(0);
    }
    console.error(`${TARGET_FILE} is stale. Run: node scripts/generate-roadmap.mjs`);
    process.exit(1);
}

writeFileSync(TARGET_FILE, rendered);

const openCount = items.filter((i) => i.content?.__typename === "Issue" && i.content.state === "OPEN").length;
const closedCount = items.filter((i) => i.content?.__typename === "Issue" && i.content.state === "CLOSED").length;
console.log(`Wrote ${TARGET_FILE} — ${openCount} open + ${closedCount} closed issue(s) across the Project.`);

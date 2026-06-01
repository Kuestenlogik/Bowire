#!/usr/bin/env node
// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// generate-roadmap.mjs — render ROADMAP.md from the Bowire GitHub Project.
//
// The project at https://github.com/orgs/Kuestenlogik/projects/2 is the
// single source of truth for the roadmap. This script queries its
// GraphQL API, groups issues by Status × Milestone, and writes a
// human-readable Markdown file so the roadmap is also readable offline
// (in `git`, in editors, in renders that don't speak the Projects API).
//
// Auth: any token with `read:project` + `repo` on Kuestenlogik/Bowire.
// In CI the workflow's GITHUB_TOKEN suffices; locally use a PAT or
// `gh auth token` piped via GH_TOKEN.
//
// Usage:
//   node scripts/generate-roadmap.mjs                # write ROADMAP.md
//   node scripts/generate-roadmap.mjs --check        # exit 1 if ROADMAP.md
//                                                    # would change (CI gate)
//   node scripts/generate-roadmap.mjs --stdout       # write to stdout

import { execSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import process from "node:process";

function resolveToken() {
    if (process.env.GH_TOKEN) return process.env.GH_TOKEN;
    if (process.env.GITHUB_TOKEN) return process.env.GITHUB_TOKEN;
    // Local dev — pull the token from gh's keyring so the script
    // works without setting an env var manually.
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

// GitHub Projects v2 pagination — we may need a few pages for >100 items.
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
              milestone { title }
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

// Field extraction helpers — Project field values come back as a mixed
// array, so we look each one up by field-name.
function fieldValue(item, fieldName) {
    for (const fv of item.fieldValues.nodes) {
        if (fv.__typename !== "ProjectV2ItemFieldSingleSelectValue") continue;
        if (fv.field?.name === fieldName) return fv.name;
    }
    return null;
}

function classify(items) {
    const groups = {
        "In progress": [],
        "Next up": [],
        Backlog: [],
        // Done items go into "Recently shipped" via GitHub Releases — we don't
        // mirror them here to keep ROADMAP.md focused on "what's next".
    };
    for (const item of items) {
        if (!item.content || item.content.__typename !== "Issue") continue;
        if (item.content.state === "CLOSED") continue;
        const status = fieldValue(item, "Status") ?? "Backlog";
        if (!(status in groups)) continue;
        groups[status].push(item);
    }
    return groups;
}

const MILESTONE_ORDER = ["v1.5", "v1.6", "v2.0", "Later", null];

function byMilestone(items) {
    const map = new Map(MILESTONE_ORDER.map((m) => [m, []]));
    for (const item of items) {
        const ms = item.content.milestone?.title ?? null;
        const key = MILESTONE_ORDER.includes(ms) ? ms : null;
        map.get(key).push(item);
    }
    return map;
}

function fmtIssue(item) {
    const c = item.content;
    const repo = c.repository.nameWithOwner === `${ORG}/Bowire`
        ? `#${c.number}`
        : `${c.repository.nameWithOwner}#${c.number}`;
    const area = fieldValue(item, "Area");
    const track = fieldValue(item, "Track");
    const priority = fieldValue(item, "Priority");
    const kind = fieldValue(item, "Kind");
    const tags = [];
    if (kind) tags.push(`\`kind:${kind}\``);
    if (area) tags.push(`\`area:${area}\``);
    if (track && track !== "none") tags.push(`\`track:${track}\``);
    if (priority) tags.push(`\`${priority}\``);
    return `- [${repo}](${c.url}) **${c.title}** ${tags.join(" ")}`;
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

    for (const [status, items] of Object.entries(groups)) {
        if (items.length === 0) continue;
        lines.push(`## ${status}`);
        lines.push("");
        const byMs = byMilestone(items);
        for (const ms of MILESTONE_ORDER) {
            const slice = byMs.get(ms) ?? [];
            if (slice.length === 0) continue;
            lines.push(`### ${ms ?? "No milestone"}`);
            lines.push("");
            for (const item of slice) lines.push(fmtIssue(item));
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
console.log(`Wrote ${TARGET_FILE} — ${items.filter((i) => i.content?.__typename === "Issue" && i.content.state === "OPEN").length} open issue(s) across the Project.`);

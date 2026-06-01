#!/usr/bin/env node
// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// sync-project.mjs — idempotent sync of `roadmap`-labelled issues into
// the Bowire Project board. Reads every open issue with the
// `roadmap` label, looks each one up in the Project, attaches the
// missing ones, and applies the Status / Area / Track / Priority /
// Kind / Milestone field values from a per-title mapping table.
//
// Safe to re-run after a rate-limit reset, after manual edits in the
// Project UI, and after new roadmap items get filed: anything already
// in the right state is a no-op. Issues whose titles are not in the
// mapping table get attached to the project but receive no field
// values — manual triage from the UI takes over.
//
// Usage:
//   node scripts/sync-project.mjs               # do the sync
//   node scripts/sync-project.mjs --dry-run     # print plan, do nothing
//
// Auth: GH_TOKEN with `project` + `repo` scope, or `gh auth token`.

import { execSync } from "node:child_process";
import process from "node:process";

const ORG = "Kuestenlogik";
const REPO = "Bowire";
const PROJECT_NUMBER = 2;
const DRY_RUN = process.argv.includes("--dry-run");

const TOKEN = process.env.GH_TOKEN || process.env.GITHUB_TOKEN ||
    (() => { try { return execSync("gh auth token", { stdio: ["ignore", "pipe", "ignore"] }).toString().trim(); } catch { return null; } })();
if (!TOKEN) { console.error("No token. Set GH_TOKEN or run `gh auth login`."); process.exit(1); }

async function gh(query, vars = {}) {
    const res = await fetch("https://api.github.com/graphql", {
        method: "POST",
        headers: {
            "Authorization": `Bearer ${TOKEN}`,
            "Accept": "application/vnd.github+json",
            "Content-Type": "application/json",
            "User-Agent": "bowire-project-sync",
        },
        body: JSON.stringify({ query, variables: vars }),
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}: ${await res.text()}`);
    const json = await res.json();
    if (json.errors) throw new Error(`GraphQL: ${JSON.stringify(json.errors)}`);
    return json.data;
}

// Per-issue-title field mapping + creation body. Issues whose title
// isn't here get attached to the project but no field values (manual
// UI triage takes over). Entries whose title doesn't exist in the repo
// yet are created via REST. milestone is set on creation; labels are
// derived from area/track/kind/priority.
const ROADMAP_URL_BASE = `https://github.com/${ORG}/${REPO}/blob/main/ROADMAP.md`;
function trackedBody(anchor, desc) {
    return `${desc}\n\nTracked in [\`ROADMAP.md\`](${ROADMAP_URL_BASE}#${anchor}) — the narrative there carries the full design rationale + the per-bullet checkboxes. This issue exists for board-level Status / Area / Track / Milestone tracking.`;
}
const MAPPING = {
    "AI side-panel integration": { status: "In progress", area: "workbench", priority: "P1", kind: "feature" },
    "Security testing tool — remaining tiers": { status: "In progress", area: "security", track: "security-tiers", priority: "P2", kind: "feature" },
    "Plugin lifecycle in the workbench UI + pre-release support": { status: "Next up", area: "plugin-sdk", priority: "P1", kind: "feature" },
    "Multi-tenant data model + SCIM (Phase B)": { status: "Next up", area: "workbench", track: "auth", priority: "P2", kind: "feature" },
    "Self-telemetry + Grafana dashboards": { status: "Next up", area: "workbench", track: "observability", priority: "P2", kind: "feature" },
    "Collections (Postman-style test suites)": { status: "Next up", area: "workbench", priority: "P2", kind: "feature" },
    "Auth-provider SPI — plugin-load privilege": { status: "Next up", area: "plugin-sdk", track: "auth", priority: "P2", kind: "debt" },
    "OIDC plugin — required-claim filter + token forwarding": { status: "Next up", area: "workbench", track: "auth", priority: "P2", kind: "feature" },
    "Protocol plugins — Connect Phase 1+3 + OTLP + Surgewave": { status: "Backlog", area: "plugin-sdk", track: "protocols", priority: "P2", kind: "feature" },
    "AsyncAPI discovery source — remaining bindings + V2 overloads + YAML pre-normaliser": { status: "Backlog", area: "plugin-sdk", track: "protocols", priority: "P2", kind: "feature" },
    "Nuclei template compat — OAST + non-HTTP transports (Phase 2f + 2g)": { status: "Backlog", area: "security", track: "security-tiers", priority: "P3", kind: "feature" },
    "Replay-Mock — HTTPS MITM / record mode": { status: "Backlog", area: "mock", priority: "P3", kind: "feature" },
    "Bowire.Mcp — remaining tools + adapter modes": { status: "Backlog", area: "mcp", priority: "P2", kind: "feature" },
    "CLI — Phase 3 polish (completion + validators + error rendering)": { status: "Backlog", area: "cli", priority: "P2", kind: "debt" },
    "HAR Import polish": { status: "Backlog", area: "workbench", priority: "P2", kind: "feature" },
    "Freeform Request Builder": { status: "Backlog", area: "workbench", priority: "P2", kind: "feature" },
    "First RC of the new versioning discipline": { status: "Backlog", area: "multi", priority: "P3", kind: "debt" },
    "Plugin project template — `dotnet new bowire-plugin`": { status: "Backlog", area: "plugin-sdk", priority: "P3", kind: "feature" },
    "MCP SSE-transport support": { status: "Backlog", area: "mcp", priority: "P3", kind: "feature" },
    "Sidecar packaging — Docker / Compose / Kubernetes": { status: "Backlog", area: "docs", priority: "P3", kind: "docs" },
    "SimpleGraphQLSubscriptions sample": { status: "Backlog", area: "plugin-sdk", priority: "P3", kind: "feature" },
    "MCP server-side notifications via IInlineSseSubscriber": { status: "Backlog", area: "mcp", priority: "P3", kind: "feature" },
    "Sidebar display: method name vs path toggle": { status: "Backlog", area: "workbench", priority: "P3", kind: "feature" },
    "Schema watch mode": { status: "Backlog", area: "workbench", priority: "P3", kind: "feature" },
    "Programmatic environment provisioning in embedded mode": { status: "Backlog", area: "workbench", priority: "P2", kind: "feature" },
    "Marketing site — gallery / lightbox layer on solutions/*": { status: "Backlog", area: "site", track: "marketing-ia", priority: "P3", kind: "feature", milestone: "Later",
        body: trackedBody("planned-no-commitments-yet", "The new responsive image pipeline (optimize-images.mjs + picture.html) already emits -400w / -1200w / original variants. Layer a small JS lightbox that swaps the thumbnail-grid render (400w) for the full-resolution AVIF on click — initial page load gets the smallest variant, original only hits the wire on click.") },
    "Marketing site — migrate <img> tags to picture.html partial": { status: "Backlog", area: "site", track: "marketing-ia", priority: "P2", kind: "debt", milestone: "v1.5",
        body: trackedBody("planned-no-commitments-yet", "Image-perf pipeline shipped (sharp → AVIF + WebP, three variant widths; picture.html partial; Lighthouse CI gates). Remaining: migrate every <img src=\"…\"> in site/_includes/*.html + site/**/*.html to the picture.html partial, then drop the legacy .png originals from the deploy bundle once the AVIF fallback chain has been verified across browsers.") },
    'Marketing — "why not just Console.WriteLine or Serilog/Loki/Grafana?"': { status: "Backlog", area: "site", track: "marketing-ia", priority: "P2", kind: "docs", milestone: "v1.5",
        body: trackedBody("planned-no-commitments-yet", "Write the missing leg of the pitch: when does Bowire beat (or augment) Console.WriteLine-debugging and a Serilog/Loki/Grafana observability stack? Likely framing: dev-time *interactive call & inspect* loop vs ops-time *log-aggregation*; Bowire makes the contract executable, Grafana makes the running system observable — different jobs, same engineering team.") },
    "Marketing site — second row of specialist comparisons": { status: "Backlog", area: "site", track: "marketing-ia", priority: "P3", kind: "feature", milestone: "Later",
        body: trackedBody("planned-no-commitments-yet", "The comparison table's \"top-5-competitors check\" framing already lets us mention more tools in the best-for strip without committing them to a full table row. A second row under the existing strip would cover specialist tools where Bowire overlaps but isn't the same shape: k6 / JMeter, WireMock / Mockoon, Burp Suite / ZAP, HTTPie / curl, MQTT Explorer / kcat, SwaggerHub / Stoplight.") },
};

// 1. Fetch project metadata (id + fields + options)
const projectQ = `
query($org: String!, $number: Int!) {
  organization(login: $org) {
    projectV2(number: $number) {
      id
      title
      fields(first: 30) {
        nodes {
          ... on ProjectV2SingleSelectField {
            id name
            options { id name }
          }
        }
      }
      items(first: 100) {
        pageInfo { hasNextPage endCursor }
        nodes {
          id
          content { ... on Issue { id number title } }
        }
      }
    }
  }
}`;

const projectData = (await gh(projectQ, { org: ORG, number: PROJECT_NUMBER })).organization.projectV2;
const PROJECT_ID = projectData.id;

// Build a field-name → { id, options: name→id } lookup.
const FIELDS = {};
for (const f of projectData.fields.nodes) {
    if (!f || !f.options) continue;
    FIELDS[f.name] = { id: f.id, options: Object.fromEntries(f.options.map((o) => [o.name, o.id])) };
}

// items already in the project, keyed by issue node-id
const existingItemByIssueId = new Map();
for (const item of projectData.items.nodes) {
    if (item.content?.id) existingItemByIssueId.set(item.content.id, item.id);
}
// note: project may have >100 items — re-fetch if pagination becomes needed later

// 2. Fetch every open `roadmap` issue in the repo
const issuesQ = `
query($owner: String!, $repo: String!, $cursor: String) {
  repository(owner: $owner, name: $repo) {
    issues(first: 100, after: $cursor, states: OPEN, labels: ["roadmap"]) {
      pageInfo { hasNextPage endCursor }
      nodes { id number title }
    }
  }
}`;
const issues = [];
let cursor = null;
while (true) {
    const result = (await gh(issuesQ, { owner: ORG, repo: REPO, cursor })).repository.issues;
    issues.push(...result.nodes);
    if (!result.pageInfo.hasNextPage) break;
    cursor = result.pageInfo.endCursor;
}
console.log(`Found ${issues.length} \`roadmap\`-labelled issues in ${ORG}/${REPO}.`);

// 2b. Create any MAPPING entries that have a `body` set but no matching
// open issue yet. REST POST /repos/owner/repo/issues; labels derived
// from area/track/kind plus the "roadmap" marker; milestone by title.
const issueTitles = new Set(issues.map((i) => i.title));
const milestonesQ = `query($owner: String!, $repo: String!) {
  repository(owner: $owner, name: $repo) { milestones(first: 25, states: OPEN) { nodes { number title } } }
}`;
const milestoneByTitle = Object.fromEntries(
    (await gh(milestonesQ, { owner: ORG, repo: REPO })).repository.milestones.nodes.map((m) => [m.title, m.number])
);

for (const [title, m] of Object.entries(MAPPING)) {
    if (!m.body || issueTitles.has(title)) continue;
    const labels = ["roadmap", `area:${m.area}`, `kind:${m.kind}`];
    if (m.track) labels.push(`track:${m.track}`);
    const payload = { title, body: m.body, labels };
    if (m.milestone && milestoneByTitle[m.milestone]) payload.milestone = milestoneByTitle[m.milestone];

    if (DRY_RUN) { console.log(`[DRY] Would create issue "${title}" with labels ${labels.join(",")}`); continue; }
    const res = await fetch(`https://api.github.com/repos/${ORG}/${REPO}/issues`, {
        method: "POST",
        headers: { Authorization: `Bearer ${TOKEN}`, Accept: "application/vnd.github+json", "Content-Type": "application/json", "User-Agent": "bowire-project-sync" },
        body: JSON.stringify(payload),
    });
    if (!res.ok) { console.error(`!! Create failed for "${title}": HTTP ${res.status} ${await res.text()}`); continue; }
    const issue = await res.json();
    // GraphQL needs the node_id, not the REST id.
    issues.push({ id: issue.node_id, number: issue.number, title: issue.title });
    console.log(`Created #${issue.number} ${issue.title}`);
}

// 3. For each issue: attach to project if missing, then push field values.
const addMut = `mutation($projectId: ID!, $contentId: ID!) {
  addProjectV2ItemById(input: { projectId: $projectId, contentId: $contentId }) {
    item { id }
  }
}`;
const setMut = `mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!, $optionId: String!) {
  updateProjectV2ItemFieldValue(input: {
    projectId: $projectId, itemId: $itemId, fieldId: $fieldId,
    value: { singleSelectOptionId: $optionId }
  }) { projectV2Item { id } }
}`;

let attached = 0, fieldsSet = 0, unmapped = 0;
for (const issue of issues) {
    let itemId = existingItemByIssueId.get(issue.id);
    if (!itemId) {
        if (DRY_RUN) { console.log(`[DRY] Would attach #${issue.number} ${issue.title}`); attached++; continue; }
        const r = await gh(addMut, { projectId: PROJECT_ID, contentId: issue.id });
        itemId = r.addProjectV2ItemById.item.id;
        attached++;
        console.log(`Attached #${issue.number} ${issue.title}`);
    }
    const map = MAPPING[issue.title];
    if (!map) { unmapped++; continue; }
    for (const [fieldName, optionName] of [["Status", map.status], ["Area", map.area], ["Track", map.track], ["Priority", map.priority], ["Kind", map.kind]]) {
        if (!optionName) continue;
        const f = FIELDS[fieldName];
        const oid = f?.options?.[optionName];
        if (!oid) { console.warn(`!! Missing option ${fieldName}=${optionName}`); continue; }
        if (DRY_RUN) { fieldsSet++; continue; }
        await gh(setMut, { projectId: PROJECT_ID, itemId, fieldId: f.id, optionId: oid });
        fieldsSet++;
    }
}

console.log(`\nDone. ${attached} attached, ${fieldsSet} field-values set, ${unmapped} issue(s) without a mapping entry.`);
console.log(`Project: https://github.com/orgs/${ORG}/projects/${PROJECT_NUMBER}`);

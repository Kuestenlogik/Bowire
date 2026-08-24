#!/usr/bin/env node
// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// Build the change list that goes under a release's curated body.
//
// GitHub's own `generate_release_notes` lists merged PRs. Bowire is
// committed to straight on `main`, so that list contained *only* Dependabot
// bumps — all 41 entries in v2.4.0, and not one of the ~200 commits of
// actual work. A reader saw an introduction about mock-from-schema and then
// a list of version bumps.
//
// This reads commits instead, and uses the Conventional Commit prefix the
// way it is meant to be used: as a grouping key that is consumed and then
// removed from the line. The prefix is a machine token — emitting it raw
// pays the noise and skips the grouping it exists to enable. `chore` means
// "no user-visible effect" by definition, so it does not belong in the main
// body at all; those and the dependency bumps collapse into one <details>.
// The scope survives as a short bold lead-in, because the section heading
// says what kind of change it is and only the scope says where.
//
// Usage:
//   node scripts/ci/generate-changelog.mjs --from v2.3.0 --to v2.4.0
//   node scripts/ci/generate-changelog.mjs --from v2.4.0            # to HEAD
//
// Writes markdown to stdout.

import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

// type(scope)!: subject  — scope and the breaking marker both optional.
const CONVENTIONAL = /^(?<type>[a-z]+)(?:\((?<scope>[^)]*)\))?(?<bang>!)?:\s*(?<subject>.+)$/;

// Section order is the reading order: what was added, what changed, what
// was fixed. Keep a Changelog's shape rather than the raw commit types —
// those are an implementation detail of how we write commits.
export const SECTIONS = [
    { key: 'added', title: 'Added', types: ['feat'] },
    { key: 'changed', title: 'Changed', types: ['refactor', 'perf', 'style'] },
    { key: 'fixed', title: 'Fixed', types: ['fix'] },
    { key: 'docs', title: 'Documentation', types: ['docs'] },
];

// Below the fold: true chores plus the test/ci/build plumbing.
const MAINTENANCE_TYPES = ['chore', 'test', 'ci', 'build'];

// …and the same judgement applied to the scope, whatever the type claims. A
// `fix(test): stop a port race` is a real fix, but not of anything a reader
// of the release has ever seen. The scope is the honest signal: it names the
// part of the repo that changed, and these parts do not ship.
const MAINTENANCE_SCOPES = ['test', 'tests', 'ci', 'build', 'workflow', 'workflows', 'actions', 'deps'];

// A dependency bump is the one case where the *count* is the useful
// information and the individual lines are not.
const DEP_SCOPES = /^(deps|deps-dev|npm|actions|nuget|ruby|docker)$/i;

/**
 * Sort commits into the buckets the release body renders.
 * @param {Array<{sha:string, author:string, subject:string, body?:string}>} commits
 */
export function classify(commits) {
    const buckets = new Map(SECTIONS.map(s => [s.key, []]));
    const breaking = [];
    const maintenance = [];
    const deps = [];
    const other = [];

    for (const c of commits) {
        const m = CONVENTIONAL.exec(c.subject);
        const scope = (m?.groups.scope ?? '').toLowerCase();

        const isBot = /dependabot/i.test(c.author ?? '');
        if (isBot || (m?.groups.type === 'chore' && DEP_SCOPES.test(scope))) {
            deps.push({ ...c, text: c.subject });
            continue;
        }

        if (!m) { other.push({ ...c, text: c.subject }); continue; }

        const { type, bang, subject } = m.groups;
        const entry = { ...c, text: scope ? `**${scope}** — ${subject}` : subject };

        // Breaking is news regardless of which area it came from, so it is
        // decided before the scope demotion below.
        if (bang || /^BREAKING[ -]CHANGE:/m.test(c.body ?? '')) { breaking.push(entry); continue; }

        if (MAINTENANCE_SCOPES.includes(scope)) { maintenance.push(entry); continue; }

        const section = SECTIONS.find(s => s.types.includes(type));
        if (section) buckets.get(section.key).push(entry);
        else if (MAINTENANCE_TYPES.includes(type)) maintenance.push(entry);
        else other.push(entry);
    }

    return { buckets, breaking, maintenance, deps, other };
}

const BT = String.fromCharCode(96);
const short = sha => (sha ?? '').slice(0, 7);
const line = e => `- ${e.text}${e.sha ? ` (${BT}${short(e.sha)}${BT})` : ''}`;

/** Render the classified buckets as the markdown appended to the release body. */
export function render(commits, { from, to, repoUrl }) {
    const { buckets, breaking, maintenance, deps, other } = classify(commits);
    const out = [];

    if (breaking.length) out.push('### Breaking changes', '', ...breaking.map(line), '');

    for (const s of SECTIONS) {
        const items = buckets.get(s.key);
        if (items.length) out.push(`### ${s.title}`, '', ...items.map(line), '');
    }

    if (other.length) out.push('### Other', '', ...other.map(line), '');

    // Collapsed rather than dropped: still the record of what happened, just
    // not what someone opening the release wants first.
    if (maintenance.length || deps.length) {
        const summary = [
            maintenance.length ? `${maintenance.length} maintenance commit${maintenance.length === 1 ? '' : 's'}` : null,
            deps.length ? `${deps.length} dependency update${deps.length === 1 ? '' : 's'}` : null,
        ].filter(Boolean).join(' · ');

        out.push('<details>', `<summary>Maintenance — ${summary}</summary>`, '');
        if (maintenance.length) out.push(...maintenance.map(line), '');
        if (deps.length) out.push('**Dependency updates**', '', ...deps.map(line), '');
        out.push('</details>', '');
    }

    // Contributors from commits rather than PR authors, same reason as
    // above. Bots are filtered: crediting dependabot next to a person reads
    // as a joke and buries the names that mean something.
    const authors = [...new Set(commits.map(c => c.author))]
        .filter(a => a && !/\[bot\]$/i.test(a.trim()) && !/^(dependabot|github-actions)$/i.test(a.trim()))
        .sort((a, b) => a.localeCompare(b));
    if (authors.length) out.push(`**Contributors:** ${authors.join(', ')}`, '');

    out.push(`**Full diff:** ${repoUrl}/compare/${from}...${to}`, '');
    return out.join('\n');
}

/** Read `git log <from>..<to>` into the shape classify() expects. */
export function readCommits(from, to) {
    // NUL-delimited so a subject containing anything at all stays parseable.
    // --no-merges: a merge commit's subject describes the merge, not a change.
    const raw = execFileSync(
        'git', ['log', '--no-merges', '--format=%H%x00%an%x00%s%x00%b%x01', `${from}..${to}`],
        { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });

    return raw.split('\x01')
        .map(rec => rec.replace(/^\n/, ''))
        .filter(rec => rec.trim())
        .map(rec => {
            const [sha, author, subject, body = ''] = rec.split('\x00');
            return { sha, author, subject, body };
        });
}

// Only run when invoked directly — the test imports the functions above.
if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    const argv = process.argv.slice(2);
    const arg = (name, fallback = null) => {
        const i = argv.indexOf(`--${name}`);
        return i >= 0 && argv[i + 1] ? argv[i + 1] : fallback;
    };

    const from = arg('from');
    const to = arg('to', 'HEAD');
    const repoUrl = arg('repo-url', 'https://github.com/Kuestenlogik/Bowire').replace(/\/+$/, '');

    if (!from) {
        console.error('usage: generate-changelog.mjs --from <tag> [--to <tag|HEAD>] [--repo-url <url>]');
        process.exit(2);
    }

    process.stdout.write(render(readCommits(from, to), { from, to, repoUrl }));
}

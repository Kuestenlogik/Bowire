#!/usr/bin/env node
// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #778 - the open issues and milestones are English too (#777), but they are not files: the
// English-only guard cannot fail on them, and must not fail on ROADMAP.md, which the roadmap sync
// generates from their titles. This report reads them with the guard's rules instead and says
// which read as German - it never fails.
//
// What it reads: the title and body of every open issue, the title and description of every open
// milestone. Fenced code, inline code and HTML comments are left out of a body, because a log, a
// payload or a quoted input there is data, not prose.
//
// What it does with a finding:
//   - prints it; in GitHub Actions also as a warning annotation and in the step summary;
//   - with --comment, asks on the issue once, in English, for an English title and body. The
//     comment carries a marker, and an issue that has one is not asked again. A milestone has no
//     comments; it is only reported.
//
//   node scripts/ci/report-issue-language.mjs                 # report only
//   node scripts/ci/report-issue-language.mjs --comment       # and ask on newly flagged issues
//   node scripts/ci/report-issue-language.mjs --repo owner/name
//
// Needs `gh` with a token that may read issues (and write them, for --comment). Exits 0 always.

import { execFileSync } from 'node:child_process';
import { appendFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { findGerman } from './check-english.mjs';

/** The marker that says an issue has been asked already. */
export const MARKER = '<!-- issue-language-report -->';

/** The prose of an issue body: fenced code, inline code and HTML comments removed. */
export function prose(text) {
    let fence = null;
    const lines = (text ?? '').replace(/<!--[\s\S]*?-->/g, m => m.replace(/[^\n]/g, '')).split(/\r?\n/).map(line => {
        const open = line.match(/^\s*(`{3,}|~{3,})/);
        if (fence) {
            if (open && open[1][0] === fence[0] && open[1].length >= fence.length) fence = null;
            return '';
        }
        if (open) { fence = open[1]; return ''; }
        return line.replace(/`[^`]*`/g, '');
    });
    // Blank lines keep the line numbers of what is left.
    return lines.join('\n');
}

/** The comment bodies from `gh api ... --jq '.[].body | @json'`: one JSON string per line. */
export function commentBodies(output) {
    return output.split(/\r?\n/).filter(l => l.trim()).map(l => JSON.parse(l));
}

function hitsOf(fields) {
    const hits = [];
    for (const [field, text] of fields) {
        for (const h of findGerman(prose(text))) hits.push({ field, line: h.line, text: h.text, reason: h.reason });
    }
    return hits;
}

/**
 * Everything that reads as German.
 * issues: [{ number, title, body, url }], milestones: [{ number, title, description, html_url }].
 * Returns [{ kind: 'issue' | 'milestone', number, title, url, hits: [{ field, line, text, reason }] }].
 */
export function flag({ issues = [], milestones = [] } = {}) {
    const flagged = [];
    for (const i of issues) {
        const hits = hitsOf([['title', i.title], ['body', i.body]]);
        if (hits.length) flagged.push({ kind: 'issue', number: i.number, title: i.title, url: i.url, hits });
    }
    for (const m of milestones) {
        const hits = hitsOf([['title', m.title], ['description', m.description]]);
        if (hits.length) flagged.push({ kind: 'milestone', number: m.number, title: m.title, url: m.html_url, hits });
    }
    return flagged;
}

/** Whether one of these comment bodies is the report's own question. */
export function hasAsked(commentBodies) {
    return commentBodies.some(b => (b ?? '').includes(MARKER));
}

/** The flagged issues to ask: issues only, and none that has been asked (askedNumbers: Set). */
export function toAsk(flagged, askedNumbers) {
    return flagged.filter(f => f.kind === 'issue' && !askedNumbers.has(f.number));
}

function fieldList(item) {
    const fields = [...new Set(item.hits.map(h => h.field))];
    return fields.length > 1 ? `${fields.slice(0, -1).join(', ')} and ${fields.at(-1)}` : fields[0];
}

/** The comment that asks for English. It quotes nothing, so it reads as English itself. */
export function commentBody(item) {
    return [
        MARKER,
        `The ${fieldList(item)} of this issue read as German. Everything in this repository is English, `
        + 'the issues included: the roadmap is generated from them, and anyone should be able to follow them '
        + '(see the Language section of CONTRIBUTING.md).',
        '',
        'Could you put the title and the description into English? Domain terms, names and quoted data can stay as they are.',
        '',
        'This is an automated note from the issue language report; it is posted once per issue.',
    ].join('\n');
}

const escapeData = s => String(s).replace(/%/g, '%25').replace(/\r/g, '%0D').replace(/\n/g, '%0A');
const escapeProperty = s => escapeData(s).replace(/:/g, '%3A').replace(/,/g, '%2C');

/** A GitHub Actions warning annotation for one flagged issue or milestone. */
export function annotation(item) {
    const what = item.kind === 'issue' ? `Issue #${item.number}` : `Milestone "${item.title}"`;
    const first = item.hits[0];
    const message = `${what} reads as German in its ${fieldList(item)} (${first.reason}): ${item.url}`;
    return `::warning title=${escapeProperty(`Issue language: ${what}`)}::${escapeData(message)}`;
}

function describe(item) {
    const what = item.kind === 'issue' ? `#${item.number}` : `milestone "${item.title}"`;
    const lines = [`${what} ${item.url}`];
    for (const h of item.hits) lines.push(`    ${h.field}:${h.line}: ${h.reason}\n        ${h.text.slice(0, 160)}`);
    return lines.join('\n');
}

// A Markdown table cell: escape backslashes first, then pipes, so a text
// ending in a backslash cannot turn the escaped pipe back into a column break.
export function cell(text) {
    return text.replace(/\\/g, '\\\\').replace(/\|/g, '\\|');
}

function summary(flagged, asked) {
    if (!flagged.length) return '### Issue language report\n\nNo open issue or milestone reads as German.\n';
    const rows = flagged.map(f => `| ${f.kind === 'issue' ? `[#${f.number}](${f.url})` : `[${cell(f.title)}](${f.url})`} `
        + `| ${fieldList(f)} | ${cell(f.hits[0].reason)} | ${asked.has(f.number) && f.kind === 'issue' ? 'yes' : ''} |`);
    return ['### Issue language report', '', `${flagged.length} open issue(s) or milestone(s) read as German.`, '',
        '| Where | Field | Why | Asked |', '|---|---|---|---|', ...rows, ''].join('\n');
}

function gh(args, input) {
    return execFileSync('gh', args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, input });
}

function run(argv) {
    const repoArg = argv.indexOf('--repo');
    const repo = repoArg >= 0 ? argv[repoArg + 1] : (process.env.GITHUB_REPOSITORY || 'Kuestenlogik/Bowire');
    const comment = argv.includes('--comment');
    const inActions = process.env.GITHUB_ACTIONS === 'true';

    const issues = JSON.parse(gh(['issue', 'list', '-R', repo, '--state', 'open', '--limit', '1000', '--json', 'number,title,body,url']));
    const milestones = JSON.parse(gh(['api', `repos/${repo}/milestones?state=open&per_page=100`]));
    const flagged = flag({ issues, milestones });

    // Only a flagged issue's comments are read: whether it has been asked already.
    const asked = new Set();
    for (const f of flagged.filter(x => x.kind === 'issue')) {
        const out = gh(['api', `repos/${repo}/issues/${f.number}/comments?per_page=100`, '--paginate', '--jq', '.[].body | @json']);
        if (hasAsked(commentBodies(out))) asked.add(f.number);
    }

    for (const f of flagged) {
        console.log(describe(f) + (f.kind === 'issue' && asked.has(f.number) ? '\n    (asked already)' : ''));
        if (inActions) console.log(annotation(f));
    }
    if (comment) {
        for (const f of toAsk(flagged, asked)) {
            try {
                gh(['api', '--method', 'POST', `repos/${repo}/issues/${f.number}/comments`, '-F', 'body=@-'], commentBody(f));
                asked.add(f.number);
                console.log(`Asked on #${f.number} for an English title and body.`);
            } catch (e) {
                console.log(`${inActions ? '::warning::' : ''}Could not comment on #${f.number}: ${String(e.message).split('\n')[0]}`);
            }
        }
    }
    if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, summary(flagged, asked));
    console.log(flagged.length
        ? `\n${flagged.length} open issue(s) or milestone(s) of ${repo} read as German.`
        : `Issue language: ${issues.length} open issue(s) and ${milestones.length} milestone(s) of ${repo} read as English.`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    try {
        run(process.argv.slice(2));
    } catch (e) {
        // A report, not a gate: a missing token or a network error is said and the job goes on.
        const prefix = process.env.GITHUB_ACTIONS === 'true' ? '::warning::' : '';
        console.log(`${prefix}Issue language report could not run: ${String(e.message).split('\n')[0]}`);
    }
    process.exit(0);
}

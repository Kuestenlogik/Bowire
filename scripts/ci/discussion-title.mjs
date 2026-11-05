#!/usr/bin/env node
// The title of the Announcements discussion a release opens.
//
// softprops/action-gh-release names the linked discussion after the release, and the release is
// named after its version alone (release-body.mjs, releaseName). Since 2.8.0 every announcement
// therefore read "2.8.0", "2.9.0", … with nothing about the delivery. The Releases list has no
// room for more than a version; the Discussions list has room for a line, and an announcement is
// read from that list. So the discussion is renamed after the release: the version, then the
// delivery's title — the one `#` the published body opens with.
//
// Usage: node scripts/ci/discussion-title.mjs <tag> <body-file> [--repo owner/name]
// Renames the release's discussion. Never fails the release: anything that goes wrong is a
// warning, and the discussion keeps the bare version it was created with.
import { readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { releaseName } from './release-body.mjs';

/** The delivery's title: the first `#` heading of the published body, or null. */
export function deliveryTitle(body) {
  const line = String(body ?? '').split(/\r?\n/).find(l => /^#\s+\S/.test(l));
  return line ? line.replace(/^#\s+/, '').trim() : null;
}

/** `2.10.0 — <delivery title>`, or the version alone when the body carries no title. */
export function discussionTitle(tag, body) {
  const version = releaseName(tag);
  const title = deliveryTitle(body);
  return title ? `${version} — ${title}` : version;
}

/** The discussion number from a release's `discussion_url`, or null. */
export function discussionNumber(url) {
  const m = /\/discussions\/(\d+)(?:[/?#]|$)/.exec(String(url ?? ''));
  return m ? Number(m[1]) : null;
}

const gh = (...args) => execFileSync('gh', args, { encoding: 'utf8' });

function rename(repo, tag, body) {
  const release = JSON.parse(gh('api', `repos/${repo}/releases/tags/${tag}`));
  const number = discussionNumber(release.discussion_url);
  if (!number) return `release ${tag} has no discussion; nothing to rename`;
  const [owner, name] = repo.split('/');
  const id = JSON.parse(gh('api', 'graphql',
    '-f', 'query=query($o:String!,$n:String!,$d:Int!){repository(owner:$o,name:$n){discussion(number:$d){id}}}',
    '-f', `o=${owner}`, '-f', `n=${name}`, '-F', `d=${number}`)).data.repository.discussion.id;
  const title = discussionTitle(tag, body);
  gh('api', 'graphql',
    '-f', 'query=mutation($id:ID!,$t:String!){updateDiscussion(input:{discussionId:$id,title:$t}){discussion{number}}}',
    '-f', `id=${id}`, '-f', `t=${title}`);
  return `discussion #${number}: ${title}`;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const args = process.argv.slice(2);
  const repoAt = args.indexOf('--repo');
  const repo = repoAt >= 0 ? args.splice(repoAt, 2)[1] : process.env.GITHUB_REPOSITORY;
  const [tag, file] = args;
  if (!tag || !file || !repo) {
    console.error('usage: discussion-title.mjs <tag> <body-file> [--repo owner/name]');
    process.exit(2);
  }
  try {
    console.log(rename(repo, tag, readFileSync(file, 'utf8')));
  } catch (err) {
    console.log(`::warning::The release discussion keeps its bare version as title: ${err.message}`);
  }
}

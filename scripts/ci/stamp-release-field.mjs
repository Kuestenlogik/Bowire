#!/usr/bin/env node
// Stamps the board's Release field with the version a release shipped (docs/contributing/
// project-board.md, "Milestones and releases"): every issue of the shipped milestones — in
// the main repo and, by mirrored milestone title, in the sibling repos on the board — gets
// the option `v<major.minor>`, created when it does not exist yet. The field is empty until
// this runs; the milestone is the section, the field the version.
//
// Usage: node scripts/ci/stamp-release-field.mjs <tag> <milestone title>[|<milestone title>…]
// Needs a token with project scope (GH_TOKEN). Never fails the release: problems are printed.
import { execFileSync } from 'node:child_process';

const [tag, titlesArg] = process.argv.slice(2);
if (!tag || !titlesArg) { console.error('usage: stamp-release-field.mjs <tag> <milestone titles |-separated>'); process.exit(2); }
const titles = titlesArg.split('|').map(t => t.trim()).filter(Boolean);
const version = 'v' + tag.replace(/^v/, '').replace(/-.*$/, '').split('.').slice(0, 2).join('.');
const ORG = 'Kuestenlogik', PROJECT = 2, FIELD = 'PVTSSF_lADOD0911s4BZcCtzhZ7lwk';
const gh = (...a) => execFileSync('gh', a, { encoding: 'utf8' });

try {
  const field = JSON.parse(gh('api', 'graphql', '-f', `query={ node(id: "${FIELD}") { ... on ProjectV2SingleSelectField { options { id name color description } } } }`)).data.node.options;
  let option = field.find(o => o.name === version);
  if (!option) {
    const all = [{ name: version, color: 'GRAY', description: `Ausgeliefert als ${tag}` }, ...field];
    const inner = all.map(o => `{${o.id ? `id: "${o.id}", ` : ''}name: ${JSON.stringify(o.name)}, color: ${o.color || 'GRAY'}, description: ${JSON.stringify(o.description || '')}}`).join(',');
    gh('api', 'graphql', '-f', `query=mutation { updateProjectV2Field(input: {fieldId: "${FIELD}", name: "Release", singleSelectOptions: [${inner}]}) { projectV2Field { ... on ProjectV2SingleSelectField { options { id name } } } } }`);
    option = JSON.parse(gh('api', 'graphql', '-f', `query={ node(id: "${FIELD}") { ... on ProjectV2SingleSelectField { options { id name } } } }`)).data.node.options.find(o => o.name === version);
    console.log(`created option ${version}`);
  }
  const projectId = gh('project', 'view', String(PROJECT), '--owner', ORG, '--format', 'json', '--jq', '.id').trim();
  const items = JSON.parse(gh('project', 'item-list', String(PROJECT), '--owner', ORG, '--limit', '1000', '--format', 'json')).items;
  let n = 0;
  for (const it of items) {
    const ms = typeof it.milestone === 'object' ? it.milestone?.title : it.milestone;
    if (!ms || !titles.includes(ms)) continue;
    if (it.release === version) continue;
    gh('project', 'item-edit', '--project-id', projectId, '--id', it.id, '--field-id', FIELD, '--single-select-option-id', option.id);
    n++;
  }
  console.log(`Release ${version} stamped on ${n} item(s) of ${titles.join(', ')}`);
} catch (err) {
  console.log(`::warning::Release field not stamped: ${err.message}`);
}

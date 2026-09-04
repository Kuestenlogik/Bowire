// Shared discovery for protocol pages contributed by plugin repositories.
//
// A protocol plugin that lives in its own repository used to have its
// documentation here, in a repository it cannot push to. `docs/protocols/dis.md`
// claimed `probeDuration` was configurable for weeks while the plugin hard-coded
// three seconds — not because anyone was careless, but because the sentence and
// the behaviour were in different repositories and could only be fixed in
// different commits. The rule is now: the doc lives with the code.
//
// Discovery is by topic, never by a checked-in list, so a new plugin repository
// appears on the site without an edit here.

export const PLUGIN_TOPIC = 'bowire-plugin';

/** Where a plugin repository keeps the page it contributes. */
export const SOURCE_PATH = 'docs/protocol.md';

/** The org whose repositories are considered. */
export const ORG = 'Kuestenlogik';

/**
 * `Bowire.Protocol.TacticalApi` → `tacticalapi`.
 *
 * The destination filename is derived **here, from the repository name**, and
 * never from anything the plugin repository sends. That is what keeps one
 * plugin from writing over another's page: a repository contributes exactly one
 * file, at exactly one path, and it does not get to choose which.
 */
export function slugFor(repoName) {
    const suffix = repoName.replace(/^Bowire\.Protocol\./, '');
    if (suffix === repoName) return null; // not a protocol plugin repo
    return suffix.toLowerCase();
}

/**
 * Every protocol-plugin repository in the org, as `{ repo, slug }`.
 *
 * @param {(url: string) => Promise<Response>} fetchImpl injectable for tests
 * @param {string|undefined} token GitHub token; public repos work without one
 */
export async function discoverPluginRepos(fetchImpl = fetch, token = process.env.GITHUB_TOKEN) {
    const headers = { Accept: 'application/vnd.github+json' };
    if (token) headers.Authorization = `Bearer ${token}`;

    const found = [];
    for (let page = 1; page <= 10; page++) {
        const res = await fetchImpl(
            `https://api.github.com/orgs/${ORG}/repos?per_page=100&page=${page}`,
            { headers });
        if (!res.ok) throw new Error(`repo listing failed: ${res.status} ${res.statusText}`);
        const batch = await res.json();
        if (!Array.isArray(batch) || batch.length === 0) break;

        for (const repo of batch) {
            if (repo.archived) continue;
            if (!(repo.topics || []).includes(PLUGIN_TOPIC)) continue;
            const slug = slugFor(repo.name);
            if (!slug) continue;
            found.push({ repo: repo.name, slug, branch: repo.default_branch || 'main' });
        }
        if (batch.length < 100) break;
    }

    // Two repositories claiming one slug would have them overwrite each other,
    // silently, in whatever order the listing happened to return. Loud is better.
    const bySlug = new Map();
    for (const entry of found) {
        const clash = bySlug.get(entry.slug);
        if (clash) throw new Error(`${clash.repo} and ${entry.repo} both map to '${entry.slug}'`);
        bySlug.set(entry.slug, entry);
    }

    return found.sort((a, b) => a.slug.localeCompare(b.slug));
}

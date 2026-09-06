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

/**
 * Ceiling for a fetched page, in bytes.
 *
 * A protocol page is prose; the largest one in the org is a few tens of
 * kilobytes. The number is less a guess at "big enough" than a refusal
 * to write an unbounded remote body to disk in CI — whatever a
 * repository serves at that path, the build has a fixed cost.
 */
export const MAX_PAGE_BYTES = 512 * 1024;

/**
 * Markup a documentation page has no reason to carry, and that a page on
 * bowire.io would execute.
 *
 * DocFX passes raw HTML straight through markdown into the rendered
 * page, so anything matched here would reach a visitor's browser on our
 * own origin. The repositories are the org's own, which makes this a
 * supply-chain guard rather than an untrusted-input one: it is what
 * stops a single compromised plugin repository from putting script on
 * the documentation site, and it costs nothing on a page that is what it
 * claims to be.
 *
 * A rejection, not a scrub. Stripping the tags would publish a page
 * subtly unlike its source, which is worse than saying the page could
 * not be read — the placeholder path already exists for exactly that, is
 * visible in the build log, and repairs itself on the next run.
 */
const UNSAFE_MARKUP = [
    [/<\s*script\b/i, '<script>'],
    [/<\s*iframe\b/i, '<iframe>'],
    [/<\s*object\b/i, '<object>'],
    [/<\s*embed\b/i, '<embed>'],
    [/<\s*form\b/i, '<form>'],
    [/<\s*meta\b[^>]*http-equiv/i, 'a <meta http-equiv> redirect'],
    [/<[^>]+\son[a-z]+\s*=/i, 'an inline event handler'],
    [/javascript\s*:/i, 'a javascript: URL'],
    [/data\s*:\s*text\/html/i, 'a data:text/html URL'],
];

/**
 * Why this body must not be published, or null when it may be.
 *
 * @param {string} body      the fetched page
 * @param {string} sourcePath the path it was fetched from, for the message
 * @returns {string | null}
 */
export function rejectPage(body, sourcePath = SOURCE_PATH) {
    if (typeof body !== 'string') return `${sourcePath} did not decode as text`;

    const bytes = Buffer.byteLength(body, 'utf8');
    if (bytes > MAX_PAGE_BYTES) {
        return `page is ${bytes} bytes, over the ${MAX_PAGE_BYTES}-byte ceiling`;
    }

    // The toc generator reads `title` out of the front matter, so a page
    // without it appears in the navigation as a bare slug —
    // indistinguishable from a broken build. Say which it is.
    if (!body.trimStart().startsWith('---')) {
        return `${sourcePath} has no YAML front matter`;
    }

    for (const [pattern, what] of UNSAFE_MARKUP) {
        if (pattern.test(body)) return `${sourcePath} contains ${what}`;
    }
    return null;
}

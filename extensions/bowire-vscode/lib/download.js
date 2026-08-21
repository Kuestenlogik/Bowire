// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #590 — the fourth step of the resolution chain: a CLI the extension fetches
// and owns, used only when the setting, the tool manifest and PATH all come up
// empty.
//
// The logic lives here rather than in extension.js for the same reason
// workbench.js does: everything below is reachable from `node --test`, with the
// network, the filesystem and the extractor injected. The only thing the VS
// Code side contributes is the question it asks the user and the progress bar
// it draws.
//
// Two properties are deliberate and worth stating up front:
//
//   * Nothing is fetched without being asked. The caller decides; this module
//     never reaches the network on its own, which is what keeps the extension
//     in line with Bowire's rule that outbound calls are opt-in.
//   * Nothing is unpacked before its digest matches. The archive is streamed to
//     a `.part` file and hashed on the way past, so the check happens on the
//     bytes that landed on disk rather than on a copy of them, and a mismatch
//     is discovered while the payload is still an inert temporary file.

'use strict';

const { createHash } = require('node:crypto');
const { spawnSync } = require('node:child_process');
const fsp = require('node:fs/promises');
const { join: joinPath } = require('node:path');

/**
 * The CLI version a managed download fetches.
 *
 * Pinned rather than "latest" on purpose — that is the correctness argument for
 * this whole step. `PATH` resolves to whatever the machine happens to have; a
 * managed CLI is the version this extension was tested against, so the two
 * cannot drift.
 *
 * It is not read from package.json: the extension carries its own version
 * (0.1.0 today) and the product versions independently, so tying them together
 * would make the extension's next patch bump silently change which CLI it
 * fetches. Bump this deliberately, alongside the release it is verified with.
 */
const PINNED_CLI_VERSION = '2.5.0';

/** Where release assets live. Tag form is `v<version>`. */
const RELEASE_BASE = 'https://github.com/Kuestenlogik/Bowire/releases/download';

/** The digest manifest published beside the archives (added for this feature). */
const CHECKSUMS_ASSET = 'checksums.txt';

/**
 * The runtime identifier for a platform/architecture pair, or null when Bowire
 * publishes no build for it.
 *
 * Returning null rather than guessing matters: a wrong RID produces a 404 from
 * a URL the user cannot do anything about, whereas "no build for this platform"
 * is a complete answer.
 */
function ridFor(platform = process.platform, arch = process.arch) {
    const os = { win32: 'win', darwin: 'osx', linux: 'linux' }[platform];
    const cpu = { x64: 'x64', arm64: 'arm64' }[arch];
    return os && cpu ? `${os}-${cpu}` : null;
}

/**
 * The published asset name for a RID.
 *
 * These names are fixed by the release workflow, which archives Windows RIDs as
 * zip and the rest as tar.gz, and says so in a comment asking that they stay
 * stable — the marketing site links them through
 * `releases/latest/download/<name>` too.
 */
function assetNameFor(rid) {
    return rid.startsWith('win-') ? `bowire-${rid}.zip` : `bowire-${rid}.tar.gz`;
}

/** Archive and digest-manifest URLs for a version and RID. */
function downloadUrls(version, rid) {
    const tag = `v${version}`;
    return {
        archive: `${RELEASE_BASE}/${tag}/${assetNameFor(rid)}`,
        checksums: `${RELEASE_BASE}/${tag}/${CHECKSUMS_ASSET}`,
    };
}

/**
 * The digest for one asset out of a `sha256sum` manifest.
 *
 * Both output forms are accepted. GNU coreutils writes `<hash>  <name>` in text
 * mode and `<hash> *<name>` in binary mode, and which one a release carries
 * depends on the runner that produced it — a parser that only knows one of them
 * works until the day it doesn't.
 *
 * Returns the lowercase hash, or null when the manifest does not mention the
 * asset at all. Null is a real answer here: it means the release predates
 * published digests, which the caller reports rather than papering over.
 */
function expectedDigest(manifestText, assetName) {
    for (const line of String(manifestText ?? '').split(/\r?\n/)) {
        const match = line.match(/^([0-9a-fA-F]{64})\s+\*?(.+?)\s*$/);
        if (match && match[2] === assetName) return match[1].toLowerCase();
    }
    return null;
}

/**
 * Where a managed CLI for a version lands.
 *
 * Keyed by version so a bump downloads beside the old one rather than over it,
 * which means a half-finished fetch can never leave a working install
 * corrupted. The nested `bowire-<rid>` is not a choice — it is the directory
 * the archives carry inside them.
 */
function managedCliPath(root, version, rid, platform = process.platform) {
    const binary = platform === 'win32' ? 'bowire.exe' : 'bowire';
    return joinPath(root, 'cli', version, `bowire-${rid}`, binary);
}

/** The directory a version's download owns, from the same root. */
function managedVersionDir(root, version) {
    return joinPath(root, 'cli', version);
}

/**
 * Which downloaded versions are no longer wanted.
 *
 * An extension that bumps its pin every release would otherwise leave a 120 MB
 * directory behind each time, in a location most people never look at.
 */
function staleCliVersions(present, keep = PINNED_CLI_VERSION) {
    return (present ?? []).filter(name => name !== keep);
}

/** Why no download is on offer for this machine. */
function unsupportedPlatformMessage(platform = process.platform, arch = process.arch) {
    return `Bowire publishes no build for ${platform}/${arch}, so it cannot be downloaded for you. `
        + 'Install it from source, or set `bowire.cliPath` to point at a build you have.';
}

/** Why a download stopped short of installing anything. */
function digestMismatchMessage(assetName, expected, actual) {
    return `The download of ${assetName} does not match the checksum published with the release `
        + `(expected ${expected.slice(0, 16)}…, got ${actual.slice(0, 16)}…). `
        + 'Nothing was installed. This usually means the transfer was corrupted — try again, '
        + 'and if it repeats, report it.';
}

/** Why a release cannot be verified at all. */
function missingChecksumsMessage(version) {
    return `Bowire ${version} publishes no checksums.txt, so a download cannot be verified. `
        + 'Install it with `winget install Kuestenlogik.Bowire`, `choco install bowire`, '
        + 'or `dotnet tool install -g Kuestenlogik.Bowire.Tool` instead.';
}

/** The offer itself, worded so the size and the destination are not a surprise. */
function downloadOfferMessage(version) {
    return `Bowire was not found on this machine. Download Bowire ${version} for the editor to use? `
        + 'It is about 60 MB, lands in the extension\'s own storage, and does not touch your workspace '
        + 'or your PATH.';
}

/**
 * Fetch a URL as text, failing with the URL in the message.
 *
 * A bare "fetch failed" in a notification is unactionable; naming the asset at
 * least says whether the release, the network or the platform is at fault.
 */
async function fetchText(url, { fetchImpl = globalThis.fetch, signal } = {}) {
    const response = await fetchImpl(url, { signal, redirect: 'follow' });
    if (!response.ok) {
        const error = new Error(`${url} returned ${response.status}.`);
        error.status = response.status;
        throw error;
    }
    return await response.text();
}

/**
 * Stream a URL to a file, hashing as it goes and reporting progress.
 *
 * Hashing the stream rather than re-reading the finished file is not an
 * optimisation: it hashes the bytes that were actually written, so a truncated
 * write cannot pass a check performed on something else.
 *
 * `onProgress` receives a 0..1 fraction, or null when the server sends no
 * Content-Length — the caller shows an indeterminate bar in that case rather
 * than a bar that lies.
 */
async function downloadToFile(url, destination, options = {}) {
    const { fetchImpl = globalThis.fetch, signal, onProgress = () => {} } = options;

    const response = await fetchImpl(url, { signal, redirect: 'follow' });
    if (!response.ok) {
        const error = new Error(`${url} returned ${response.status}.`);
        error.status = response.status;
        throw error;
    }

    const declared = Number(response.headers?.get?.('content-length') ?? 0);
    const total = Number.isFinite(declared) && declared > 0 ? declared : null;

    const hash = createHash('sha256');
    let received = 0;

    const handle = await fsp.open(destination, 'w');
    try {
        for await (const chunk of response.body) {
            const buffer = Buffer.from(chunk);
            hash.update(buffer);
            await handle.write(buffer);
            received += buffer.length;
            onProgress(total ? Math.min(received / total, 1) : null, received, total);
        }
    } finally {
        await handle.close();
    }

    return { digest: hash.digest('hex'), bytes: received };
}

/**
 * Unpack an archive with the system `tar`.
 *
 * One extractor for all three platforms, which is only possible because bsdtar
 * reads zip as well as tar.gz — it ships with Windows 10 1803 and later and
 * with macOS, and Linux only ever gets a tar.gz here, so GNU tar's inability to
 * read zip never comes up.
 *
 * The alternative was bundling an unzip implementation for the sake of one
 * platform, which is a dependency and a supply-chain surface for something the
 * OS already does.
 */
function extractArchive(archivePath, destination, runner = spawnSync) {
    const result = runner('tar', ['-xf', archivePath, '-C', destination], { encoding: 'utf8' });
    if (result.error || result.status !== 0) {
        const detail = result.error?.message || String(result.stderr || '').trim() || `exit ${result.status}`;
        throw new Error(`Could not unpack ${archivePath}: ${detail}`);
    }
}

/**
 * Fetch, verify and unpack a managed CLI. Returns the path to the executable.
 *
 * The order is the contract: digests first, then the archive, then the check,
 * and only then anything that touches the install directory. A release without
 * digests never gets as far as downloading 60 MB, and a corrupted download
 * never gets as far as being made executable.
 */
async function installManagedCli(options = {}) {
    const {
        root,
        version = PINNED_CLI_VERSION,
        platform = process.platform,
        arch = process.arch,
        fetchImpl = globalThis.fetch,
        signal,
        onProgress = () => {},
        runner = spawnSync,
        fs = fsp,
    } = options;

    const rid = ridFor(platform, arch);
    if (!rid) throw new Error(unsupportedPlatformMessage(platform, arch));

    const asset = assetNameFor(rid);
    const urls = downloadUrls(version, rid);

    // Digests before payload. Discovering a release cannot be verified after
    // pulling 60 MB down a metered connection is a poor trade for one request.
    let manifest;
    try {
        manifest = await fetchText(urls.checksums, { fetchImpl, signal });
    } catch (err) {
        if (err.status === 404) throw new Error(missingChecksumsMessage(version));
        throw err;
    }

    const expected = expectedDigest(manifest, asset);
    if (!expected) throw new Error(missingChecksumsMessage(version));

    const versionDir = managedVersionDir(root, version);
    await fs.mkdir(versionDir, { recursive: true });

    // `.part` while in flight: an interrupted download leaves a name that
    // cannot be mistaken for a finished one, and the next attempt overwrites it.
    const archivePath = joinPath(versionDir, `${asset}.part`);
    const { digest } = await downloadToFile(urls.archive, archivePath, { fetchImpl, signal, onProgress });

    if (digest !== expected) {
        await fs.rm(archivePath, { force: true });
        throw new Error(digestMismatchMessage(asset, expected, digest));
    }

    extractArchive(archivePath, versionDir, runner);
    await fs.rm(archivePath, { force: true });

    const cli = managedCliPath(root, version, rid, platform);

    // Archive members carry their mode, but only from archivers that record
    // one — a zip round-trip through a Windows runner does not. Setting it
    // explicitly costs nothing and is the difference between a working install
    // and EACCES on macOS.
    if (platform !== 'win32') await fs.chmod(cli, 0o755);

    // Only once the new version is complete. Pruning first would turn a failed
    // download into the loss of a working one.
    await pruneStaleVersions(root, version, fs);

    return cli;
}

/** Remove downloads for versions this extension no longer pins. */
async function pruneStaleVersions(root, keep = PINNED_CLI_VERSION, fs = fsp) {
    const cliRoot = joinPath(root, 'cli');
    let present;
    try {
        present = await fs.readdir(cliRoot);
    } catch {
        return [];
    }

    const stale = staleCliVersions(present, keep);
    for (const name of stale) {
        // Best-effort: a locked directory on Windows is not a reason to fail an
        // install that already succeeded.
        try { await fs.rm(joinPath(cliRoot, name), { recursive: true, force: true }); }
        catch { /* next start tries again */ }
    }
    return stale;
}

module.exports = {
    PINNED_CLI_VERSION,
    CHECKSUMS_ASSET,
    ridFor,
    assetNameFor,
    downloadUrls,
    expectedDigest,
    managedCliPath,
    managedVersionDir,
    staleCliVersions,
    unsupportedPlatformMessage,
    digestMismatchMessage,
    missingChecksumsMessage,
    downloadOfferMessage,
    fetchText,
    downloadToFile,
    extractArchive,
    installManagedCli,
    pruneStaleVersions,
};

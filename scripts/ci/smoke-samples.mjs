#!/usr/bin/env node
// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// Sample smoke test — starts every sample, then asserts the thing our
// unit tests structurally cannot: that the workbench a user actually
// opens against that sample WORKS.
//
// Why this exists: "every sample must be CI-built" caught compile
// errors and nothing else. A 2026-07 QA pass found nine real defects in
// code that had been green for months — a discovery crash that reported
// "0 services", three OData methods that were discovered but answered
// 404/405, and unguarded optional-package calls that blanked the
// embedded UI. Every one of them needed the sample to RUN, not build.
//
// What it checks per sample:
//   1. the process starts and prints a listening URL
//   2. GET  {url}/bowire            → 200 (embedded workbench is mounted)
//   3. GET  {url}/bowire/api/services → 200 and, unless the sample needs
//      an external broker, at least one service
//   4. every discovered method is invoked once with an empty payload;
//      the invoke must not answer 404 / 405 / 5xx — "Bowire discovered
//      it" and "you can call it" have to mean the same thing
//
// Deliberately NOT checked here: browser-side behaviour. A ReferenceError
// in the workbench bundle is invisible over HTTP; that needs a headless
// browser and lives in a separate job.
//
// Usage:
//   node scripts/ci/smoke-samples.mjs                 # all samples
//   node scripts/ci/smoke-samples.mjs Rest OData      # a subset
//   BOWIRE_SMOKE_CONFIG=Release node scripts/ci/…     # CI builds Release

import { spawn } from 'node:child_process';
import { readdir, access } from 'node:fs/promises';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
// Lower-cased: the SDK's artifacts layout names the pivot folder `debug` /
// `release`, not `Debug` / `Release`. Case matters on Linux, where CI runs —
// the env var is still written the familiar way, so this normalises rather
// than demanding callers know the on-disk spelling.
const CONFIG = (process.env.BOWIRE_SMOKE_CONFIG || 'Debug').toLowerCase();
const SAMPLES_DIR = join(REPO, 'samples');

// Samples whose protocol needs a broker we do not start here. They are
// written to boot and serve the workbench anyway, so we still check that
// — we just don't demand discovered services.
const NEEDS_EXTERNAL_BROKER = new Set(['Nats', 'Pulsar']);

// Invoke outcomes that mean "discovery advertised something that isn't
// there". A 400/422 from an empty payload is fine — that's the server
// validating input, which is the sample working as intended.
const BROKEN_STATUS = /^HTTP (40[45]|5\d\d)$/;

const START_TIMEOUT_MS = 90_000;
const INVOKE_TIMEOUT_MS = 15_000;
const STREAM_PROBE_MS = 3_000;

const only = process.argv.slice(2);
const failures = [];
const notes = [];

async function exists(p) {
  try { await access(p); return true; } catch { return false; }
}

async function discoverSamples() {
  const entries = await readdir(SAMPLES_DIR, { withFileTypes: true });
  const out = [];
  for (const e of entries) {
    if (!e.isDirectory()) continue;
    const m = /^Kuestenlogik\.Bowire\.Sample\.(.+)$/.exec(e.name);
    if (!m) continue;                       // socketio-chat is Node — separate job
    const name = m[1];
    if (only.length && !only.includes(name)) continue;
    const dll = join(REPO, 'artifacts', 'bin', e.name, CONFIG, `${e.name}.dll`);
    if (!(await exists(dll))) {
      notes.push(`${name}: no build output at ${dll} — skipped (build the solution first)`);
      continue;
    }
    out.push({ name, dll, needsBroker: NEEDS_EXTERNAL_BROKER.has(name) });
  }
  return out.sort((a, b) => a.name.localeCompare(b.name));
}

// Start the sample and resolve with its base URL, read from the host's
// own "Now listening on: …" line. Parsing stdout rather than hard-coding
// ports keeps this script correct when a sample moves.
function startSample(sample) {
  return new Promise((resolve, reject) => {
    const child = spawn('dotnet', [sample.dll], {
      cwd: dirname(sample.dll),
      env: { ...process.env, DOTNET_ENVIRONMENT: 'Production' },
      stdio: ['ignore', 'pipe', 'pipe'],
    });

    let log = '';
    let settled = false;
    const timer = setTimeout(() => {
      if (settled) return;
      settled = true;
      child.kill('SIGKILL');
      reject(new Error(`did not report a listening URL within ${START_TIMEOUT_MS / 1000}s\n${tail(log)}`));
    }, START_TIMEOUT_MS);

    const onData = (buf) => {
      log += buf.toString();
      const m = /Now listening on:\s*(https?:\/\/\S+)/.exec(log);
      if (m && !settled) {
        settled = true;
        clearTimeout(timer);
        resolve({ child, baseUrl: m[1].replace(/\/$/, ''), getLog: () => log });
      }
    };
    child.stdout.on('data', onData);
    child.stderr.on('data', onData);

    child.on('exit', (code) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      reject(new Error(`exited with code ${code} before listening\n${tail(log)}`));
    });
  });
}

const tail = (s, n = 25) => s.split('\n').slice(-n).join('\n');

async function getJson(url, ms = INVOKE_TIMEOUT_MS) {
  const ctrl = new AbortController();
  const t = setTimeout(() => ctrl.abort(), ms);
  try {
    const res = await fetch(url, { signal: ctrl.signal });
    const text = await res.text();
    let json = null;
    try { json = JSON.parse(text); } catch { /* not json */ }
    return { status: res.status, json, text };
  } finally {
    clearTimeout(t);
  }
}

async function invokeUnary(baseUrl, service, method, serverUrl) {
  const ctrl = new AbortController();
  const t = setTimeout(() => ctrl.abort(), INVOKE_TIMEOUT_MS);
  const qs = serverUrl ? `?serverUrl=${encodeURIComponent(serverUrl)}` : '';
  try {
    const res = await fetch(`${baseUrl}/bowire/api/invoke${qs}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ service, method, messages: ['{}'] }),
      signal: ctrl.signal,
    });
    const text = await res.text();
    let json = null;
    try { json = JSON.parse(text); } catch { /* not json */ }
    return { httpStatus: res.status, json, text };
  } finally {
    clearTimeout(t);
  }
}

// Streaming methods only get a "does it open" probe: we subscribe, wait
// a moment, and abort. A hang or an immediate error is the signal.
async function probeStream(baseUrl, service, method, serverUrl) {
  const params = { service, method, messages: JSON.stringify(['{}']) };
  if (serverUrl) params.serverUrl = serverUrl;
  const qs = new URLSearchParams(params);
  const ctrl = new AbortController();
  const t = setTimeout(() => ctrl.abort(), STREAM_PROBE_MS);
  try {
    const res = await fetch(`${baseUrl}/bowire/api/invoke/stream?${qs}`, { signal: ctrl.signal });
    return { httpStatus: res.status };
  } catch (err) {
    // Abort is the expected end of a healthy stream probe.
    if (err?.name === 'AbortError') return { httpStatus: 200, aborted: true };
    throw err;
  } finally {
    clearTimeout(t);
  }
}

async function smokeOne(sample) {
  process.stdout.write(`\n── ${sample.name}\n`);
  let started;
  try {
    started = await startSample(sample);
  } catch (err) {
    failures.push(`${sample.name}: failed to start — ${err.message}`);
    return;
  }

  const { child, baseUrl, getLog } = started;
  let logAtDiscovery = null;
  try {
    // 1. embedded workbench mounted
    const ui = await getJson(`${baseUrl}/bowire`);
    if (ui.status !== 200) {
      failures.push(`${sample.name}: GET /bowire → ${ui.status} (embedded workbench not reachable)`);
      return;
    }
    process.stdout.write(`   workbench   ${baseUrl}/bowire → 200\n`);

    // 2. discovery — ask the way the Sources rail does.
    //
    // Every sample bundles a <proto>-catalogue.json naming the URL it
    // wants discovered, INCLUDING the path and protocol hint
    // ("graphql@http://host:5183/graphql"). Asking /api/services with no
    // serverUrl instead probes the host ROOT, which finds nothing for
    // every sample whose surface lives under a path — a false alarm that
    // says nothing about the sample. Fall back to the root probe only
    // when a sample ships no catalogue.
    const cat = await getJson(`${baseUrl}/bowire/api/catalogue/entries`, 20_000);
    const entries = Array.isArray(cat.json?.entries) ? cat.json.entries
      : Array.isArray(cat.json) ? cat.json : [];
    const targets = entries.length
      ? entries.map(e => {
        const proto = Array.isArray(e.protocols) && e.protocols.length ? e.protocols[0] : null;
        // A catalogue url may already carry its own hint
        // ("grpcweb@http://…"); prefixing again yields "grpc@grpcweb@…",
        // which no plugin claims.
        const alreadyHinted = /^[a-z][a-z0-9]*@/i.test(e.url);
        return {
          label: e.name || e.url,
          serverUrl: proto && !alreadyHinted ? `${proto}@${e.url}` : e.url,
        };
      })
      : [{ label: 'self', serverUrl: null }];

    const services = [];
    for (const t of targets) {
      const url = t.serverUrl
        ? `${baseUrl}/bowire/api/services?serverUrl=${encodeURIComponent(t.serverUrl)}`
        : `${baseUrl}/bowire/api/services`;
      const svc = await getJson(url, 30_000);
      if (svc.status !== 200 || !Array.isArray(svc.json)) {
        const detail = svc.json?.title ? `${svc.json.title} — ${svc.json.detail ?? ''}` : svc.text.slice(0, 200);
        if (sample.needsBroker) {
          notes.push(`${sample.name}: ${t.label} not discoverable without its broker (expected) — ${detail}`);
          continue;
        }
        failures.push(`${sample.name}: discovery of ${t.label} (${t.serverUrl ?? 'self'}) → ${svc.status} ${detail}`);
        continue;
      }
      for (const one of svc.json) {
        one.__serverUrl = t.serverUrl; // route the invoke to the same target
        services.push(one);
      }
    }
    const methodCount = services.reduce((n, s) => n + (s.methods?.length ?? 0), 0);
    process.stdout.write(`   discovery   ${services.length} service(s), ${methodCount} method(s)\n`);
    // Everything logged up to here is startup + discovery. What the host
    // logs during the invoke sweep is a different animal: every method is
    // called with an EMPTY payload, so a handler rejecting a missing
    // required argument is behaving correctly, not failing.
    logAtDiscovery = getLog();

    if (services.length === 0) {
      if (sample.needsBroker) {
        notes.push(`${sample.name}: 0 services without its broker (expected)`);
      } else {
        failures.push(`${sample.name}: discovery returned 0 services — the sample serves a schema, so this is a defect`);
      }
      return;
    }

    // 3. every discovered method must be callable
    let checked = 0;
    for (const s of services) {
      for (const m of s.methods ?? []) {
        const streaming = (m.methodType ?? 'Unary') !== 'Unary';
        try {
          if (streaming) {
            const r = await probeStream(baseUrl, s.name, m.name, s.__serverUrl);
            if (r.httpStatus >= 400) {
              failures.push(`${sample.name}: stream ${s.name}/${m.name} → HTTP ${r.httpStatus}`);
            }
          } else {
            const r = await invokeUnary(baseUrl, s.name, m.name, s.__serverUrl);
            if (r.httpStatus >= 500) {
              failures.push(`${sample.name}: invoke ${s.name}/${m.name} → HTTP ${r.httpStatus}`);
            } else if (r.json?.status && BROKEN_STATUS.test(String(r.json.status))) {
              // Discovered but not actually there — exactly the OData
              // "3 of 5 methods 404" shape.
              failures.push(
                `${sample.name}: invoke ${s.name}/${m.name} → ${r.json.status}` +
                ` — discovered but not served`);
            }
          }
          checked++;
        } catch (err) {
          failures.push(`${sample.name}: invoke ${s.name}/${m.name} threw — ${err.message}`);
        }
      }
    }
    process.stdout.write(`   invoked     ${checked} method(s)\n`);
  } finally {
    child.kill('SIGKILL');
    // Surface host-side exceptions even when the HTTP checks passed:
    // a swallowed discovery failure logs a warning and returns [].
    const log = logAtDiscovery ?? getLog();
    const suspicious = log.split('\n').filter(l => /\bUnhandled exception|\bfail:|Discovery failed/i.test(l));
    if (suspicious.length) {
      failures.push(`${sample.name}: host logged errors —\n      ${suspicious.slice(0, 5).join('\n      ')}`);
    }
    await new Promise(r => setTimeout(r, 250));
  }
}

const samples = await discoverSamples();
if (samples.length === 0) {
  console.error('No built samples found. Build the solution first (dotnet build Kuestenlogik.Bowire.slnx).');
  process.exit(2);
}

process.stdout.write(`Smoke-testing ${samples.length} sample(s) [${CONFIG}]\n`);
for (const s of samples) {
  await smokeOne(s);
}

if (notes.length) {
  process.stdout.write('\nNotes:\n');
  for (const n of notes) process.stdout.write(`  · ${n}\n`);
}

if (failures.length) {
  process.stdout.write(`\n${failures.length} failure(s):\n`);
  for (const f of failures) process.stdout.write(`  ✗ ${f}\n`);
  process.exit(1);
}

process.stdout.write(`\nAll ${samples.length} sample(s) smoke-tested clean.\n`);

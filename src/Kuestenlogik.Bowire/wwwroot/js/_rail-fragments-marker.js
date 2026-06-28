// @generated
// This file is a fragment of the assembled `wwwroot/bowire.js` bundle.
//
// #311 — per-rail JS fragments live on the sibling Bowire NuGets as
// embedded resources. BowireHtmlGenerator scans every loaded assembly
// whose name starts with `Kuestenlogik.Bowire` (excluding Core itself,
// the Tool, and Map), pulls the embedded JS resource named
// `<asm>.wwwroot.js.*.js`, and replaces the marker below with the
// concatenation. The replacement happens inside the IIFE opened by
// prologue.js + closed by init.js, so the rail fragments see every
// helper / state / closure declared in core.
//
// v2.1 (#325): the previous `Rail.*` startsWith filter widened to
// "any Kuestenlogik.Bowire.* sibling" because Welle 2 dropped the
// Rail. prefix from every package id (Compose / Recordings / Flows /
// Workspaces / Benchmarking / Interceptor live at the flat namespace
// now). Security, Mock, and Help all participate through the same
// channel — same resource-name convention, same splice site.
//
// If no rail packages are referenced (e.g. Bundle.Minimal embedded
// host without per-rail NuGets), the marker is replaced with the
// empty string — the bundle stays valid JS, just smaller.
//
// Must come AFTER every core fragment that declares the symbols the
// rail JS references (request-builder.js, request-builder-protocols.js,
// render-sidebar.js, render-main.js, helpers.js,
// presets.js, &c.) and BEFORE init.js so its DOMContentLoaded handler
// runs after the rails have wired their globals.
/*BOWIRE_RAIL_FRAGMENTS_BEGIN*/
/*BOWIRE_RAIL_FRAGMENTS_END*/

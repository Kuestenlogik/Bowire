---
# The release's `#` heading. A sentence about this delivery, not the version.
title: <fill in before the tag>
version: 2.11.0
---

<One-sentence frame for what 2.11 is about. Replaces this placeholder
before the tag.>

## Highlights

<!-- Add a section per landed feature as the work merges. Pattern —
note the example heading is indented so the placeholder detector in
release.yml (which counts lines matching `^### `) does not mistake this
template for a filled-in body:

  ### <headline> (#issue)
  <2-4 sentences>
-->

## Fixes along the way

### A compiled sidecar ships one manifest for every platform (#990)

On Windows a manifest's `executable` is now also looked up in the plugin directory with each `PATHEXT` extension, so `"executable": "echo"` starts the shipped `echo.exe` instead of the first `echo.exe` on `PATH`. A Go or Rust sidecar names its binary once and runs on Linux, macOS and Windows.

### An embedded workbench no longer keeps a disposed host alive (#989)

The plugin update check kept the `HttpClient` that `AddHttpClient` registers for the default name, and with it the host's root service provider, so a host that embeds the workbench was never collected after it was disposed. The check now owns its client, which also recycles its connections every two minutes so a DNS change reaches it. A test holds that no singleton Bowire registers takes an `HttpClient` from DI.

## Breaking changes

<!-- Each change has been on a back-compat ramp through the prior minor
and is removed in this release. Add a section per breaking change, with
the migration path. -->

## Acknowledgements

<!-- Optional. Names of contributors who exercised rc / reported. -->

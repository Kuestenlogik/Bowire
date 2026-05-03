# Microsoft Store listing

This is a **listing-only** distribution path. Bowire ships through
the Store as an *unpackaged Win32 app* — Microsoft links to our
GitHub-hosted MSI, the user gets a Store experience, but there's no
MSIX repackaging and no sandbox.

This directory holds the listing copy + the manual one-time setup
steps. There's nothing to commit beyond docs because the actual
listing lives in the Microsoft Partner Center.

## Why unpackaged Win32 instead of MSIX

- **No code-signing requirement.** MSIX would force Authenticode
  signing (or Store-hosted signing with publisher verification).
  Unpackaged listings install the same MSI we ship via winget, with
  the same SmartScreen behaviour.
- **No sandbox.** Bowire loads plugins from `~/.bowire/plugins/`,
  reaches out to arbitrary hosts on arbitrary ports, runs an embedded
  webserver. MSIX would need explicit capabilities + `runFullTrust`
  to do all of that. Unpackaged Win32 doesn't have those constraints.
- **One artefact across channels.** The Store, winget, and direct
  download all point at the same `Bowire-<version>-x64.msi` URL —
  fewer formats to validate per release.

The trade-off is no Store-managed auto-update lifecycle (the Store
shows a "Get" button that runs the MSI once); subsequent updates ride
the GitHub Release URL via the Store's "manifest poll" mechanism.

## One-time setup (manual)

1. **Partner Center account.** [partner.microsoft.com](https://partner.microsoft.com/)
   → Developer account → "Individual" ($19 once) or "Company" ($99
   once). Use the Küstenlogik identity, not a personal one.

2. **Verify publisher identity.** Email + business address verification
   takes 1–3 business days. The verified display name appears as
   `Publisher` on the listing.

3. **Create a new app submission.** In Partner Center → "Apps and
   games" → "+ Create new app" → "Bring an existing app". Pick
   "Unpackaged Win32 app". Reserve `Bowire` as the display name.

4. **Fill the listing**. Most fields mirror the winget manifest
   (`packaging/winget/template/KuestenLogik.Bowire.locale.en-US.yaml`)
   so the metadata is consistent across channels:

   | Store field        | Source / value |
   |--------------------|----------------|
   | Display name       | `Bowire` |
   | Publisher          | `Küstenlogik` |
   | Description        | Long-form copy from the locale manifest |
   | Short description  | One-line lede from `ShortDescription` |
   | Categories         | Developer tools |
   | Tags               | api, grpc, rest, graphql, signalr, websocket, … |
   | Privacy policy URL | TBD — Bowire currently has no privacy concerns to declare (no telemetry, no accounts, no cloud); a single-paragraph "no data leaves your machine" page on the Jekyll site is enough |
   | Support URL        | <https://github.com/Kuestenlogik/Bowire/issues> |

5. **Upload the installer.** Pick "URL" as the source, paste the
   stable GitHub Release URL pattern:
   `https://github.com/Kuestenlogik/Bowire/releases/download/v<version>/Bowire-<version>-x64.msi`

   For arm64, add a second entry with the matching arm64 MSI URL.

6. **Screenshots.** The Store wants at least one 1366×768 (or
   1920×1080) screenshot. Reuse the Bowire UI captures already
   produced for the marketing site under `site/assets/images/`.

7. **Submit for review.** Microsoft reviews listings for malware /
   policy compliance. Turnaround is typically 24–72 hours; first-time
   listings can take up to a week.

## Per-release flow (after the listing is live)

For minor updates the Store auto-detects new versions when the
release URL pattern stays stable. Users see the new version on next
Store refresh; clicking "Update" downloads the fresh MSI from
GitHub.

For major changes (publisher name, screenshots, description text)
re-submit through Partner Center → existing app → "Update". No code
change required — only metadata flows through Partner Center.

## What we don't do via the Store

- **Auto-update via Microsoft Store servicing**. That's an MSIX-only
  feature.
- **In-app purchases.** Bowire is Apache-2.0 OSS.
- **Telemetry / analytics.** Bowire collects nothing; the listing
  reflects that.

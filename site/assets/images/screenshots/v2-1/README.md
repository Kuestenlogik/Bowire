# v2.1 screenshots

Captured by `scripts/screenshots/capture-v2-1.js` against the live
samples the operator runs locally. Re-run after a UI change with:

```
# (operator-managed: keep these running first)
#   http://localhost:5180             Tool standalone (workbench at /)
#   https://localhost:5101/bowire     Combined Harbor sample
#   http://localhost:5181/bowire      Sample.Embedded (REST + Map)
#   http://localhost:5182             Sample.TacticalApi (gRPC)
node scripts/screenshots/capture-v2-1.js
```

The script bails one-line if `@playwright/test` isn't available and
each capture is independent — partial failures don't kill the run.

## Captures

| File | What it shows | Capture target | Clip |
| ---- | ------------- | -------------- | ---- |
| `rail-strip.png` | Activity rail column under the topbar — mode buttons + group separators in their v2.1 stack. | Combined `https://localhost:5101/bowire` | `x:0 y:56 w:56 h:720` |
| `discover-with-response.png` | Discover rail with a List method open + executed; sidebar tree, request pane, response card all visible. The layout IS the story so this is full-pane. | Combined | full pane (1400×900) |
| `compose-library-left.png` | Compose rail mode — request pane open under the v2.1 layout. The library-on-the-left flip from v2.0. | Combined | full pane |
| `settings-plugins-protocols.png` | Settings → Plugins → Protocols sub-page listing bundled protocol plugins (REST, WebSocket, gRPC, …) with their update / uninstall / inspect buttons. | Combined | `x:100 y:60 w:1200 h:800` |
| `help-rail.png` | Help rail target. Combined doesn't ship the Help provider package (`/api/help/available` returns false), so this falls back to the discover layout. Re-run against Tool standalone to get the actual help tree + topic body. | Combined (fallback) | full pane |
| `streaming-state-badge.png` | Response-pane header for a server-streaming gRPC call (HarborService.WatchCrane). Shows the SERVER STREAM state pill + method header. | Combined | `x:720 y:110 w:700 h:80` |

## Known gaps (services-side, not script-side)

- **map-pins** is NOT included. The geo-coordinate endpoint (`/api/locations`) lives on `Sample.Embedded` only — Combined has docks/ships/port-calls but no coordinate-bearing payload. Sample.Embedded's bundled UI throws `ReferenceError: loadFlows is not defined` during boot, which halts JS before the method list renders, so the workbench surface is unreachable. Fix the embedded bundle (or ship Bowire.Flows with it) and rerun the script — `captureMapPins` is already wired and waiting.
- **help-rail** shows a fallback (discover) view because Combined doesn't ship the Help provider package. Restart the standalone Tool at 5180 (the harness lost it mid-run on this pass) and rerun — `captureHelpRail` will pick it up.
- **settings-plugins-protocols** lands on the Plugins parent's protocols content rather than the explicit Protocols sub-leaf. `openSettings()` always seeds `settingsTab='general'` on no-arg invoke; the script clicks the Plugins parent which sets `settingsTab='configure-protocols'`. Visually equivalent.

## Why these and not the older script-set

The pre-v2.0 capture scripts under `scripts/screenshots/capture-*.js`
target UI surfaces that don't exist anymore (collections rail, drawer
help, single-source sidebar). They are kept on disk for reference but
should be archived rather than revived. This new script targets only
the v2.1 surfaces the site + docs need.

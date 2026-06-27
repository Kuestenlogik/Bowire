#!/bin/bash
# Smoke test: build Bowire + the two sibling plugin repos
# (Bowire.Protocol.Dis, Bowire.Protocol.Udp), install them into a
# throw-away plugin directory via `bowire plugin install`, and verify
# that `bowire plugin list` + `bowire plugin inspect` both see them.
#
# Run from the Bowire repo root:
#   ./scripts/smoke-dis-udp.sh
#
# Assumes the DIS and UDP plugin repos are checked out as siblings of
# this one at:
#   ../Bowire.Protocol.Dis
#   ../Bowire.Protocol.Udp
# Override with env vars DIS_REPO / UDP_REPO if needed.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIS_REPO="${DIS_REPO:-$ROOT/../Bowire.Protocol.Dis}"
UDP_REPO="${UDP_REPO:-$ROOT/../Bowire.Protocol.Udp}"
VERSION="${VERSION:-0.9.4-smoke}"
SMOKE_DIR="${SMOKE_DIR:-$ROOT/artifacts/smoke-plugins}"

die() { echo "error: $*" >&2; exit 1; }

[[ -d "$DIS_REPO" ]] || die "DIS repo not found at $DIS_REPO (set DIS_REPO to override)"
[[ -d "$UDP_REPO" ]] || die "UDP repo not found at $UDP_REPO (set UDP_REPO to override)"

echo "==> Packing Bowire ($VERSION)"
dotnet pack "$ROOT/Kuestenlogik.Bowire.slnx" -c Release -p:Version="$VERSION" --nologo -v quiet

echo "==> Packing Bowire.Protocol.Dis ($VERSION)"
dotnet pack "$DIS_REPO/Bowire.Protocol.Dis.slnx" -c Release -p:Version="$VERSION" \
    -p:KL_Bowire_Version="$VERSION" --nologo -v quiet

echo "==> Packing Bowire.Protocol.Udp ($VERSION)"
dotnet pack "$UDP_REPO/Bowire.Protocol.Udp.slnx" -c Release -p:Version="$VERSION" \
    -p:KL_Bowire_Version="$VERSION" --nologo -v quiet

echo "==> Resetting $SMOKE_DIR"
rm -rf "$SMOKE_DIR"
mkdir -p "$SMOKE_DIR"

# Aggregate feed so the installer resolves every transitive Kuestenlogik.Bowire*
# dependency from one place — Bowire itself, the plugin packages, and
# their deps.
SMOKE_FEED="$ROOT/artifacts/smoke-feed"
rm -rf "$SMOKE_FEED"
mkdir -p "$SMOKE_FEED"
cp "$ROOT/artifacts/packages/"*.nupkg "$SMOKE_FEED/" 2>/dev/null || true
cp "$DIS_REPO/artifacts/packages/"*.nupkg "$SMOKE_FEED/" 2>/dev/null || true
cp "$UDP_REPO/artifacts/packages/"*.nupkg "$SMOKE_FEED/" 2>/dev/null || true

BOWIRE="dotnet run --project $ROOT/src/Kuestenlogik.Bowire.Tool/Kuestenlogik.Bowire.Tool.csproj --no-build -- "
echo "==> Installing DIS plugin"
$BOWIRE plugin install Kuestenlogik.Bowire.Protocol.Dis \
    --version "$VERSION" --source "$SMOKE_FEED" --source "https://api.nuget.org/v3/index.json" --plugin-dir "$SMOKE_DIR"

echo "==> Installing UDP plugin"
$BOWIRE plugin install Kuestenlogik.Bowire.Protocol.Udp \
    --version "$VERSION" --source "$SMOKE_FEED" --source "https://api.nuget.org/v3/index.json" --plugin-dir "$SMOKE_DIR"

echo "==> plugin list (verbose)"
$BOWIRE plugin list --verbose --plugin-dir "$SMOKE_DIR"

echo "==> plugin inspect Kuestenlogik.Bowire.Protocol.Dis"
$BOWIRE plugin inspect Kuestenlogik.Bowire.Protocol.Dis --plugin-dir "$SMOKE_DIR"

echo "==> plugin inspect Kuestenlogik.Bowire.Protocol.Udp"
$BOWIRE plugin inspect Kuestenlogik.Bowire.Protocol.Udp --plugin-dir "$SMOKE_DIR"

# Grep the inspect outputs for the expected concrete types so the
# script fails loudly if the contract types didn't round-trip through
# the plugin ALC boundary.
DIS_OUT=$($BOWIRE plugin inspect Kuestenlogik.Bowire.Protocol.Dis --plugin-dir "$SMOKE_DIR")
UDP_OUT=$($BOWIRE plugin inspect Kuestenlogik.Bowire.Protocol.Udp --plugin-dir "$SMOKE_DIR")
echo "$DIS_OUT" | grep -q 'BowireDisProtocol' \
    || die "DIS plugin: BowireDisProtocol not found in inspect output"
echo "$UDP_OUT" | grep -q 'BowireUdpProtocol' \
    || die "UDP plugin: BowireUdpProtocol not found in inspect output"

# IBowireMockEmitter is the extension point that `bowire mock` picks
# up via PluginManager.EnumeratePluginServices. A regression in the
# plugin ALC walker would silently unwire DIS proactive replay —
# assert the type is visible in the inspect output so CI catches it.
echo "$DIS_OUT" | grep -q 'DisMockEmitter' \
    || die "DIS plugin: DisMockEmitter not found in inspect output — mock-emitter extension point broken"

echo ""
echo "OK — both plugins installed, listed, and inspect-reported their IBowireProtocol types."
echo "DIS plugin also exposes IBowireMockEmitter (DisMockEmitter) for 'bowire mock' proactive replay."
echo "Plugin dir: $SMOKE_DIR"
echo "Local feed: $SMOKE_FEED"

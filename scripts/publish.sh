#!/bin/bash
# Bowire — Publish standalone executables + NuGet packages
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
CONFIGURATION="${1:-Release}"
RUNTIME="${2:-linux-x64}"
VERSION="${3:-0.9.4-dev}"

echo ""
echo "  Bowire — Publish"
echo "  Version:       $VERSION"
echo "  Configuration: $CONFIGURATION"
echo "  Runtime:       $RUNTIME"
echo ""

# NuGet — PackageOutputPath in Directory.Build.props writes to artifacts/packages
echo "━━━ NuGet Packages ━━━"
dotnet pack "$ROOT/Kuestenlogik.Bowire.slnx" -c "$CONFIGURATION" -p:Version="$VERSION" --nologo -v quiet
echo "  Done → $ROOT/artifacts/packages/"
echo ""

# Standalone
echo "━━━ Standalone Executable ($RUNTIME) ━━━"
dotnet publish "$ROOT/src/Kuestenlogik.Bowire.Tool" -c "$CONFIGURATION" -r "$RUNTIME" --self-contained \
    -p:Version="$VERSION" \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=false \
    -p:ReadyToRun=true \
    -p:DebuggerSupport=false \
    -o "$ROOT/artifacts/publish/bowire-$RUNTIME" --nologo -v quiet

SIZE=$(du -sh "$ROOT/artifacts/publish/bowire-$RUNTIME" 2>/dev/null | cut -f1)
echo "  $SIZE → artifacts/publish/bowire-$RUNTIME"
echo ""
echo "  Done. Run: ./artifacts/publish/bowire-$RUNTIME/bowire --url https://my-server:443"
echo ""

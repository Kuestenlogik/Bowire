#!/bin/bash
# Bowire — Build a self-contained OCI container image as a tarball.
#
# Uses the .NET 10 SDK's built-in container support
# (`dotnet publish /t:PublishContainer`) — no Dockerfile, no docker
# daemon required when ContainerArchiveOutputPath is set. Output goes to
# artifacts/containers/bowire-<version>.tar.gz.
#
# Load the resulting image with:
#   docker load < artifacts/containers/bowire-<version>.tar.gz
#   podman load < artifacts/containers/bowire-<version>.tar.gz
#
# Usage:
#   scripts/publish-container.sh [version] [arch]
#     version  semver string for the image tag (default: 0.9.4-dev)
#     arch     linux-x64 (default) or linux-arm64
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
VERSION="${1:-0.9.4-dev}"
ARCH="${2:-linux-x64}"

OUTPUT_DIR="$ROOT/artifacts/containers"
ARCHIVE_PATH="$OUTPUT_DIR/bowire-$VERSION-$ARCH.tar.gz"

mkdir -p "$OUTPUT_DIR"

echo ""
echo "  Bowire — Container build"
echo "  Version: $VERSION"
echo "  Arch:    $ARCH"
echo "  Output:  $ARCHIVE_PATH"
echo ""

dotnet publish "$ROOT/src/Kuestenlogik.Bowire.Tool" \
    -c Release \
    -r "$ARCH" \
    -p:Version="$VERSION" \
    -p:ContainerImageTags='"'"$VERSION"';latest"' \
    -p:ContainerArchiveOutputPath="$ARCHIVE_PATH" \
    -t:PublishContainer \
    --nologo

echo ""
SIZE=$(du -sh "$ARCHIVE_PATH" 2>/dev/null | cut -f1)
echo "  Done. ($SIZE)"
echo ""
echo "  Load the image with:"
echo "    docker load < $ARCHIVE_PATH"
echo "    podman load < $ARCHIVE_PATH"
echo ""
echo "  Run with:"
echo "    docker run --rm -p 5080:5080 kuestenlogik/bowire:$VERSION --url https://target:443 --no-browser"
echo ""

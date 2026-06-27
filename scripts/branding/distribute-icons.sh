#!/usr/bin/env bash
# Push the current icon artefacts from images/ into every consumer
# location (embedded Bowire UI, docfx docs, marketing site). Also
# renders one extra file that doesn't live in images/ — the iOS
# apple-touch-icon (180×180 PNG).
#
# Usage:
#   scripts/distribute-icons.sh
#
# Does NOT regenerate images/ — run scripts/generate-icons.sh first
# if the sources need rebuilding. Safe to run any time: it's a pure
# copy/render step.
#
# Shares the transient tmp/icon-gen/ deps pocket with
# generate-icons.sh (sharp is the only runtime requirement). First
# run writes the inline package.json and npm-installs; subsequent
# runs skip the install.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPS_DIR="$SCRIPT_DIR/../tmp/icon-gen"

if [ ! -d "$DEPS_DIR/node_modules" ]; then
    mkdir -p "$DEPS_DIR"
    cat > "$DEPS_DIR/package.json" <<'JSON'
{
  "name": "bowire-icon-tool-deps",
  "version": "1.0.0",
  "private": true,
  "description": "Transient dependency pocket for scripts/generate-icons.js and scripts/distribute-icons.js. Regenerated on demand from the wrappers — do not edit by hand.",
  "dependencies": {
    "png-to-ico": "^2.1.8",
    "sharp": "^0.34.0"
  }
}
JSON
    echo "Installing icon-tool dependencies (one-time) ..."
    (cd "$DEPS_DIR" && npm install --silent)
fi

NODE_PATH="$DEPS_DIR/node_modules" node "$SCRIPT_DIR/distribute-icons.js"

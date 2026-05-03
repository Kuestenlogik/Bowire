#!/usr/bin/env bash
# Regenerate every derivative icon artefact under images/ from the
# two committed source SVGs (bowire_logo.svg + bowire_logo_small.svg).
#
# Usage:
#   scripts/generate-icons.sh
#
# Does NOT touch wwwroot/docs/site — use scripts/distribute-icons.sh
# afterwards to push the regenerated files to consumer locations.
#
# Dependencies (sharp, png-to-ico) live in tmp/icon-gen/, which is
# gitignored via the repo-wide `tmp/` rule and fully transient.
# First run writes the inline package.json, runs `npm install`
# there, and caches the deps; any subsequent run skips the install.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPS_DIR="$SCRIPT_DIR/../tmp/icon-gen"

# Install transient deps if the deps dir or node_modules is missing.
# The package.json is emitted inline so this wrapper is self-contained:
# delete scripts/generate-icons/ at any time and the next run will
# rebuild it from scratch.
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
    echo "Installing icon-generator dependencies (one-time) ..."
    (cd "$DEPS_DIR" && npm install --silent)
fi

# Run the generator with NODE_PATH pointing at the cached node_modules
# so the script's `require('sharp')` finds them even though the .js
# file lives one level up in scripts/.
NODE_PATH="$DEPS_DIR/node_modules" node "$SCRIPT_DIR/generate-icons.js"

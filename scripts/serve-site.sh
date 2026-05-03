#!/usr/bin/env bash
# Local preview for the marketing site + docs. Uses `npx http-server`
# because it matches GitHub Pages' URL semantics out of the box:
#
#   /docs/ui-guide           → 302 → /docs/ui-guide/        (directory)
#   /docs/ui-guide/          → 200 (serves index.html)
#   /docs/ui-guide/sidebar   → 200 (serves sidebar.html via auto-ext)
#   /docs/ui-guide/sidebar.html → 200
#
# The alternative (`npx serve`) does not redirect bare directory URLs,
# so relative asset paths like `../public/main.css` inside a page at
# `/docs/ui-guide` resolve against /docs/ instead of /docs/ui-guide/
# and every CSS / JS link 404s. http-server redirects correctly and
# we don't need a per-path serve.json with hand-maintained directory
# entries.
#
# Usage:
#   scripts/serve-site.sh                 # http://localhost:4000
#   scripts/serve-site.sh 8080            # custom port
set -euo pipefail

PORT="${1:-4000}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SITE="$ROOT/site/_site"

if [[ ! -d "$SITE" ]]; then
    echo "error: $SITE does not exist. Run scripts/build-docs.sh first." >&2
    exit 1
fi

echo "==> Serving $SITE on http://localhost:$PORT"
echo "    Home:   http://localhost:$PORT/"
echo "    Docs:   http://localhost:$PORT/docs/"
echo ""
# -c-1 disables caching so CSS/JS edits reload without Ctrl+F5.
# --cors keeps Pagefind's fetch() happy on localhost.
exec npx --yes http-server -p "$PORT" -c-1 --cors "$SITE"

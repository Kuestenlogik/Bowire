#!/usr/bin/env bash
# Build the docfx API docs + conceptual markdown, then mirror the
# output into site/docs/ so the Jekyll marketing site serves them at
# /docs/. Single source of truth for local "rebuild everything" and
# for the CI docs workflow — keep them in sync.
#
# Usage:
#   scripts/build-docs.sh                 # full rebuild
#   scripts/build-docs.sh --serve         # build then start Jekyll serve
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# Make Ruby/Bundler discoverable on the typical Windows install path.
# Git Bash finds the WindowsApps stubs (bundle, ruby) on PATH but
# they need the real install dir to actually run — prepend whichever
# Ruby<ver>-x64 dir we find first.
for ruby_dir in "/c/Program Files/Ruby40-x64/bin" "/c/Program Files/Ruby32-x64/bin"; do
    if [[ -d "$ruby_dir" ]]; then
        export PATH="$ruby_dir:$PATH"
        break
    fi
done

echo "==> Restoring .NET tools"
dotnet tool restore >/dev/null 2>&1 || true

echo "==> Building main solution (release) for API metadata"
dotnet build Kuestenlogik.Bowire.slnx -c Release --nologo -v q

echo "==> Running docfx build"
docfx build docs/docfx.json

echo "==> Mirroring docs into site/docs for Jekyll"
rm -rf site/docs
cp -r artifacts/docs site/docs

echo "==> Building Jekyll site (for local preview)"
# Jekyll turns every site/*.{html,md,yml} plus the site/docs mirror
# into site/_site/. If Ruby/bundle isn't available locally, fall
# through to a direct file mirror so the preview still works —
# scripts/serve-site.sh serves site/_site/ via `npx http-server`
# which handles directory-URL trailing slashes correctly without a
# hand-maintained redirect list.
if command -v bundle >/dev/null 2>&1; then
    (cd site && bundle exec jekyll build >/dev/null)
else
    echo "  (Jekyll/bundle not found — falling back to direct mirror of site/ → site/_site/)"
    mkdir -p site/_site
    rsync -a --delete --exclude='_site' --exclude='_site_preview' \
          --exclude='Gemfile*' --exclude='_config.yml' --exclude='_includes' \
          --exclude='_layouts' --exclude='workflows' \
          site/ site/_site/
fi

echo "==> Generating unified Pagefind index over site + docs"
npx -y pagefind --site site/_site --output-path site/_site/pagefind >/dev/null

echo "==> Done. Pagefind index at site/_site/pagefind/."

if [[ "${1:-}" == "--serve" ]]; then
    echo "==> Starting Jekyll serve"
    cd site && bundle exec jekyll serve --watch
fi

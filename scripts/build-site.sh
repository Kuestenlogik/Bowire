#!/usr/bin/env bash
# Site-only rebuild: runs Jekyll, then refreshes the Pagefind index.
# Use this for marketing-site iterations that don't touch the docs
# templates or the API metadata — saves a couple of minutes of
# `dotnet build` + `docfx build` per iteration.
#
# When the docs change, use scripts/build-docs.sh instead — it does
# the same Jekyll + Pagefind step on top of a full DocFX rebuild.
#
# Usage:
#   scripts/build-site.sh                 # rebuild + index
#   scripts/build-site.sh --serve         # rebuild + index + jekyll serve
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# Make Ruby/Bundler discoverable on the typical Windows install path
# when the surrounding shell hasn't put it on PATH. Git Bash on
# Windows finds the WindowsApps stubs (bundle, ruby) on PATH, but
# those stubs need the real Ruby install to actually run — so we
# always prepend the real install dir if we see it. First match wins.
for ruby_dir in "/c/Program Files/Ruby40-x64/bin" "/c/Program Files/Ruby32-x64/bin"; do
    if [[ -d "$ruby_dir" ]]; then
        export PATH="$ruby_dir:$PATH"
        break
    fi
done

echo "==> Building Jekyll site"
if command -v bundle >/dev/null 2>&1; then
    (cd site && bundle exec jekyll build >/dev/null)
else
    echo "  (Jekyll/bundle not found — falling back to direct mirror of site/ -> site/_site/)"
    mkdir -p site/_site
    rsync -a --delete --exclude='_site' --exclude='_site_preview' \
          --exclude='Gemfile*' --exclude='_config.yml' --exclude='_includes' \
          --exclude='_layouts' --exclude='workflows' \
          site/ site/_site/
fi

echo "==> Generating Pagefind index"
# Pagefind owns the search-overlay assets the marketing-site header
# requests at /pagefind/pagefind-ui.{js,css}. Jekyll wipes _site/ on
# every build, so without this step the search button 404s.
npx -y pagefind --site site/_site --output-path site/_site/pagefind >/dev/null

echo "==> Done."

if [[ "${1:-}" == "--serve" ]]; then
    echo "==> Starting Jekyll serve"
    cd site && bundle exec jekyll serve --watch
fi

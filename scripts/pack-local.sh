#!/bin/bash
VERSION="${1:-0.9.4-local}"
# PackageOutputPath in Directory.Build.props points at artifacts/packages,
# so a plain `dotnet pack` writes there directly. No -o override needed.
PACKAGES="$(dirname "$0")/../artifacts/packages"

echo "Packing Kuestenlogik.Bowire v${VERSION}..."
dotnet pack Kuestenlogik.Bowire.slnx -c Release -p:Version="$VERSION"

echo ""
echo "Package published to: $(cd "$PACKAGES" && pwd)"
echo "To use in other projects, add to nuget.config:"
echo "  <add key=\"local\" value=\"$(cd "$PACKAGES" && pwd)\" />"

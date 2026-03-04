#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$REPO_ROOT/src/Sextant.Cli/Sextant.Cli.csproj"
TOOL_NAME="sextant.cli"
NUPKG_DIR="$REPO_ROOT/artifacts/nupkg"

echo "==> Checking for existing installation..."
if dotnet tool list -g | grep -q "$TOOL_NAME"; then
    echo "    Found existing installation. Uninstalling..."
    dotnet tool uninstall -g "$TOOL_NAME"
    echo "    Uninstalled."
else
    echo "    No existing installation found."
fi

echo "==> Packing $PROJECT..."
rm -rf "$NUPKG_DIR"
dotnet pack "$PROJECT" -c Release -o "$NUPKG_DIR" --nologo -v quiet

NUPKG_FILE=$(ls "$NUPKG_DIR"/*.nupkg 2>/dev/null | head -1)
if [ -z "$NUPKG_FILE" ]; then
    echo "ERROR: No .nupkg file produced in $NUPKG_DIR"
    exit 1
fi

echo "==> Installing from $NUPKG_FILE..."
dotnet tool install -g "$TOOL_NAME" --add-source "$NUPKG_DIR" --prerelease

echo ""
echo "==> Done! Run 'sextant --help' to verify."

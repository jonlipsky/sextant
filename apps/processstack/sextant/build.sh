#!/usr/bin/env bash
# Publishes the Sextant app's activities bundle to activities/sextant/ (relative to this directory).
# The host provides the ProcessStack SDK, so the bundle must carry no ProcessStack.*.dll.
# Extra arguments are passed to `dotnet publish`.
set -euo pipefail

cd "$(dirname "$0")"

output="activities/sextant"
rm -rf "$output"
dotnet publish src/Sextant.ProcessStack.Activities -c Release -o "$output" "$@"

echo "Published $output:"
(cd "$output" && ls -1)

if compgen -G "$output/ProcessStack.*.dll" > /dev/null; then
  echo "error: the bundle must not ship ProcessStack SDK assemblies:" >&2
  (cd "$output" && ls -1 ProcessStack.*.dll) >&2
  exit 1
fi

for required in Sextant.ProcessStack.Activities.dll Sextant.Core.dll; do
  if [[ ! -f "$output/$required" ]]; then
    echo "error: the bundle is missing $required" >&2
    exit 1
  fi
done

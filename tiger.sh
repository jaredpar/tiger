#!/usr/bin/env bash
set -euo pipefail

cd -- "$(dirname -- "${BASH_SOURCE[0]}")"

git fetch origin
git merge origin/main --ff-only
dotnet build
exec dotnet artifacts/bin/Tiger/debug/tiger.dll "$@"

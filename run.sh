#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

exec dotnet run \
  --project "$repo_root/src/MSBuild.Dag.Cli/MSBuild.Dag.Cli.csproj" \
  -- \
  "$@"

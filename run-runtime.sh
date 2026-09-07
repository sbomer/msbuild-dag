#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
runtime_root="${RUNTIME_ROOT:-$HOME/src/runtime-dag}"
arcade_version="$(
  sed -n 's/.*"Microsoft.DotNet.Arcade.Sdk": "\([^"]*\)".*/\1/p' \
    "$runtime_root/global.json" |
    head -n 1
)"
package_root="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
toolset_project="$runtime_root/artifacts/toolset/$arcade_version/Build.proj"
runtime_overrides="$repo_root/runtime-overrides.targets"

if [[ ! -f "$toolset_project" ]]; then
  cached_toolset="$package_root/microsoft.dotnet.arcade.sdk/$arcade_version/toolset"

  if [[ ! -f "$cached_toolset/Build.proj" ]]; then
    echo "Arcade toolset package was not found for version $arcade_version." >&2
    echo "Run $runtime_root/build.sh once to restore it." >&2
    exit 1
  fi

  mkdir -p "$(dirname "$toolset_project")"
  cp -R "$cached_toolset/." "$(dirname "$toolset_project")"
fi

export MSBuildDagRuntimeRoot="$runtime_root"
export AfterMicrosoftNETSdkTargets="${AfterMicrosoftNETSdkTargets:-$runtime_overrides}"

exec dotnet run \
  --project "$repo_root/src/MSBuild.Dag.Cli/MSBuild.Dag.Cli.csproj" \
  -- \
  --no-render \
  "$toolset_project" \
  Execute

#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -s)" != Linux ]]; then
  echo "Empacote as Lambdas em Linux para preservar as permissões do bootstrap." >&2
  exit 1
fi

infra_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd -- "$infra_dir/.."

build_dir="$infra_dir/build"
mkdir -p -- "$build_dir"
staging="$(mktemp -d "$build_dir/.publish.XXXXXXXX")"
trap 'rm -rf -- "$staging"' EXIT

publish() {
  local project="$1" destination="$2"

  dotnet publish "src/$project/$project.csproj" \
    --configuration Release \
    --framework net11.0 \
    --runtime linux-x64 \
    --self-contained true \
    -p:UseAppHost=true \
    --output "$destination"

  mv -- "$destination/$project" "$destination/bootstrap"
  chmod 755 -- "$destination/bootstrap"
}

publish SyncForge.Api "$staging/api"
publish SyncForge.Worker "$staging/worker"

rm -rf -- "$build_dir/api" "$build_dir/worker"
mv -- "$staging/api" "$staging/worker" "$build_dir/"
test -x "$build_dir/api/bootstrap"
test -x "$build_dir/worker/bootstrap"

echo "Bundles Linux preparados em infra/build/api e infra/build/worker."

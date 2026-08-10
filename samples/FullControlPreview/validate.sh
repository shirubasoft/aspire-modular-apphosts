#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_directory/../.." && pwd)"
apphost_project="$script_directory/FullControlPreview.AppHost/FullControlPreview.AppHost.csproj"
publish_directory="$(mktemp -d)"

cleanup() {
    rm -r -- "$publish_directory"
}
trap cleanup EXIT

cd "$repository_root"
dotnet tool run aspire -- publish \
    --apphost "$apphost_project" \
    --output-path "$publish_directory" \
    --non-interactive

compose_file="$publish_directory/docker-compose.yaml"
test -f "$compose_file"
grep --quiet --fixed-strings 'image: "nginx:alpine"' "$compose_file"
test "$(grep --count --fixed-strings 'image: "nginx:alpine"' "$compose_file")" -eq 2
grep --quiet --fixed-strings 'image: "redis:7.2.0"' "$compose_file"
if grep --quiet --fixed-strings 'image: "nginx:main"' "$compose_file" ||
   grep --quiet --fixed-strings 'image: "redis:main"' "$compose_file"; then
    echo "The published model retained a default tag instead of the full-control override." >&2
    exit 1
fi

echo "Verified the same full-control tags in the published AppHost model."

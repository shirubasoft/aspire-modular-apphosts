#!/usr/bin/env bash
set -euo pipefail

if (( $# != 1 )); then
  echo "Usage: build-image.sh <image-reference>" >&2
  exit 2
fi

if [[ -n "${ASPIRE_CONTAINER_RUNTIME:-}" ]]; then
  container_runtime="$ASPIRE_CONTAINER_RUNTIME"
elif command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
  container_runtime="docker"
elif command -v podman >/dev/null 2>&1 && podman info >/dev/null 2>&1; then
  container_runtime="podman"
elif command -v docker >/dev/null 2>&1; then
  container_runtime="docker"
elif command -v podman >/dev/null 2>&1; then
  container_runtime="podman"
else
  echo "Docker or Podman is required to build the sample image." >&2
  exit 1
fi

exec "$container_runtime" build --file Dockerfile --tag "$1" .

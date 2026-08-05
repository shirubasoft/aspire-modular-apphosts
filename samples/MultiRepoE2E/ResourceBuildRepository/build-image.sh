#!/usr/bin/env bash
set -euo pipefail

if (( $# != 1 )); then
  echo "Usage: build-image.sh <image-reference>" >&2
  exit 2
fi

container_runtime="${ASPIRE_CONTAINER_RUNTIME:-docker}"
exec "$container_runtime" build --file Dockerfile --tag "$1" .

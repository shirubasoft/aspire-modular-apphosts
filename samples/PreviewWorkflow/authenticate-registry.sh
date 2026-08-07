#!/usr/bin/env bash
set -euo pipefail

: "${REGISTRY_USERNAME:?Set REGISTRY_USERNAME to the registry user.}"
: "${REGISTRY_TOKEN:?Set REGISTRY_TOKEN to the registry token.}"

printf '%s' "$REGISTRY_TOKEN" |
    docker login registry.example.test --username "$REGISTRY_USERNAME" --password-stdin

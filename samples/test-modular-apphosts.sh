#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_directory/.." && pwd)"
active_apphost=""

stop_active_apphost() {
    if [[ -n "$active_apphost" ]]; then
        dotnet tool run aspire -- stop \
            --apphost "$active_apphost" \
            --non-interactive || true
        active_apphost=""
    fi
}

assert_resource_message() {
    local resource="$1"
    local expected="Hello from an arbitrary exported Aspire resource."
    local resource_url
    resource_url="$(
        dotnet tool run aspire -- describe "$resource" \
            --apphost "$active_apphost" \
            --format Json \
            --non-interactive |
            jq --raw-output '[.resources[].urls[] | select(.name == "http") | .url][0] // empty'
    )"
    test -n "$resource_url"
    curl --fail --show-error --silent "$resource_url" |
        jq --exit-status --arg expected "$expected" '.message == $expected' >/dev/null
}

trap stop_active_apphost EXIT
cd "$repository_root"

active_apphost="samples/AppHostA/ModularSample.AppHostA.csproj"
dotnet tool run aspire -- start \
    --apphost "$active_apphost" \
    --isolated \
    --format Json \
    --non-interactive
dotnet tool run aspire -- wait sample-api \
    --apphost "$active_apphost" \
    --timeout 180 \
    --non-interactive
dotnet tool run aspire -- wait sample-static \
    --apphost "$active_apphost" \
    --timeout 180 \
    --non-interactive
dotnet tool run aspire -- wait sample-generated-static \
    --apphost "$active_apphost" \
    --timeout 180 \
    --non-interactive
assert_resource_message sample-api
stop_active_apphost

active_apphost="samples/AppHostB/ModularSample.AppHostB.csproj"
dotnet tool run aspire -- start \
    --apphost "$active_apphost" \
    --isolated \
    --format Json \
    --non-interactive
dotnet tool run aspire -- wait sample-api \
    --apphost "$active_apphost" \
    --timeout 180 \
    --non-interactive
dotnet tool run aspire -- wait sample-static \
    --apphost "$active_apphost" \
    --timeout 180 \
    --non-interactive
dotnet tool run aspire -- wait sample-generated-static \
    --apphost "$active_apphost" \
    --timeout 180 \
    --non-interactive
dotnet tool run aspire -- wait dependency-gateway \
    --apphost "$active_apphost" \
    --timeout 180 \
    --non-interactive
assert_resource_message sample-api

echo "Verified same-module callback resources in project and container modes."

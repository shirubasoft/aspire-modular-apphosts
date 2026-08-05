#!/usr/bin/env bash
set -euo pipefail

run_containers=false
if [[ "${1:-}" == "--containers" ]]; then
  run_containers=true
elif [[ $# -gt 0 ]]; then
  echo "Usage: ./build.sh [--containers]" >&2
  exit 2
fi

dotnet tool restore
dotnet restore Aspire.ModularAppHosts.slnx
dotnet format Aspire.ModularAppHosts.slnx --verify-no-changes --no-restore
dotnet build Aspire.ModularAppHosts.slnx --configuration Release --no-restore
dotnet test Aspire.ModularAppHosts.slnx --configuration Release --no-build --no-restore
dotnet pack src/Aspire.Hosting.ModularAppHosts/Aspire.Hosting.ModularAppHosts.csproj \
  --configuration Release --no-build --no-restore --output artifacts
dotnet pack src/Aspire.Hosting.ModularAppHosts.Testing/Aspire.Hosting.ModularAppHosts.Testing.csproj \
  --configuration Release --no-build --no-restore --output artifacts

if [[ "$run_containers" == true ]]; then
  Parameters__orders_api_key=e2e-orders-key \
  ESHOP_E2E_MODE=compose \
    dotnet test samples/E2ETesting/EShop.E2E.Tests/EShop.E2E.Tests.csproj \
      --configuration Release --no-build --no-restore
fi

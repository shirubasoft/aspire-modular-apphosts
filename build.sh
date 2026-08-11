#!/usr/bin/env bash
set -euo pipefail

run_containers=false
package_version=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --containers)
      run_containers=true
      shift
      ;;
    --package-version)
      if [[ $# -lt 2 || -z "$2" ]]; then
        echo "--package-version requires a value." >&2
        exit 2
      fi

      package_version="$2"
      shift 2
      ;;
    *)
      echo "Usage: ./build.sh [--containers] [--package-version <version>]" >&2
      exit 2
      ;;
  esac
done

version_arguments=()
if [[ -n "$package_version" ]]; then
  version_arguments+=("-p:Version=$package_version")
fi

dotnet tool restore
dotnet restore Aspire.ModularAppHosts.slnx
dotnet format Aspire.ModularAppHosts.slnx --verify-no-changes --no-restore
dotnet build Aspire.ModularAppHosts.slnx --configuration Release --no-restore \
  "${version_arguments[@]}"
dotnet test Aspire.ModularAppHosts.slnx --configuration Release --no-build --no-restore
dotnet pack src/Aspire.Hosting.ModularAppHosts/Aspire.Hosting.ModularAppHosts.csproj \
  --configuration Release --no-build --no-restore --output artifacts \
  "${version_arguments[@]}"
dotnet pack src/Aspire.Hosting.ModularAppHosts.Testing/Aspire.Hosting.ModularAppHosts.Testing.csproj \
  --configuration Release --no-build --no-restore --output artifacts \
  "${version_arguments[@]}"
dotnet pack src/Aspire.Hosting.ModularAppHosts.Tool/Aspire.Hosting.ModularAppHosts.Tool.csproj \
  --configuration Release --no-build --no-restore --output artifacts \
  "${version_arguments[@]}"
dotnet pack templates/Aspire.Hosting.ModularAppHosts.Templates.csproj \
  --configuration Release --no-build --no-restore --output artifacts \
  "${version_arguments[@]}"

if [[ "$run_containers" == true ]]; then
  Parameters__orders_api_key=e2e-orders-key \
  ESHOP_E2E_MODE=compose \
    dotnet test samples/E2ETesting/EShop.E2E.Tests/EShop.E2E.Tests.csproj \
      --configuration Release --no-build --no-restore
fi

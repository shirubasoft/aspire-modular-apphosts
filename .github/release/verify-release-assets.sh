#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 || -z "$1" ]]; then
  echo "Usage: $0 <release-version>" >&2
  exit 2
fi

release_version="$1"
version_file="artifacts/release-version.txt"

if [[ ! -f "$version_file" ]]; then
  echo "The CI artifact does not contain $version_file." >&2
  exit 1
fi

artifact_version="$(<"$version_file")"
if [[ "$artifact_version" != "$release_version" ]]; then
  echo "CI packaged version $artifact_version, but semantic-release selected $release_version." >&2
  exit 1
fi

package_ids=(
  Shirubasoft.Aspire.ModularAppHosts
  Shirubasoft.Aspire.ModularAppHosts.Testing
  Shirubasoft.Aspire.ModularAppHosts.Tool
  Shirubasoft.Aspire.ModularAppHosts.Templates
)

symbol_package_ids=(
  Shirubasoft.Aspire.ModularAppHosts
  Shirubasoft.Aspire.ModularAppHosts.Testing
  Shirubasoft.Aspire.ModularAppHosts.Tool
)

for package_id in "${package_ids[@]}"; do
  package_path="artifacts/$package_id.$release_version.nupkg"
  if [[ ! -s "$package_path" ]]; then
    echo "The CI artifact does not contain $package_path." >&2
    exit 1
  fi
done

for package_id in "${symbol_package_ids[@]}"; do
  package_path="artifacts/$package_id.$release_version.snupkg"
  if [[ ! -s "$package_path" ]]; then
    echo "The CI artifact does not contain $package_path." >&2
    exit 1
  fi
done

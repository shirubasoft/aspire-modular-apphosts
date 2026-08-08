#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_directory/../.." && pwd)"
apphost_project="$script_directory/ImagePush.E2E.AppHost/ImagePush.E2E.AppHost.csproj"
description_directory="$(mktemp -d)"
document="$description_directory/module-images.json"

cleanup() {
    rm -r -- "$description_directory"
}
trap cleanup EXIT

export ImagePush__RegistryEndpoint="registry.example.test"
export ImagePush__RetagRegistryEndpoint="mirror.example.test"

cd "$repository_root"
dotnet tool run aspire -- do describe-images \
    --apphost "$apphost_project" \
    --output-path "$description_directory" \
    --non-interactive

jq --exit-status '
    .schemaVersion == 2 and
    ([.modules[] | {name, contractPackageId}] == [
        {
          "name": "contract-only",
          "contractPackageId": "Sample.ContractOnly"
        },
        {
          "name": "image-push-e2e",
          "contractPackageId": "Sample.ImagePush.Contract"
        },
        {
          "name": "image-push-extra",
          "contractPackageId": null
        }
    ]) and
    ([.images[].effectiveResource] == [
        "image-pull-mapped",
        "image-push-declared",
        "image-push-extra",
        "image-push-factory",
        "image-push-project"
    ]) and
    (.images[] | select(.resource == "image-pull-mapped") |
        .reference == "mirror.example.test/image-pull/local:push-test" and
        .pullReference == "registry.example.test/image-pull/source:push-test" and
        .pushReference == null and
        .build == null) and
    (.images[] | select(.resource == "image-push-declared") |
        .reference == "registry.example.test/image-push/declared:push-test" and
        .pullReference == .reference and
        .pushReference == .reference and
        .build.step == "build-image-push-declared") and
    (.images[] | select(.resource == "image-push-factory") |
        .reference == "registry.example.test/image-push/factory:push-test" and
        .pullReference == .reference and
        .pushReference == .reference and
        .build.step == "build-image-push-factory") and
    (.images[] | select(.resource == "image-push-extra") |
        .module == "image-push-extra" and
        .reference == "registry.example.test/image-push/extra:push-test" and
        .pullReference == .reference and
        .pushReference == .reference and
        .build.step == "build-image-push-extra") and
    (.images[] | select(.resource == "image-push-project") |
        .reference == "image-push-project:push-test" and
        .pullReference == "registry.example.test/image-push/project:push-test" and
        .pushReference == "registry.example.test/image-push/project:push-test" and
        .build.step == "build-image-push-project")
' "$document" >/dev/null

echo "Verified deterministic run, pull, push, and build identities in $document."

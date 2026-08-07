#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_directory/../.." && pwd)"
tool_project="$repository_root/src/Aspire.Hosting.ModularAppHosts.Tool/Aspire.Hosting.ModularAppHosts.Tool.csproj"
temporary_directory="$(mktemp -d)"
verified_request="$temporary_directory/module-preview.verified.json"
generated_workflow="$temporary_directory/module-preview.yml"
image_descriptions="$temporary_directory/images"

cleanup() {
    rm -r -- "$temporary_directory"
}
trap cleanup EXIT

cd "$repository_root"

dotnet run --project "$tool_project" --configuration Release -- \
    preview verify \
    --manifest samples/PreviewWorkflow/external-image-request.json \
    --policy samples/PreviewWorkflow/module-preview-policy.json \
    --output "$verified_request"

jq --exit-status '
    .producer.repository == "https://github.com/example/sample-image-builder.git" and
    .modules[0].repository == "https://github.com/example/sample-module.git" and
    .producer.repository != .modules[0].repository and
    .images[0].repository == "registry.example.test/image-push/declared" and
    .images[0].sha256 == "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
' "$verified_request" >/dev/null

jq --exit-status '
    .schemaVersion == 2 and
    .modules[0].contract.required == true and
    ([.modules[0].images[] | select(.required == true)] | length) == 2 and
    ([.modules[0].images[] | select(.producerRepositories | length > 0)] | length) == 1
' samples/PreviewWorkflow/module-preview-policy.json >/dev/null

jq --exit-status '
    (.contracts | length) == 0 and
    (.images | length) == 1
' "$verified_request" >/dev/null

ImagePush__RegistryEndpoint=registry.example.test \
    dotnet tool run aspire -- do describe-images \
    --apphost samples/ImagePushE2E/ImagePush.E2E.AppHost/ImagePush.E2E.AppHost.csproj \
    --output-path "$image_descriptions" \
    --non-interactive

jq --exit-status --slurpfile descriptor samples/PreviewWorkflow/module-preview.producer.json '
    [
        .images[]
        | select(
            .module == $descriptor[0].module and
            .resource == $descriptor[0].images[0].resource and
            .resourceKind == $descriptor[0].images[0].resourceKind and
            .build != null and
            .pushReference == ($descriptor[0].images[0].repository + ":push-test"))
    ] | length == 1
' "$image_descriptions/module-images.json" >/dev/null

aspire_version="$(jq --raw-output '.tools["aspire.cli"].version' .config/dotnet-tools.json)"
dotnet run --project "$tool_project" --configuration Release -- \
    preview workflow generate producer \
    --descriptor samples/PreviewWorkflow/module-preview.producer.json \
    --working-directory "$repository_root" \
    --apphost samples/ImagePushE2E/ImagePush.E2E.AppHost/ImagePush.E2E.AppHost.csproj \
    --output "$generated_workflow" \
    --repo example/consumer-tests \
    --workflow module-preview-e2e.yml \
    --ref main \
    --aspire-version "$aspire_version" \
    --tool-version 4.4.0 \
    --github-token-secret PREVIEW_AUTOMATION_TOKEN \
    --registry-auth-script samples/PreviewWorkflow/authenticate-registry.sh \
    --secret REGISTRY_USERNAME=SAMPLE_REGISTRY_USERNAME \
    --secret REGISTRY_TOKEN=SAMPLE_REGISTRY_TOKEN

bash -n samples/PreviewWorkflow/authenticate-registry.sh
grep --fixed-strings --quiet 'preview produce' "$generated_workflow"
grep --fixed-strings --quiet -- '--apphost "$GITHUB_WORKSPACE/$APPHOST"' "$generated_workflow"
grep --fixed-strings --quiet 'gh workflow run' "$generated_workflow"
grep --fixed-strings --quiet 'gh run watch' "$generated_workflow"
if grep --fixed-strings --quiet 'jq' "$generated_workflow"; then
    echo "Generated producer workflows must not depend on jq." >&2
    exit 1
fi
grep --fixed-strings --quiet 'samples/PreviewWorkflow/authenticate-registry.sh' "$generated_workflow"

echo "Verified the external image policy and generated producer workflow."

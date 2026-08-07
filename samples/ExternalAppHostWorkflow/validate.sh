#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_directory/../.." && pwd)"
producer_directory="$script_directory/Producer"
apphost_project="$script_directory/Consumer/ExternalConsumer.AppHost/ExternalConsumer.AppHost.csproj"
tool_project="$repository_root/src/Aspire.Hosting.ModularAppHosts.Tool/Aspire.Hosting.ModularAppHosts.Tool.csproj"
container_runtime="${ASPIRE_CONTAINER_RUNTIME:-docker}"
temporary_directory="$(mktemp -d)"
generated_workflow="$temporary_directory/module-preview.yml"
registry_container_id=""
registry_endpoint=""
container_registries_configuration=""
image_tag="external-test"

if [[ -n "$(git -C "$repository_root" status --porcelain --untracked-files=normal)" ]]; then
    image_tag="$image_tag-dirty"
fi

cleanup() {
    if [[ -n "$registry_container_id" ]]; then
        "$container_runtime" rm --force "$registry_container_id" >/dev/null 2>&1 || true
    fi

    "$container_runtime" image rm --force \
        "${registry_endpoint:+$registry_endpoint/external/image:$image_tag}" \
        >/dev/null 2>&1 || true
    if [[ -n "$container_registries_configuration" ]]; then
        rm --force "$container_registries_configuration"
    fi
    rm -r -- "$temporary_directory"
}
trap cleanup EXIT

if find "$producer_directory" -type f \
    \( -name '*.csproj' -o -name 'apphost.cs' -o -name 'apphost.ts' \) \
    | grep --quiet .; then
    echo "The producer fixture must not contain an AppHost." >&2
    exit 1
fi

cd "$repository_root"
aspire_version="$(jq --raw-output '.tools["aspire.cli"].version' .config/dotnet-tools.json)"
dotnet run --project "$tool_project" --configuration Release -- \
    preview workflow generate producer \
    --descriptor samples/ExternalAppHostWorkflow/Producer/module-preview.producer.json \
    --working-directory "$repository_root" \
    --apphost samples/ExternalAppHostWorkflow/Consumer/ExternalConsumer.AppHost/ExternalConsumer.AppHost.csproj \
    --apphost-repository example/consumer-application \
    --apphost-ref main \
    --output "$generated_workflow" \
    --repo example/consumer-tests \
    --workflow e2e.yml \
    --ref main \
    --aspire-version "$aspire_version" \
    --tool-version 5.1.0 \
    --github-token-secret PREVIEW_AUTOMATION_TOKEN \
    --anonymous-registry

grep --fixed-strings --quiet 'gh repo clone "$APPHOST_REPOSITORY" "$APPHOST_CHECKOUT"' "$generated_workflow"
grep --fixed-strings --quiet -- '--apphost "$APPHOST_CHECKOUT/$APPHOST"' "$generated_workflow"
grep --fixed-strings --quiet -- '--pin "$MODULE_NAME=https://github.com/$APPHOST_REPOSITORY.git@$APPHOST_COMMIT"' "$generated_workflow"
grep --fixed-strings --quiet \
    'Aspire__ModularAppHosts__Modules__external-build-source__Containers__external-image__BuildRepository: ${{ github.workspace }}' \
    "$generated_workflow"
grep --fixed-strings --quiet \
    "'Aspire__ModularAppHosts__Modules__external-build-source__Containers__external-image__BuildRepositoryRevision' \"\$producer_commit\"" \
    "$generated_workflow"
if grep --fixed-strings --quiet 'contract-version' "$generated_workflow"; then
    echo "External AppHost workflows must remain image-only." >&2
    exit 1
fi

registry_container_id="$(
    "$container_runtime" run \
        --detach \
        --publish 127.0.0.1::5000 \
        registry:2
)"
registry_binding="$("$container_runtime" port "$registry_container_id" 5000/tcp | tail -n 1)"
registry_port="${registry_binding##*:}"
registry_endpoint="localhost:$registry_port"

if [[ "$container_runtime" == *podman* ]]; then
    container_registries_configuration="$(mktemp)"
    printf '[[registry]]\nlocation="%s"\ninsecure=true\n' "$registry_endpoint" \
        > "$container_registries_configuration"
    export CONTAINERS_REGISTRIES_CONF="$container_registries_configuration"
fi

for _ in {1..40}; do
    if curl --fail --silent --output /dev/null "http://$registry_endpoint/v2/"; then
        break
    fi
    sleep 0.25
done
curl --fail --silent --output /dev/null "http://$registry_endpoint/v2/"

env \
    "ASPIRE_CONTAINER_RUNTIME=$container_runtime" \
    "ExternalAppHost__RegistryEndpoint=$registry_endpoint" \
    "Aspire__ModularAppHosts__Modules__external-build-source__Containers__external-image__BuildRepository=$producer_directory" \
    dotnet tool run aspire -- do push resource:external-image \
        --apphost "$apphost_project" \
        --non-interactive

response="$(curl --fail --silent "http://$registry_endpoint/v2/external/image/tags/list")"
if [[ "$response" != *"\"$image_tag\""* ]]; then
    echo "External build repository image was not pushed with tag '$image_tag': $response" >&2
    exit 1
fi

echo "Verified workflow generation and image publishing for a producer without an AppHost."

#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_directory/../.." && pwd)"
apphost_project="$script_directory/ImagePush.E2E.AppHost/ImagePush.E2E.AppHost.csproj"
fixture_directory="$script_directory/ImageFixture"
container_runtime="${ASPIRE_CONTAINER_RUNTIME:-docker}"
image_tag="push-test"
fixture_image="image-pull-fixture:$image_tag"
project_image="image-push-project:$image_tag"
registry_container_id=""
registry_endpoint=""
container_registries_configuration=""

cleanup() {
    if [[ -n "$registry_container_id" ]]; then
        "$container_runtime" rm --force "$registry_container_id" >/dev/null 2>&1 || true
    fi

    "$container_runtime" image rm --force \
        "$fixture_image" \
        "$project_image" \
        "${registry_endpoint:+$registry_endpoint/image-push/project:$image_tag}" \
        "${registry_endpoint:+$registry_endpoint/image-push/declared:$image_tag}" \
        "${registry_endpoint:+$registry_endpoint/image-push/factory:$image_tag}" \
        >/dev/null 2>&1 || true

    if [[ -n "$container_registries_configuration" ]]; then
        rm --force "$container_registries_configuration"
    fi
}
trap cleanup EXIT

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

remote_project_image="$registry_endpoint/image-push/project:$image_tag"
remote_declared_image="$registry_endpoint/image-push/declared:$image_tag"
remote_factory_image="$registry_endpoint/image-push/factory:$image_tag"

"$container_runtime" build --tag "$fixture_image" "$fixture_directory"
"$container_runtime" tag "$fixture_image" "$remote_project_image"
"$container_runtime" tag "$fixture_image" "$remote_declared_image"
"$container_runtime" tag "$fixture_image" "$remote_factory_image"
"$container_runtime" push "$remote_project_image"
"$container_runtime" push "$remote_declared_image"
"$container_runtime" push "$remote_factory_image"
"$container_runtime" image rm --force \
    "$remote_project_image" \
    "$remote_declared_image" \
    "$remote_factory_image"

assert_image_present() {
    local image="$1"
    if ! "$container_runtime" image inspect "$image" >/dev/null 2>&1; then
        echo "Expected local image '$image' to exist." >&2
        return 1
    fi
}

assert_image_absent() {
    local image="$1"
    if "$container_runtime" image inspect "$image" >/dev/null 2>&1; then
        echo "Expected local image '$image' to be absent." >&2
        return 1
    fi
}

export ImagePush__RegistryEndpoint="$registry_endpoint"
export ASPIRE_CONTAINER_RUNTIME="$container_runtime"

cd "$repository_root"
dotnet tool run aspire -- do pull image-push-project \
    --apphost "$apphost_project" \
    --non-interactive

assert_image_present "$project_image"
assert_image_absent "$remote_declared_image"
assert_image_absent "$remote_factory_image"

dotnet tool run aspire -- do pull \
    --apphost "$apphost_project" \
    --non-interactive

assert_image_present "$project_image"
assert_image_present "$remote_declared_image"
assert_image_present "$remote_factory_image"

echo "Verified scoped and complete Aspire image pulls from $registry_endpoint."

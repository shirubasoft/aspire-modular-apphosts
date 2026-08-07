#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_directory/../.." && pwd)"
tool_project="$repository_root/src/Aspire.Hosting.ModularAppHosts.Tool/Aspire.Hosting.ModularAppHosts.Tool.csproj"
temporary_directory="$(mktemp -d)"
package_feed="$temporary_directory/packages"
nuget_config="$temporary_directory/nuget.config"
descriptor="$temporary_directory/module-preview.producer.json"
mismatch_request="$temporary_directory/module-preview.mismatch.json"
mismatch_error="$temporary_directory/mismatch.error"
source_repository="$temporary_directory/source-repository"
source_policy="$temporary_directory/source-policy.json"
source_manifest="$temporary_directory/source-manifest.json"
source_resolution="$temporary_directory/source-resolution.json"
source_packages="$temporary_directory/source-packages"
source_work="$temporary_directory/source-work"
git_wrapper="$temporary_directory/git-wrapper"
aspire_tool_directory="$temporary_directory/aspire-tool"

cleanup() {
    rm -r -- "$temporary_directory"
}
trap cleanup EXIT

mkdir -p "$package_feed"
dotnet pack "$script_directory/Shared.Contract/Shared.Contract.csproj" \
    --configuration Release \
    --output "$package_feed"

sed "s|PACKAGE_FEED|$package_feed|g" > "$nuget_config" <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="sample" value="PACKAGE_FEED" />
  </packageSources>
</configuration>
EOF

aspire_version="$(jq --raw-output '.tools["aspire.cli"].version' "$repository_root/.config/dotnet-tools.json")"
dotnet tool install aspire.cli \
    --tool-path "$aspire_tool_directory" \
    --version "$aspire_version"

dotnet run --project "$tool_project" --configuration Release -- \
    preview descriptor generate producer \
    --apphost "$script_directory/ContractDependencyPreview.AppHost/ContractDependencyPreview.AppHost.csproj" \
    --module contract-dependency-preview \
    --contract-project "$script_directory/Producer.Contract/Producer.Contract.csproj" \
    --contract-dependency Example.Preview.Shared.Contract \
    --nuget-config "$nuget_config" \
    --output "$descriptor" \
    --aspire-executable "$aspire_tool_directory/aspire" \
    --working-directory "$temporary_directory"

jq --exit-status '
    .schemaVersion == 2 and
    .contract.packageId == "Example.Preview.Producer.Contract" and
    .contract.dependencies == [
      {
        "packageId": "Example.Preview.Shared.Contract",
        "version": "1.4.2"
      }
    ]
' "$descriptor" >/dev/null

dotnet run --project "$tool_project" --configuration Release -- \
    preview verify \
    --manifest "$script_directory/module-preview.json" \
    --policy "$script_directory/module-preview-policy.json"

jq '.contracts[0].dependencies[0].version = "1.5.0"' \
    "$script_directory/module-preview.json" > "$mismatch_request"
if dotnet run --project "$tool_project" --configuration Release -- \
    preview verify \
    --manifest "$mismatch_request" \
    --policy "$script_directory/module-preview-policy.json" \
    2> "$mismatch_error"; then
    echo "Expected the mismatched dependency lock to be rejected." >&2
    exit 1
fi

grep --fixed-strings --quiet "'1.5.0'" "$mismatch_error"
grep --fixed-strings --quiet "'1.4.2'" "$mismatch_error"

mkdir -p "$source_repository/Producer.Contract"
cp "$script_directory/Producer.Contract/Producer.Contract.csproj" \
    "$script_directory/Producer.Contract/ProducedValue.cs" \
    "$source_repository/Producer.Contract/"
git -C "$source_repository" init --quiet
git -C "$source_repository" config user.email sample@example.test
git -C "$source_repository" config user.name "Contract dependency sample"
git -C "$source_repository" add .
git -C "$source_repository" commit --quiet -m "Add producer contract"
source_commit="$(git -C "$source_repository" rev-parse HEAD)"

jq --arg commit "$source_commit" '
    .producer.commit = $commit |
    .modules[0].commit = $commit
' "$script_directory/module-preview.json" > "$source_manifest"
jq '
    del(.modules[0].contract.published) |
    .modules[0].contract.sourceFallback = {
      "enabled": true,
      "project": "Producer.Contract/Producer.Contract.csproj"
    }
' "$script_directory/module-preview-policy.json" > "$source_policy"

real_git="$(command -v git)"
cat > "$git_wrapper" <<EOF
#!/usr/bin/env bash
set -euo pipefail
if [[ "\${1:-}" == "remote" && "\${2:-}" == "add" && "\${3:-}" == "origin" ]]; then
    exec "$real_git" remote add origin "$source_repository"
fi
exec "$real_git" "\$@"
EOF
chmod +x "$git_wrapper"

dotnet run --project "$tool_project" --configuration Release -- \
    preview materialize \
    --manifest "$source_manifest" \
    --policy "$source_policy" \
    --work-directory "$source_work" \
    --package-feed "$source_packages" \
    --resolution "$source_resolution" \
    --consumer-repository https://github.com/example/preview-consumer.git \
    --consumer-commit 89abcdef0123456789abcdef0123456789abcdef \
    --nuget-config "$nuget_config" \
    --git-executable "$git_wrapper" \
    --gh-executable true \
    --docker-executable true

jq --exit-status '
    .schemaVersion == 2 and
    .contracts[0].packageId == "Example.Preview.Producer.Contract" and
    .contracts[0].dependencies == [
      {
        "packageId": "Example.Preview.Shared.Contract",
        "version": "1.4.2"
      }
    ] and
    (.contracts[0].packagePath | endswith(".nupkg"))
' "$source_resolution" >/dev/null

echo "Verified exact contract dependency generation, policy enforcement, and source materialization."

# Cross-repository module previews

Module previews let a developer in a producer repository run a consumer-owned E2E workflow against
the exact commit and immutable container images they have pushed. The .NET tool owns the generic
producer and consumer mechanics; each consumer retains a reviewed allowlist and its application-
specific test assertions.

```mermaid
flowchart LR
    C["Repo C: producer branch"] -->|"produce: commit + artifact digests"| R["Untrusted preview request"]
    R -->|"dispatch"| D["Repo D: trusted default-branch workflow"]
    P["Repo D policy"] --> V["verify + materialize"]
    R --> V
    V --> X["Trusted preview resolution"]
    X --> A["Aspire AppHost"]
    A --> E["Repo D E2E assertions"]
```

The distinction between request and resolution is intentional:

- Repo C controls requested module commits, optional contract versions, and immutable OCI digests.
- Repo D controls whether a contract is required, allowed repositories and package IDs, the
  published package source or source fallback project, MSBuild properties, emitted environment
  variable names, and allowed image repositories.
- `preview verify` is pure and offline. It does not clone repositories, run producer code, or contact
  a registry.
- `preview materialize` revalidates the request, performs only policy-approved work, hashes the
  resulting packages, verifies image digests remotely, and writes the trusted resolution consumed
  by the AppHost.

## Install the tool

Use a repository-local tool manifest so developers and CI use the same version:

```bash
dotnet new tool-manifest # only when .config/dotnet-tools.json does not exist
dotnet tool install Shirubasoft.Aspire.ModularAppHosts.Tool
dotnet tool restore
```

The installed command is `dotnet modular-apphosts`.

## Producer setup

Commit a `module-preview.producer.json` descriptor to Repo C. It describes what the producer may
offer; it contains no credentials, commands, build contexts, or consumer paths.

```json
{
  "schemaVersion": 1,
  "module": "module-c",
  "contract": {
    "packageId": "Acme.ModuleC.Contract"
  },
  "images": [
    {
      "resource": "module-c-api",
      "resourceKind": "container",
      "repository": "ghcr.io/acme/module-c/api",
      "required": true
    }
  ]
}
```

`resourceKind` is `project` when the module contract declares `AddProject(...).ExportAsContainer(...)`
and `container` when it declares `AddContainer(...)`.

Build and push images in Repo C. Capture the registry-reported digest, not a tag. If the origin
workflow already built the image, pass that digest directly to the tool; Repo D does not rebuild it:

```bash
dotnet modular-apphosts preview produce \
  --descriptor module-preview.producer.json \
  --contract-version 2.3.0-preview.7 \
  --image module-c-api=ghcr.io/acme/module-c/api@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef \
  --output module-preview.json
```

The contract version may instead be committed as `contract.version` in the descriptor. Supplying
`--contract-version` overrides that value, which lets CI pass the exact version it just published.
A declared contract must get a version from one of those locations.

Omit `contract` entirely when the preview needs only already-built images:

```json
{
  "schemaVersion": 1,
  "module": "module-c",
  "images": [
    {
      "resource": "module-c-api",
      "resourceKind": "container",
      "repository": "ghcr.io/acme/module-c/api",
      "required": true
    }
  ]
}
```

That produces a request with an empty `contracts` collection. Do not pass `--contract-version` for
an image-only descriptor.

`produce` verifies that the worktree is clean, `HEAD` is attached to a branch, and the exact commit
is the pushed tip of that branch on `origin`. It rejects undeclared images, tags, uppercase or
abbreviated digests, missing required images, and mismatches between a supplied image repository
and the committed descriptor.

Add changed dependencies explicitly; a branch name is never inferred across repositories:

```bash
dotnet modular-apphosts preview produce \
  --descriptor module-preview.producer.json \
  --contract-version 2.3.0-preview.7 \
  --pin module-a=https://github.com/acme/repo-a.git@0123456789abcdef0123456789abcdef01234567 \
  --image module-c-api=ghcr.io/acme/module-c/api@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef \
  --output module-preview.json
```

The request contains only reproducible identities. Package sources and source project paths are
absent because Repo C is not allowed to choose them for Repo D:

```json
{
  "schemaVersion": 1,
  "producer": {
    "repository": "https://github.com/acme/repo-c.git",
    "commit": "89abcdef0123456789abcdef0123456789abcdef",
    "dirty": false,
    "branch": "feat/some-work",
    "baseRef": "refs/heads/main",
    "baseCommit": "0123456789abcdef0123456789abcdef01234567"
  },
  "modules": [
    {
      "name": "module-c",
      "repository": "https://github.com/acme/repo-c.git",
      "commit": "89abcdef0123456789abcdef0123456789abcdef",
      "branch": "feat/some-work",
      "baseRef": "refs/heads/main",
      "baseCommit": "0123456789abcdef0123456789abcdef01234567"
    }
  ],
  "contracts": [
    {
      "module": "module-c",
      "packageId": "Acme.ModuleC.Contract",
      "version": "2.3.0-preview.7"
    }
  ],
  "images": [
    {
      "module": "module-c",
      "resource": "module-c-api",
      "resourceKind": "container",
      "repository": "ghcr.io/acme/module-c/api",
      "sha256": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    }
  ]
}
```

The producer branch and base ref are audit metadata. Consumers use only full immutable commits and
digests as artifact identities.

## Consumer policy

Commit a policy such as `.github/module-preview-policy.json` to Repo D's default branch:

```json
{
  "schemaVersion": 1,
  "modules": [
    {
      "module": "module-c",
      "repository": "https://github.com/acme/repo-c.git",
      "contract": {
        "packageId": "Acme.ModuleC.Contract",
        "versionEnvironment": "ModuleCContractVersion",
        "required": false,
        "published": {
          "source": "https://api.nuget.org/v3/index.json"
        },
        "sourceFallback": {
          "enabled": false
        }
      },
      "images": [
        {
          "resource": "module-c-api",
          "resourceKind": "container",
          "repositories": [
            "ghcr.io/acme/module-c/api"
          ],
          "required": true
        }
      ]
    }
  ]
}
```

The contract policy's `required` value defaults to `true`. Set it to `false` only when Repo D permits
an image-only request; when the request omits the contract, no contract version or package feed is
exported. Image `required: true` independently prevents a producer from omitting a digest and making
the AppHost fall back to a source build.

`published.source` is the preferred contract materialization mode when Repo C already published the
package. It must be a consumer-reviewed, credential-free absolute HTTPS NuGet source without a query
or fragment. Keep credentials in Repo D's NuGet configuration or credential provider, never in the
source URL or preview documents.

Published resolution and an enabled source fallback are mutually exclusive. When a package is not
published, replace `published` with a reviewed fallback:

```json
{
  "sourceFallback": {
    "enabled": true,
    "project": "src/ModuleC.Contract/ModuleC.Contract.csproj"
  },
  "allowedPackProperties": [
    "ModularAppHostsVersion"
  ]
}
```

This fragment represents the corresponding fields inside `contract`. Source fallback permits fixed
`dotnet restore` and `dotnet pack` commands to execute MSBuild from the producer commit, so run it in
a no-secrets job. `allowedPackProperties` applies only to fallback mode and must be empty or omitted
in published mode. Every declared contract policy must enable exactly one materialization mode.

## Verify and materialize in Repo D

Write the workflow input to a file without shell interpolation, then perform offline policy
verification before any producer code is fetched:

```bash
dotnet modular-apphosts preview verify \
  --manifest "$RUNNER_TEMP/module-preview.input.json" \
  --policy .github/module-preview-policy.json \
  --output "$RUNNER_TEMP/module-preview.verified.json"
```

After Repo D has prepared any consumer-owned package dependencies, materialize the request:

```bash
dotnet modular-apphosts preview materialize \
  --manifest "$RUNNER_TEMP/module-preview.verified.json" \
  --policy .github/module-preview-policy.json \
  --work-directory "$RUNNER_TEMP/module-preview-work" \
  --package-feed "$GITHUB_WORKSPACE/.preview-feed" \
  --resolution "$RUNNER_TEMP/module-preview.resolution.json" \
  --consumer-repository "https://github.com/acme/repo-d.git" \
  --consumer-commit "$GITHUB_SHA" \
  --nuget-config "$GITHUB_WORKSPACE/nuget.config" \
  --property ModularAppHostsVersion="$ModularAppHostsVersion" \
  --github-env "$GITHUB_ENV"
```

Materialization performs these operations:

1. Revalidates the strict request and consumer policy.
2. In published mode, restores the exact requested package ID and version with the consumer's NuGet
   configuration, then verifies NuGet's recorded source equals the policy-owned source and verifies
   the package bytes against NuGet's SHA-512 content hash. This permits separately trusted sources
   for transitive dependencies without allowing the requested contract to come from one of them. It
   does not fetch the producer repository or run `dotnet pack`.
3. In source fallback mode, fetches only the exact producer commit and runs fixed `dotnet restore`
   and `dotnet pack` commands against only the policy-owned project path.
4. Opens the resolved `.nupkg`, verifies its nuspec ID and version, records its SHA-256, and copies it
   into the package feed. Published contracts record both `source` and `packagePath` in the trusted
   resolution; fallback contracts record `packagePath` and a null `source`.
5. Verifies every `repository@sha256:...` with `docker buildx imagetools inspect`.
6. Writes the resolution and, when `--github-env` is used, exports `ModulePreview__Resolution`,
   `ModulePreview__PackageFeed`, and each policy-owned contract version environment variable.

`--package-feed` is required only when the request contains a contract. For an accepted image-only
request, omit `--package-feed`, `--nuget-config`, and contract pack properties:

```bash
dotnet modular-apphosts preview materialize \
  --manifest "$RUNNER_TEMP/module-preview.verified.json" \
  --policy .github/module-preview-policy.json \
  --work-directory "$RUNNER_TEMP/module-preview-work" \
  --resolution "$RUNNER_TEMP/module-preview.resolution.json" \
  --consumer-repository "https://github.com/acme/repo-d.git" \
  --consumer-commit "$GITHUB_SHA" \
  --github-env "$GITHUB_ENV"
```

The image-only GitHub environment contains `ModulePreview__Resolution`; it does not contain
`ModulePreview__PackageFeed` or a contract version variable.

The work directory must be empty. Authentication for Git, NuGet, and OCI registries is configured by
the workflow and never appears in the request, descriptor, policy, or resolution.

## Apply the trusted resolution

Apply the resolution before importing a module:

```csharp
using Aspire.Hosting.ModularAppHosts;
using ModuleC.Contract;

var builder = DistributedApplication.CreateBuilder(args);

var resolutionPath = builder.Configuration["ModulePreview:Resolution"]
    ?? throw new InvalidOperationException("ModulePreview:Resolution is required.");
await builder.ApplyModulePreviewResolutionAsync(resolutionPath);

await builder.ImportModuleCModuleAsync();
await builder.Build().RunAsync();
```

The resolution makes image overrides authoritative. Projects run in container mode, module-declared
image publishing is disabled, and Aspire receives the repository plus native `WithImageSHA256(...)`
pin. The protocol keeps the canonical `sha256:<hex>` form; the adapter passes only `<hex>` to Aspire,
which adds the algorithm prefix when it constructs the image reference. A module whose resources are
all satisfied by immutable container images and whose factories do
not require repository content can start without a Repo C runtime checkout.

The resolution also records the canonical request hash, Repo D repository/commit, selected module
commits, resolved contract package hashes and local paths, and image digests. Upload it with the E2E
diagnostics.

## Dispatch the trusted workflow

From Repo C, dispatch the workflow definition from Repo D's trusted default branch:

```bash
dotnet modular-apphosts preview trigger \
  --manifest module-preview.json \
  --repo acme/repo-d \
  --workflow module-preview-e2e.yml \
  --ref main \
  --wait \
  --github-output "$GITHUB_OUTPUT"
```

The default workflow input is `manifest_json`. Use `--input-name` for another declared input and
repeat `--input name=value` for consumer-specific metadata. `--ref` selects Repo D's workflow
definition; keep it fixed to a trusted branch. It is deliberately unrelated to Repo C's feature
branch.

On a successful dispatch, `trigger` prints the run identity as GitHub-style output:

```text
workflow_run_id=123456789
workflow_run_url=https://github.com/acme/repo-d/actions/runs/123456789
```

`--github-output <path>` appends the same two values to that file. A bare `--wait` watches the
returned run with `gh run watch --exit-status`, so the trigger process exits unsuccessfully when the
consumer workflow fails. Omit `--wait` when Repo C only needs to enqueue the run and retain its URL.

For local use, authenticate with `gh auth login`. A normal repository-scoped `GITHUB_TOKEN` in Repo C
cannot dispatch Repo D. For unattended dispatch, use a GitHub App installed only on the necessary
repositories with Actions write permission on Repo D and Contents read permission on selected
producer repositories.

## Legacy source-only export

`preview export --module <name> --output <path>` remains available for source-only requests that do
not use a producer descriptor, contracts, or images. `--dependency` is an alias for `--pin`. Apply
such a request with `ApplyModulePreviewManifestAsync`; it selects exact source commits but does not
cross the consumer policy/materialization boundary and cannot consume prebuilt artifacts.

## Complete fixture

The runnable fixture is split across:

- [`Shirubasoft/aspire-modular-apphosts-preview-producer`](https://github.com/Shirubasoft/aspire-modular-apphosts-preview-producer), which owns the contract, API image build, descriptor, and immutable digest; and
- [`Shirubasoft/aspire-modular-apphosts-preview-consumer`](https://github.com/Shirubasoft/aspire-modular-apphosts-preview-consumer), which owns the policy, trusted workflow, AppHost, and assertions.

The producer publishes its changed API container once. The consumer tool verifies and applies that
digest, waits for the API and preview-only sidecar with the Aspire CLI, asserts the feature response,
and uploads the trusted resolution and diagnostics.

## Failure modes

| Failure | Meaning | Resolution |
| --- | --- | --- |
| Dirty or unpushed producer worktree | CI cannot reproduce all runnable content | Commit and push the exact branch tip |
| Declared contract has no exact version | The request cannot identify a reproducible package | Commit `contract.version` or pass `--contract-version` |
| Required contract omitted | Repo D does not allow an image-only request for that module | Include the contract or review the policy and set contract `required` to `false` |
| Missing required image | The request could fall back to building source | Publish the image and pass its immutable digest |
| Unauthorized repository or resource | Repo C attempted to widen Repo D's trust boundary | Review and update Repo D's policy, not the request |
| Contract identity mismatch | The restored or packed nuspec differs from the request | Fix the published package or producer package ID/version |
| Published contract resolution fails | The exact version is absent, inaccessible, or NuGet authentication is missing | Publish or grant access to the exact version, then retry |
| Image inspect failure | The digest is absent, inaccessible, or authentication is missing | Publish/grant access, then retry the same digest |
| No contract materialization mode | Repo D has no approved way to obtain the requested contract | Configure a reviewed published source or enable a no-secrets fallback |
| AppHost requests a repository despite complete image pins | The contract has a repository-backed generic factory or an unpinned project | Pin every runtime image or retain the exact checkout |
| Dispatch denied | The caller cannot start Repo D's workflow | Authenticate with a narrowly scoped GitHub App or user token |
| Watched run fails | Repo D's workflow completed unsuccessfully | Follow `workflow_run_url` and inspect the consumer-owned diagnostics |

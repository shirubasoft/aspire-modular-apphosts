# Cross-repository E2E image workflows

This workflow lets a branch in Repo B build and push only the module images it owns, then run Repo
A's existing E2E AppHost against those images. Repo A receives a strict manifest and ordinary .NET
configuration; it does not clone Repo B or rebuild Repo B's resources.

The checked-in examples contain no custom orchestration scripts:

- [Repo A receiver](workflows/repo-a-e2e.yml) accepts both `workflow_call` and
  `workflow_dispatch`.
- [Repo B reusable call](workflows/repo-b-workflow-call.yml) hands the compact manifest directly to
  Repo A.
- [Repo B dispatch](workflows/repo-b-dispatch.yml) uses the tool to dispatch, wait, and propagate
  Repo A's status.

## Contract between the repos

Repo B's AppHost must expose pushable module images. Each selected project/container needs a
resolved registry, repository, tag, and build/push plan; see [module image publishing](modules.md).
Repo A must import the same module contract identities. A manifest entry is keyed by the declared
`module/resource`, never by Repo A's effective Aspire alias.

Pin the same release of the runtime package and tool in both repositories. In each repo:

```bash
dotnet new tool-manifest
dotnet tool install Shirubasoft.Aspire.ModularAppHosts.Tool --version <VERSION>
git add .config/dotnet-tools.json
```

The workflows restore that committed manifest with `dotnet tool restore`. Repo B also needs Aspire
CLI 13.4.6 or later. Dispatch additionally needs GitHub CLI 2.87.0 or later; GitHub-hosted runners
already include `gh`, but verify the version on self-hosted runners.

## Manifest contract

Repo B publishes a [versioned image manifest](module-image-manifest.schema.json). A complete example
is checked in at [examples/module-image-manifest.json](examples/module-image-manifest.json):

```json
{
  "schemaVersion": 1,
  "images": [
    {
      "module": "orders",
      "resource": "api",
      "resourceKind": "project",
      "registry": "ghcr.io",
      "repository": "your-org/orders-api",
      "tag": "0123456789abcdef",
      "digest": null
    }
  ]
}
```

Unknown properties, duplicate case-insensitive identities, incomplete registry references, invalid
tags/digests, and reserved configuration separators are rejected. Each image has exactly one tag
or SHA-256 digest. The manifest and the complete dispatch input payload are each limited to 65,535
characters.

`manifest publish` records the pushed tag. It does not query the registry for a post-push digest.
Use a unique immutable-by-convention tag such as the commit SHA, or supply a digest manifest from a
trusted system when registry-level immutability is required.

## Publish in Repo B

Authenticate the container runtime first, then publish an explicit selection:

```bash
dotnet tool run modular-apphosts -- manifest publish \
  --apphost src/RepoB.AppHost \
  --selector orders \
  --selector catalog/api \
  --tag "$GITHUB_SHA" \
  --resource-tags '{"orders/worker":"worker-candidate"}' \
  --output module-image-manifest.json
```

Selectors can be an unambiguous module/resource name, `module:<name>`, `resource:<name>`, or an
exact `<module>/<resource>` identity. Use `--all` only when every publishable AppHost image should
be pushed. The command runs Aspire's structured `describe-images` and `workflow-images` pipelines,
then builds the manifest from the resolved push targets.

On GitHub Actions it automatically emits step outputs:

| Output | Use |
| --- | --- |
| `manifest` | Compact JSON passed to a reusable workflow. |
| `manifest-path` | Saved file passed to `workflow dispatch`. |

## Apply in Repo A

Pass the manifest through an environment variable and run apply in its own step:

```bash
dotnet tool run modular-apphosts -- manifest apply \
  --json "$IMAGE_MANIFEST" \
  --tag "$REPO_A_IMAGE_TAG" \
  --resource-tags "$REPO_A_RESOURCE_TAGS"
```

`apply` uses GitHub Actions workflow commands to export the standard
`Aspire:ModularAppHosts:Modules` option hierarchy to `GITHUB_ENV`. Those values become available to
subsequent steps, so the E2E command must be a separate step. Projects run in container mode,
listed resources do not publish locally, and `ImagePullPolicy.Always` prevents a stale local tag.
Normal `IConfiguration` precedence still applies; later code configuration can intentionally
replace a workflow override.

The tag precedence is the same for both handoff styles:

| Order | Source | Scope |
| --- | --- | --- |
| 1 | AppHost-resolved image tag/digest | Each Repo B resource. |
| 2 | Repo B `manifest publish --tag` | Every selected resource. |
| 3 | Repo B `manifest publish --resource-tags` | Named Repo B resources. |
| 4 | Repo A `manifest apply --tag` | Every received resource. |
| 5 | Repo A `manifest apply --resource-tags` | Named Repo A resources. |

Repo A's tag options only select existing images. They do not create or retag registry content.

## Choose a handoff

| Handoff | Use it when | Behavior |
| --- | --- | --- |
| Reusable `workflow_call` | GitHub permits Repo B to call Repo A's workflow directly. | Repo A's workflow runs in the caller context; it must explicitly check out Repo A. |
| `workflow_dispatch` | Repo A should have a separate workflow run, permissions, and UI history. | The tool dispatches the exact run, streams it, and returns its status to Repo B. |

For a reusable call, copy [repo-b-workflow-call.yml](workflows/repo-b-workflow-call.yml) into Repo B
and [repo-a-e2e.yml](workflows/repo-a-e2e.yml) into Repo A. The `uses:` revision chooses the Repo A
workflow definition; the `repo-a-ref` input chooses the Repo A source revision checked out by it.

For a separate run, copy [repo-b-dispatch.yml](workflows/repo-b-dispatch.yml). Its orchestration is
one command:

```bash
dotnet tool run modular-apphosts -- workflow dispatch \
  --repository your-org/repo-a \
  --workflow external-e2e.yml \
  --ref main \
  --manifest module-image-manifest.json \
  --input repo-a-ref=main \
  --input image-tag="$REPO_A_IMAGE_TAG" \
  --input resource-tags="$REPO_A_RESOURCE_TAGS"
```

The tool sends JSON to `gh workflow run`, takes the exact returned run URL, and calls
`gh run watch --compact --exit-status`. It writes `run-id` and `run-url` as step outputs. The
calling job's `timeout-minutes` bounds the wait; cancellation reaches `gh` through the tool.

## Authentication and registry access

| Operation | Minimum workflow concern |
| --- | --- |
| Checkout Repo B | `contents: read`. |
| Push Repo B images | Registry write access; for GHCR this is commonly `packages: write`. |
| Dispatch Repo A | `GH_TOKEN` must be able to access Repo A and trigger Actions there. Repo B's built-in token normally cannot dispatch a different repository. |
| Watch Repo A | The same `gh` authentication must read the run and checks. Fine-grained PATs cannot currently grant the Checks permission required by `gh run watch`. |
| Pull in Repo A | Registry read access; for GHCR this is commonly `packages: read`. |
| Checkout private Repo A from a reusable call | A token that can read Repo A, passed as `REPO_A_CHECKOUT_TOKEN`. |

For a private GHCR package, grant Repo A Actions access in the package settings or supply registry
credentials that can read Repo B's package. Repository permissions alone do not automatically make
every separately scoped package readable.

Do not run the image-publishing job for an untrusted fork with privileged secrets. Fork pull
requests normally receive a read-only built-in token and no protected secrets. Use an explicit
trusted maintainer workflow or another isolated policy before building and executing fork-provided
container inputs.

Workflow values are assigned through `env` and passed as quoted command arguments in the examples;
they are not interpolated directly into shell programs. Tokens stay in `GH_TOKEN` or action inputs,
not command lines or manifests.

## Exit status and troubleshooting

Manifest commands return `0` on success, `1` for operational failures, `2` for invalid inputs, and
`130` when interrupted. Dispatch returns `gh run watch --exit-status` so Repo A failure makes Repo
B fail without a second status-mapping layer.

- **No run URL is returned:** upgrade `gh` to 2.87.0 or newer. Never guess by selecting the latest
  run; concurrent dispatches make that unsafe.
- **Apply has no effect:** ensure it runs before, not in the same step as, the E2E command.
- **A tag cannot be pulled:** ensure it was pushed and that Repo A's container runtime is logged in.
- **A resource is unknown:** use the declared `module/resource`, not an imported alias or effective
  Aspire name.
- **A private GHCR image returns unauthorized:** grant Repo A access in the package's Actions
  settings and confirm the login token can read the package.
- **`gh run watch` rejects authentication:** replace a fine-grained PAT with a compatible GitHub
  App installation token or classic token.
- **Dispatch exceeds the payload limit:** publish fewer resources or shorten repository/tag/input
  values; the tool validates the total JSON payload before calling GitHub.

## Adoption checklist

1. Pin matching runtime and tool versions in both repositories.
2. Confirm Repo B's AppHost publishes every selected resource to a registry Repo A can reach.
3. Copy the Repo A receiver and replace repository/test paths.
4. Choose and copy one Repo B handoff; replace `your-org/repo-a`, workflow name, refs, and AppHost
   path.
5. Configure registry write/read permissions and, for dispatch, `GH_TOKEN`.
6. Configure Repo A package access and private checkout secrets where needed.
7. Test from a trusted branch with a unique tag, then verify Repo B receives Repo A's failure code.

The [MultiRepoE2E sample](../samples/MultiRepoE2E/README.md) is the runnable local-registry version.
CI validates its real producer-to-consumer publish/apply handoff while tool tests validate dispatch
against deterministic `gh` process responses.

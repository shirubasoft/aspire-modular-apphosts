# Cross-repository E2E image workflows

The `modular-apphosts` .NET tool lets a module-producing repository publish selected images and
hand their fully qualified references to a separate repository's E2E AppHost. The receiving
workflow writes ordinary `Aspire:ModularAppHosts:Modules` configuration to `GITHUB_ENV`; the next
step starts the existing AppHost without changing its source or checking out the producer's build
repository.

Use [workflow dispatch](workflows/repo-b-dispatch.yml) when Repo B must start a separate Actions run
in Repo A. Use a [reusable workflow call](workflows/repo-b-workflow-call.yml) when both repositories
can participate in GitHub's reusable-workflow model. Both use the same
[Repo A workflow](workflows/repo-a-e2e.yml) and manifest contract.

## Install the tool

Install globally in CI:

```bash
dotnet tool install --global Shirubasoft.Aspire.ModularAppHosts.Tool
```

For a repository-pinned version, use a local tool manifest:

```bash
dotnet new tool-manifest
dotnet tool install Shirubasoft.Aspire.ModularAppHosts.Tool
dotnet tool run modular-apphosts --help
```

The publish command also requires the Aspire CLI on `PATH`. Pass `--aspire-path` when it is
installed elsewhere.

## Publish in Repo B

Registry login stays in the workflow. Use `docker/login-action` or the equivalent action for the
chosen registry, then publish an explicit module/resource selection:

```bash
modular-apphosts manifest publish \
  --apphost src/RepoB.AppHost \
  --selector orders \
  --selector catalog/api \
  --tag "pr-123" \
  --resource-tag "orders/worker=worker-pr-123" \
  --output module-image-manifest.json
```

Selectors match a declared module name, `module/resource`, or an unambiguous resource name. Use
`--all` only when every publishable image is intended. A global tag is applied first and each
`--resource-tag` wins for its resource.

The command asks Aspire for one structured image description, pushes the selected resources, and
builds the manifest from those same resolved push targets. It does not launch a second AppHost just
to rediscover the identities. In GitHub Actions, `manifest` and `manifest-path` are written as step
outputs automatically.

The compact manifest is versioned and strict. Each case-insensitive `module/resource` identity has
a resource kind, registry, repository, and exactly one tag or digest. The publish command emits
tags; it does not query a registry for immutable post-push digests. Use a unique tag such as the
commit SHA when the consumer must be isolated from later pushes. Applying a digest supplied by
another trusted system is also supported.

Duplicate identities, unknown selectors, malformed tags, incomplete references, conflicting
tag/digest values, unknown JSON properties, and payloads over 65,535 characters fail with exit code
2. An Aspire discovery or push failure returns 1, and interruption returns 130.

## Apply in Repo A

Pass either a file or inline JSON. The command writes standard options keys to `GITHUB_ENV`, so
only subsequent steps receive the configuration:

```bash
modular-apphosts manifest apply --json "$IMAGE_MANIFEST"
```

Repo A can select alternate tags for all manifest entries or individual resources:

```bash
modular-apphosts manifest apply \
  --file module-image-manifest.json \
  --tag validation \
  --resource-tag orders/api=validation-api
```

These options change the tag that Repo A pulls; they do not create or retag an image in the
registry. Every selected tag must already exist.

For each listed resource, the generated configuration selects container mode for projects,
disables local publishing, skips an unnecessary build-repository checkout, and uses
`ImagePullPolicy.Always`. Resources not listed keep their normal configuration. Because the tool
writes the existing `Modules` option hierarchy, normal .NET configuration and
`ConfigureModularAppHosts` precedence applies; a later code callback can intentionally replace a
workflow-provided value.

## Dispatch and wait with GitHub CLI

GitHub CLI 2.87 and later returns the created run URL from `gh workflow run`. The tool uses that
native output, sends the manifest as JSON over standard input, and waits with
`gh run watch --exit-status`:

```bash
modular-apphosts workflow dispatch \
  --repository your-org/repo-a \
  --workflow external-e2e.yml \
  --ref main \
  --manifest module-image-manifest.json \
  --input repo-a-ref=main
```

The [dispatch workflow](workflows/repo-b-dispatch.yml) contains no custom orchestration script. The
command writes `run-id` and `run-url` as step outputs, while the job's
`timeout-minutes` bounds the wait. It returns the status from `gh run watch --exit-status`, so Repo
B naturally propagates Repo A's conclusion.

## Authentication and private repositories

- Repo B's built-in `GITHUB_TOKEN` normally cannot dispatch a different repository. Supply a token
  that can access Repo A and run Actions as `GH_TOKEN`.
- `gh run watch` does not support fine-grained personal access tokens because those tokens cannot
  currently grant the Checks permission it uses. Use a supported GitHub App installation token or
  classic token for the dispatch example.
- Grant `packages: write` while Repo B pushes GHCR images and `packages: read` while Repo A pulls
  them. Configure registry credentials through `docker/login-action`.
- A called reusable workflow executes in the caller context. Its checkout would otherwise select
  Repo B, so the Repo A example explicitly supplies `repository`, `ref`, and an optional
  `REPO_A_CHECKOUT_TOKEN`. Private Repo A checkouts need a token that can read Repo A.
- Put the dispatch workflow on the target `--ref`. The workflow input name in the examples is
  `image-manifest`.

## Troubleshooting

- **No run URL is returned:** upgrade `gh` to 2.87 or newer. Do not guess the run by selecting the
  repository's latest run; concurrent dispatches make that unsafe.
- **Manifest apply has no effect:** run it in a separate step before the E2E command. GitHub does
  not expose newly appended `GITHUB_ENV` values to the step that wrote them.
- **An alternate tag cannot be pulled:** `manifest apply --tag` only selects a tag; it does not
  publish that tag.
- **A resource is unknown:** selectors and overrides use declared module/resource identities, not
  an import alias or prefixed effective Aspire resource name.
- **A private image cannot be pulled:** authenticate the same container runtime used by Aspire on
  the Repo A runner and grant it access to every registry/repository in the manifest.

The [MultiRepoE2E sample](../samples/MultiRepoE2E/README.md) is the runnable two-AppHost version. CI
publishes the producer image to a local registry, emits and applies the manifest through GitHub
files, makes the consumer's build repository deliberately unavailable, and runs its HTTP E2E test.

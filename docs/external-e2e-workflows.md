# Cross-repository E2E image workflows

The `modular-apphosts` .NET tool lets a module-producing repository publish selected images and
hand their exact identities to a separate repository's E2E AppHost. The receiving workflow writes
the manifest to `GITHUB_ENV`; the next step starts the existing AppHost without source changes,
rebuilding producer images, or checking out their repositories.

Use [workflow dispatch](workflows/repo-b-dispatch.yml) when Repo B must start a true Actions run in
Repo A and wait for its conclusion. Use a [reusable workflow call](workflows/repo-b-workflow-call.yml)
when both repositories can participate in GitHub's reusable-workflow model. Both use the same
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

The publish command also requires the Aspire CLI on `PATH`. Pass `--aspire-path` when it is installed
elsewhere. The dispatch command requires GitHub CLI 2.97 or newer and uses `GH_TOKEN` or the existing
`gh` authentication; it never reads, stores, or exchanges credentials itself.

## Publish in Repo B

Registry login stays in the workflow. Use `docker/login-action` (or the equivalent action for the
chosen registry), then publish an explicit module/resource selection:

```bash
modular-apphosts manifest publish \
  --apphost src/RepoB.AppHost \
  --selector orders \
  --selector catalog/api \
  --tag "pr-123" \
  --resource-tag "orders/worker=worker-pr-123" \
  --output module-image-manifest.json \
  --github-output manifest
```

Selectors match a declared module name, `module/resource`, or an unambiguous resource name. Use
`--all` only when every publishable image is intended. A global tag is applied first and each
`--resource-tag` wins for its resource. The command asks Aspire to describe the graph, pushes only
the selected images, then emits their structured remote identities. It does not reparse OCI image
references. `manifest` and `manifest-path` are written as step outputs when `--github-output` is
present.

The compact manifest is versioned and strict. Each case-insensitive `module/resource` identity has a
resource kind, registry, repository, and exactly one tag or digest. Duplicate identities, unknown
selectors, malformed tags, incomplete remote identities, conflicting tag/digest values, unknown
JSON properties, and payloads over 65,535 characters fail with exit code 2.

## Apply in Repo A

Pass either a file or inline JSON. The command writes standard .NET configuration keys to
`GITHUB_ENV`, so only subsequent steps receive the overrides:

```bash
modular-apphosts manifest apply --json "$IMAGE_MANIFEST"
```

Repo A can intentionally retag all producer identities or selected resources:

```bash
modular-apphosts manifest apply \
  --file module-image-manifest.json \
  --tag validation \
  --resource-tag orders/api=validation-api
```

For each listed resource, the AppHost forces container mode, disables local image publishing,
skips an otherwise unnecessary build-repository checkout, and pulls the supplied identity with
`ImagePullPolicy.Always`. Resources not listed in the manifest keep Repo A's normal configuration.
CLI tags win over manifest tags. The workflow override configuration is deliberately applied after
ordinary configuration and `ConfigureModularAppHosts` callbacks.

## Dispatch and wait

Repo B can create a Repo A run and return its outcome directly:

```bash
modular-apphosts workflow dispatch \
  --repository your-org/repo-a \
  --workflow external-e2e.yml \
  --ref main \
  --manifest module-image-manifest.json \
  --input repo-a-ref=main \
  --timeout 00:30:00
```

The command sends compact JSON to `gh workflow run --json`, obtains the created run URL/ID, streams
`gh run watch`, and queries the final conclusion. It writes `manifest`, `manifest-path`, `run-id`,
`run-url`, and `conclusion` to `GITHUB_OUTPUT` when that file is available. Timeout or interruption
best-effort cancels the external run.

| Exit code | Meaning |
| ---: | --- |
| 0 | Repo A concluded successfully. |
| 1 | Repo A completed with a non-success conclusion. |
| 2 | Local usage, manifest, or validation failure. |
| 3 | GitHub CLI/API operational failure. |
| 4 | GitHub authentication failure. |
| 124 | The requested timeout elapsed. |
| 130 | The local command was interrupted. |

## Authentication and private repositories

- Repo B's built-in `GITHUB_TOKEN` normally cannot dispatch a different repository. Supply a fine-
  grained token or GitHub App token with access to Repo A and permission to run Actions as
  `GH_TOKEN` (the examples use `REPO_A_ACTIONS_TOKEN`).
- Grant `packages: write` while Repo B pushes GHCR images and `packages: read` while Repo A pulls
  them. Configure the corresponding registry credentials through `docker/login-action`.
- A called reusable workflow executes in the caller context. Its checkout would otherwise select
  Repo B, so the Repo A example explicitly supplies `repository`, `ref`, and an optional
  `REPO_A_CHECKOUT_TOKEN`. Private Repo A checkouts need a token that can read Repo A.
- Put the dispatch workflow on the target `--ref`. The workflow input name defaults to
  `image-manifest`; change both sides with `--manifest-input` when necessary.

## Troubleshooting

- **No run URL is returned:** upgrade `gh` to 2.97 or newer. The tool does not guess by selecting a
  repository's latest run because concurrent dispatches make that unsafe.
- **Manifest apply has no effect:** it must be a separate step before the E2E command. GitHub does
  not expose newly appended `GITHUB_ENV` values to the step that wrote them.
- **A resource is unknown:** selectors and overrides use the declared module/resource identities,
  not an import alias or prefixed effective Aspire resource name.
- **A private image cannot be pulled:** authenticate the same container runtime used by Aspire on
  the Repo A runner and grant it access to every registry/repository in the manifest.
- **Dispatch returns 3:** run `gh workflow run` and `gh run view` with the same token/repository to
  distinguish workflow visibility, Actions permission, and API failures.

The [MultiRepoE2E sample](../samples/MultiRepoE2E/README.md) is the runnable two-AppHost version. CI
publishes the producer image to a local registry, emits and applies the manifest through GitHub
files, makes the consumer's source repository deliberately unavailable, and runs its HTTP E2E test.

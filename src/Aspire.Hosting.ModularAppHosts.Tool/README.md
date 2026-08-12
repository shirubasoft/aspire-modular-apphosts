# Shirubasoft.Aspire.ModularAppHosts.Tool

`modular-apphosts` publishes module images from one repository, transfers their exact registry
identities in a versioned module image workflow document, applies those identities to another AppHost, and can dispatch
and wait for the receiving repository's GitHub Actions workflow. Workflow YAML needs no custom
orchestration script.

The module image workflow document is the tool's cross-repository contract. It is distinct from Aspire's
application manifest and includes module/source identities that the standard manifest does not model.

## Requirements

- .NET 10 SDK to install the tool.
- Aspire CLI 13.4.6 or later for `images publish`.
- GitHub CLI 2.87.0 or later for `workflow dispatch`.
- A container registry login that is already available to the selected container runtime.
- The same released version of this tool and `Shirubasoft.Aspire.ModularAppHosts` in both repos.

Pin the tool in each repository instead of installing an unversioned global copy:

```bash
dotnet new tool-manifest
dotnet tool install Shirubasoft.Aspire.ModularAppHosts.Tool --version <VERSION>
git add .config/dotnet-tools.json
```

CI can then run `dotnet tool restore` and invoke commands with
`dotnet tool run modular-apphosts -- ...`.

## Repo B: publish images

```bash
dotnet tool run modular-apphosts -- images publish \
  --apphost src/RepoB.AppHost \
  --module orders \
  --resource api \
  --tag "$GITHUB_SHA" \
  --resource-tags '{"orders/worker":"worker-candidate"}' \
  --output module-image-workflow.json
```

Exactly one selection mode is required:

- Repeat `--module` and `--resource` for explicit module and declared/effective resource names.
- Use `--all` to publish every image exposed by the AppHost.

`--tag` applies first. Values in the `--resource-tags` JSON object win for their declared
`module/resource`. The command runs Aspire's `describe-images` and `workflow-images` pipelines; use
`--aspire-path` only when the Aspire executable is not named `aspire`.

The module image workflow document is saved to `--output`, or `module-image-workflow.json` by default. Inside GitHub
Actions, the command automatically writes these step outputs when `GITHUB_OUTPUT` is configured:

| Output | Value |
| --- | --- |
| `workflow-document` | Compact document JSON for a reusable workflow input. |
| `workflow-document-path` | Absolute path to the saved document for dispatch. |

## Repo A: apply images

Run the AppHost or E2E command through `apply`. The invocation is identical locally and in CI:

```bash
dotnet tool run modular-apphosts -- images apply \
  --json "$IMAGE_WORKFLOW" \
  --tag "$REPO_A_IMAGE_TAG" \
  --resource-tags "$REPO_A_RESOURCE_TAGS" \
  -- \
  dotnet test tests/RepoA.E2E.Tests/RepoA.E2E.Tests.csproj --configuration Release
```

Specify exactly one of `--json` or `--file`, then place the command and its arguments after `--`.
`apply` launches that command directly, without a shell, and gives it ordinary
`Aspire:ModularAppHosts:Modules:<module>:<collection>:<resource>` configuration. The caller's
environment and working directory are inherited, while the parent shell remains unchanged. Standard
input and output are streamed, and the child command's exit code becomes the tool's exit code.

Repo A's `--tag` overrides every received tag or digest. Its `--resource-tags` JSON object wins
last. These options select existing registry tags; they do not create or retag images.

For listed projects, apply selects container mode. For every listed resource it disables local
publishing, clears the conflicting tag or digest field, and selects `ImagePullPolicy.Always`.

## Repo B: dispatch and wait

```bash
dotnet tool run modular-apphosts -- workflow dispatch \
  --repository your-org/repo-a \
  --workflow external-e2e.yml \
  --ref main \
  --workflow-document module-image-workflow.json \
  --input repo-a-ref=main \
  --input image-tag="$REPO_A_IMAGE_TAG" \
  --input resource-tags="$REPO_A_RESOURCE_TAGS"
```

The command validates the module image workflow document and the complete GitHub input payload, sends the inputs to
`gh workflow run --json` over standard input, extracts the exact created run ID from the returned
URL, and streams `gh run watch --compact --exit-status`. Authentication belongs to GitHub CLI; set
`GH_TOKEN` or use an existing `gh auth` session.

Options:

| Option | Meaning |
| --- | --- |
| `--repository` | Required `[HOST/]OWNER/REPO` target. |
| `--workflow` | Required workflow file name, ID, or workflow name. |
| `--workflow-document` | Required module image workflow document file. |
| `--ref` | Branch or tag containing the target workflow; defaults to the target's default branch. |
| `--workflow-document-input` | Workflow input receiving the document; defaults to `image-workflow`. |
| `--input` | Additional `<name>=<value>` input; repeat as needed. |
| `--gh-path` | GitHub CLI executable; defaults to configured `GitHubCliPath`, then `gh`. |

When `GITHUB_OUTPUT` is configured, dispatch emits `run-id` and `run-url`. Its final exit code is
the exit code from `gh run watch --exit-status`, so the calling job represents the external run's
outcome without polling or selecting a potentially unrelated latest run.

## Module image workflow document contract

The document is strict JSON. Unknown fields are rejected, identities are case-insensitively unique,
and each image contains exactly one tag or supported SHA-256 digest:

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

The total compact workflow document and the complete dispatch input payload are each limited to 65,535
characters. Module/resource names cannot contain identity or configuration separators.

## Configuration and exit codes

The tool reads runner configuration through .NET `IConfiguration`. Set
`Aspire:ModularAppHosts:GitHubCliPath` (environment form
`Aspire__ModularAppHosts__GitHubCliPath`) to choose a default GitHub CLI executable.

| Code | Meaning |
| --- | --- |
| `0` | Command or watched external workflow succeeded. |
| `1` | Aspire, GitHub CLI, registry, file, or watched workflow failure. |
| `2` | Invalid command input, workflow document, selection, tag map, or payload. |
| `130` | The tool was interrupted. |

The command launched by `images apply` and GitHub CLI may define additional exit statuses that the
tool returns unchanged.

## Security notes

- Do not push images from untrusted fork pull requests with a privileged registry token.
- Repo A needs package read access to every image in the workflow document; Repo B needs write access.
- A repository's built-in `GITHUB_TOKEN` normally cannot dispatch another repository. Provide a
  suitable token through `GH_TOKEN` without placing it in command arguments.
- `gh run watch` cannot use fine-grained personal access tokens because Checks read permission is
  unavailable to them. Use a compatible GitHub App or classic token.
- Treat manifests as deployment inputs. The tool validates their shape, not whether a caller is
  authorized to make Repo A execute an image.

See the repository's [cross-repository workflow guide](https://github.com/Shirubasoft/aspire-modular-apphosts/blob/main/docs/external-e2e-workflows.md)
and [checked-in workflow examples](https://github.com/Shirubasoft/aspire-modular-apphosts/tree/main/docs/workflows).

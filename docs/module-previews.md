# Cross-repository module previews

Module previews let a developer working in a producer repository trigger an E2E workflow in a
consumer repository against the exact source they have pushed. The producer branch is retained as
display metadata, while the immutable Git commit is the materialization identity.

This workflow has three parts:

1. `dotnet modular-apphosts preview export` validates the producer checkout and writes a versioned
   JSON manifest.
2. `dotnet modular-apphosts preview trigger` dispatches a trusted workflow from the consumer's
   default branch and passes that manifest as a typed workflow input.
3. The consumer validates the allowlisted repositories, prepares any preview contract packages or
   images, applies the manifest before importing modules, and records the fully resolved inputs with
   its test results.

The manifest never contains credentials. A schema-version-1 manifest also cannot represent a dirty
worktree: export fails until every change is committed and the current commit is present on the
configured `origin` branch.

## Install the tool

Install the tool into a repository-local tool manifest so every contributor and CI runner uses the
same version:

```bash
dotnet new tool-manifest # only when .config/dotnet-tools.json does not exist
dotnet tool install Shirubasoft.Aspire.ModularAppHosts.Tool
dotnet tool restore
```

The installed command is `dotnet modular-apphosts`.

## Export a producer preview

From the root of repository C:

```bash
dotnet modular-apphosts preview export \
  --module module-c \
  --output module-preview.json
```

Export verifies all of the following before writing the file:

- the current directory belongs to a Git repository;
- `HEAD` is attached to a branch;
- the worktree and index are clean;
- `origin` identifies GitHub or a credential-free absolute remote repository URL;
- the current branch exists on `origin` at exactly the local `HEAD` commit; and
- the default branch and its base commit can be resolved.

The output may be inside the producer repository. The exporter excludes that one untracked output
file from its cleanliness check, so the same command can refresh it. A tracked output is rejected;
add the manifest path to `.gitignore` if you want the rest of the worktree to remain clean.

Add a changed dependency explicitly rather than assuming that the same branch exists in another
repository:

```bash
dotnet modular-apphosts preview export \
  --module module-c \
  --pin module-a=https://github.com/acme/repo-a.git@0123456789abcdef0123456789abcdef01234567 \
  --output module-preview.json
```

`--dependency` is an alias for `--pin`. Every pin uses a full 40- or 64-character commit ID.

The resulting file has this shape:

```json
{
  "schemaVersion": 1,
  "producer": {
    "repository": "https://github.com/acme/repo-c.git",
    "commit": "89abcdef0123456789abcdef0123456789abcdef",
    "branch": "feat/some-work",
    "baseRef": "refs/heads/main",
    "baseCommit": "0123456789abcdef0123456789abcdef01234567",
    "dirty": false
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
  ]
}
```

`branch`, `baseRef`, and `baseCommit` are audit metadata. Consumers must always checkout and import
`commit`.

## Apply a preview in an AppHost

The consumer AppHost applies the manifest before it imports any selected module:

```csharp
using Aspire.Hosting.ModularAppHosts;
using ModuleC.Contract;

var builder = DistributedApplication.CreateBuilder(args);

var previewManifest = builder.Configuration["ModulePreview:Manifest"];
if (!string.IsNullOrWhiteSpace(previewManifest))
{
    await builder.ApplyModulePreviewManifestAsync(previewManifest);
}

var moduleC = await ModuleCModule.ImportModuleAsync(builder);

await builder.Build().RunAsync();
```

The path is resolved relative to the AppHost directory. Applying a manifest validates its schema,
rejects duplicate module selections, rejects dirty producers and invalid commit IDs, and configures
the selected modules' `Repository` and `RepositoryRevision` values programmatically. Explicit
preview selections therefore take precedence over the consumer's normal branch defaults.

An already loaded `ModulePreviewManifest` can be applied with
`builder.ApplyModulePreviewManifest(manifest)`.

## Handle producer-owned contract changes

The managed Git checkout supplies source and build context; it does not load executable module
definitions. The consumer still compiles against the producer-owned module contract package.

When a branch only changes an implementation, applying its source commit is sufficient. When it
changes resource names, resource types, endpoints, required configuration, or materialization
semantics, the consumer workflow must also build or download the contract produced by that same
commit before restoring the AppHost.

A safe consumer workflow performs these operations in order:

1. Validate the manifest without executing producer code.
2. Allowlist every repository and module name.
3. Checkout each producer at its exact commit with persisted credentials disabled.
4. Pack the producer-owned contract at a unique immutable preview version.
5. Restore the AppHost against that exact package and an isolated package source.
6. Apply the manifest and start the AppHost.
7. Record the consumer commit, producer commits, contract package versions and package hashes in a
   resolved manifest artifact.

For a Compose workflow, use the same rule for runtime artifacts: build the images from the selected
commit and record immutable image digests. A human-readable branch tag is not an artifact identity.

## Dispatch the consumer workflow

From repository C, dispatch repository D's trusted workflow:

```bash
dotnet modular-apphosts preview trigger \
  --manifest module-preview.json \
  --repo acme/repo-d \
  --workflow module-preview-e2e.yml \
  --ref main
```

The default workflow input is named `manifest_json`; select another declared input with
`--input-name`. Pass consumer-specific inputs such as a correlation ID with repeated
`--input name=value` options. The tool uses the authenticated GitHub CLI, submits structured JSON
without shell interpolation, and prints the created workflow run URL.

The `--ref` value selects the workflow definition in repository D. Keep it fixed to a trusted
default branch; it is deliberately unrelated to repository C's feature branch.

For local use, authenticate with `gh auth login`. A workflow in repository C cannot use its normal
`GITHUB_TOKEN` to dispatch repository D because that token is repository-scoped. For unattended
cross-repository dispatch, use a GitHub App installed only on the required repositories with:

- Actions write permission on repository D;
- Contents read permission on the producer repositories; and
- Commit statuses or Checks write permission on repository C when D reports a result back.

Do not provide deployment or production secrets to the E2E job. It builds and executes code from a
feature branch. Use a separate trusted reporting job for callbacks, and pass branch names only as
data rather than interpolating them into shell scripts.

## Consumer workflow outline

The complete runnable fixture is split across:

- [`Shirubasoft/aspire-modular-apphosts-preview-producer`](https://github.com/Shirubasoft/aspire-modular-apphosts-preview-producer), which contains the source project and producer-owned contract; and
- [`Shirubasoft/aspire-modular-apphosts-preview-consumer`](https://github.com/Shirubasoft/aspire-modular-apphosts-preview-consumer), which owns the trusted `workflow_dispatch` E2E.

The producer's preview branch changes both the API behavior and the resource graph. The consumer
checks out that exact commit, packs its contract, starts the AppHost with `aspire start`, waits for
the API and preview-only sidecar with `aspire wait`, and verifies the branch-specific response. This
ensures the sample covers source selection and contract evolution instead of testing only a branch
name handoff.

## Failure modes

| Failure | Meaning | Resolution |
| --- | --- | --- |
| Dirty worktree | The manifest cannot identify all runnable content | Commit the changes; snapshot transport is intentionally outside schema version 1 |
| Branch differs from `origin` | Remote CI cannot fetch the selected `HEAD` | Push the current commit, then export again |
| Contract version mismatch | Source changed the resource graph but D restored an older contract | Pack and pin the contract from the selected producer commit |
| Conflicting module pin | Two preview selections request different commits for one logical module | Resolve the dependency explicitly; never infer it from branch names |
| Unauthorized repository | The producer attempted to widen D's source trust boundary | Add a reviewed allowlist entry in D, not in the producer manifest |
| Dispatch denied | The current token cannot start the workflow in D | Authenticate with an appropriately scoped GitHub App or user token |

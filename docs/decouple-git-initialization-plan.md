# Decouple Git operations from AppHost initialization

The refactor should make AppHost model construction synchronous and side-effect free. Git mutation belongs to `aspire do initialize`; image acquisition and building belong to run-time installer resources.

```text
Synchronous module declaration
├── aspire do initialize
│   └── clone / validate origin / fetch / fast-forward / checkout revision
└── aspire run
    ├── fast filesystem + receipt preflight
    ├── image installer: reuse / pull / build / retag
    └── start application resources
```

## Assumed policies

- Pinned revisions use collision-resistant, revision-specific sibling directories beside the AppHost Git root. This supports multiple revisions without detaching a developer checkout.
- Runtime Git mutation and network access are off by default. Installers may use read-only Git inspection to identify source state. An explicit option may fetch and fast-forward clean build checkouts; dirty checkouts are preserved and rebuilt.
- Repository-backed factories must be declarative: they may compose repository paths but must not read repository content while constructing the model.

## 1. Introduce pure repository and image plans

Extract the planning currently mixed into `DistributedApplicationModuleExtensions`.

Add internal models such as:

- `ModuleRepositoryRequirement`
- `ModuleRepositoryPlanRegistry`
- `ModuleImageBuildRecipe`
- `ModuleInitializationReceipt`

The repository registry should:

- Find the AppHost Git root by walking parents for a `.git` file or directory, without invoking Git.
- Resolve every remote repository to a direct child of the Git-root parent.
- Use collision-resistant names based on normalized remote identity.
- Give pinned revisions distinct sibling paths.
- Deduplicate shared repositories and reject incompatible policies.
- Exclude same-worktree, unpinned explicit existing local paths, and external-image-only modules. A local repository paired with a revision is a clone source for an initializer-owned revision sibling.

Store per-repository receipts under the AppHost's `.aspire/modular-apphosts/` directory. A receipt records the normalized remote, destination, requested revision, and configuration fingerprint, but no credentials.

Remove the two-layout model:

- Remove `RepositoryBasePath` and the legacy base-location parameter.
- Remove all `AutoClone*` options; `initialize` becomes the only automatic acquisition mechanism.
- Replace ambiguous update flags with `UpdateRepositoriesOnInitialize` / `UpdateRepositoryOnInitialize` and the separate `RefreshBuildRepositoriesOnRun` / `RefreshBuildRepositoryOnRun` image-build refresh policy.

## 2. Add the `initialize` pipeline

Create an aggregate `initialize` pipeline step and one `initialize-<repository>` step per repository requirement, following the existing image pipeline conventions.

Each repository step asynchronously:

1. Validates the target path and existing origin.
2. Clones missing repositories.
3. Preserves dirty worktrees.
4. Fast-forwards clean unpinned branches when initialization updates are enabled.
5. Fetches and checks out pinned revisions only in initializer-owned revision siblings.
6. Updates submodules.
7. Writes the receipt atomically after success.

Independent repositories can initialize concurrently. Shared repositories execute once. Repeated initialization must be idempotent.

During `aspire do initialize`, skip normal-run repository preflight and missing-project validation so the pipeline can be constructed before any checkout exists.

Reuse the existing authentication and async process execution, but move all calls behind pipeline actions.

## 3. Make module declaration synchronous

Replace the raw API:

```csharp
builder.DefineModule(...);
builder.ExportModule(...);
builder.AddModule(module);
builder.ImportModule("orders", options);
```

Generated APIs become:

```csharp
var catalog = builder.AddCatalogModule();
var orders = builder.ImportOrdersModule();
```

Update the source generator to:

- Remove `Task`, `Async`, cancellation tokens, and `await`.
- Reserve the synchronous generated names.
- Recognize `DefineModule` and `ExportModule` lambdas.
- Return generated module wrappers directly.

Delete the async module-operation semaphore rather than synchronously waiting on it. AppHost resource declarations are expected to be single-threaded.

Normal run preflight should synchronously aggregate:

- Missing sibling directories.
- Missing `.git` markers.
- Missing or stale initialization receipts.
- Missing declared project files and build directories.

Return one actionable exception listing all affected modules and paths and ending with:

```text
Run 'aspire do initialize --non-interactive'.
```

Except for explicitly enabled installer refresh, `aspire run` must not perform Git mutation or network access; read-only Git inspection is allowed.

## 4. Move image decisions into installer execution

Replace the model-time image plan with a recipe annotation that is evaluated when the installer or pipeline action runs.

Use a deterministic local run alias that does not depend on branch, commit, or dirty state. At execution time, the installer:

1. Resolves Docker or Podman.
2. Optionally fast-forwards a clean build checkout.
3. Computes branch, commit, and dirty state.
4. Reuses an existing clean canonical image when available.
5. Optionally pulls the canonical image.
6. Falls back to the declared build command.
7. Always rebuilds dirty source.
8. Retags the result to the stable local run alias.
9. Rechecks source state after building and fails clearly if files changed during the build.

Ship the conditional installer orchestration as a package-carried managed command, with no shell wrapper or separately installed global tool. Pipeline build, push, and describe actions should call the same underlying evaluator directly.

External image overrides must remain checkout-free.

## 5. Improve logging

Give initialization and image preparation stable structured events and operation scopes containing module, resource, repository kind, path, image reference, and operation ID.

Important events include:

- Repository clone, fetch, fast-forward, and revision checkout started and completed.
- Update skipped with reason: dirty, disabled, or no upstream.
- Preflight failure with the exact initialization command.
- Image found locally, pull attempted or completed, and build fallback reason.
- Dirty-source rebuild.
- Build and retag completion with elapsed time.

Lifecycle events should go to both the pipeline and resource logger. Raw command output should appear only under the relevant resource.

Redact URI userinfo, query strings, credential-helper arguments, and arbitrary environment values. Treat ordinary tool stderr as informational unless the command fails.

## 6. Tests, sample, and CI

Extend `samples/MultiRepoE2E`; it already covers independent and pinned build repositories. Replace the large inline CI orchestration with a .NET E2E driver so CI invokes one tool-backed command.

The E2E scenario should verify:

1. Run fails fast before initialization.
2. `aspire do initialize --non-interactive` creates the expected siblings.
3. Initialization is idempotent.
4. Normal run performs no Git mutation or network access by default; read-only source inspection is allowed.
5. A clean upstream change is picked up by another initialization.
6. A dirty checkout is preserved and rebuilt.
7. Optional runtime refresh fast-forwards only clean checkouts.
8. Pinned revisions do not move developer checkouts.
9. Logs are structured and credentials are redacted.
10. Docker or Podman is selected through the existing resolver.

Unit tests should cover repository-plan deduplication, collisions, receipts, preflight aggregation, synchronous generated APIs, installer decision tables, and the external-image checkout-free path.

## Breaking-change migration

```diff
-var definition = await builder.ExportModuleAsync(...);
-await builder.AddAsync(definition);
-var orders = await builder.ImportOrdersModuleAsync();
+var definition = builder.ExportModule(...);
+builder.AddModule(definition);
+var orders = builder.ImportOrdersModule();
```

The core commit must be marked, for example:

```text
feat!: decouple repository initialization from AppHost declaration

BREAKING CHANGE: Module declaration APIs are synchronous and repository
acquisition now requires `aspire do initialize`. Replace the Async module APIs
with DefineModule, ExportModule, AddModule, ImportModule, and the generated
Add*Module/Import*Module methods.
```

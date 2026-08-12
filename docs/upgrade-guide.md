# Upgrade to synchronous Aspire-native modules

This release intentionally removes compatibility shims and moves module contracts onto Aspire's
native extension namespace and lifecycle. Update the package and tool together, then make the
following changes in every module contract and AppHost.

## Use the Aspire namespace

Replace `using Aspire.Hosting.ModularAppHosts;` with `using Aspire.Hosting;`. Public module,
configuration, image workflow, and generator attribute types now extend Aspire's existing namespace.
Application-model resource types remain in `Aspire.Hosting.ApplicationModel`.

In projects that reference `Shirubasoft.Aspire.ModularAppHosts.Testing`, replace the same old namespace
with `using Aspire.Hosting.Testing;` for `DockerComposeDeploymentTestingBuilder`,
`DockerComposeDeploymentOptions`, and the Compose test configuration extensions.

## Construct the application model synchronously

Module declaration no longer performs Git or image I/O, so its public APIs are synchronous:

| Previous API | Replacement |
| --- | --- |
| `DefineModuleAsync(...)` | `DefineModule(...)` |
| `ExportModuleAsync(...)` | `ExportModule(...)` |
| `ImportModuleAsync(...)` | `ImportModule(...)` |
| `AddAsync(module)` | `AddModule(module)` |
| generated `Add...ModuleAsync()` | generated `Add...Module()` |
| generated `Import...ModuleAsync()` | generated `Import...Module()` |

Remove `await` from those calls. Keep `await builder.Build().RunAsync()` for the Aspire application
lifecycle.

Call `ConfigureModularAppHosts(...)` before the first `DefineModule(...)`, `ExportModule(...)`,
`AddModule(...)`, or `ImportModule(...)` call. Model-shaping options are now captured once as an
immutable snapshot while the AppHost resource graph is constructed; later configuration calls throw
instead of rebinding and mutating already-materialized resources.

## Initialize repositories explicitly

Automatic cloning and updating during AppHost construction has been removed. Delete
`AutoCloneRepositories`, `AutoCloneRepository`, `AutoCloneBuildRepository`,
`UpdateImportedRepositories`, `UpdateRepository`, and `UpdateBuildRepository` configuration.
`RepositoryBasePath`, `RepositoryBaseLocationParameterName`, and `GetRepositoryParameterName(...)`
are also removed. Managed checkouts now use collision-resistant direct siblings of the AppHost Git
root. Configure repositories through `IConfiguration` with
`GetRepositoryConfigurationKey(moduleName)` or through `WithRepository(...)`.

Use `UpdateRepositoriesOnInitialize` or `UpdateRepositoryOnInitialize` to control clean, unpinned
fast-forwards during initialization. From the AppHost directory, acquire required managed checkouts
with:

```bash
aspire do initialize --apphost . --non-interactive
```

Normal `aspire run` validates the checkout and its credential-free initialization state without
mutating Git. If validation fails, copy the exact AppHost-aware recovery command from the error.
Initialization state now lives in per-repository sections of Aspire's deployment-state file under
`~/.aspire/deployments`; delete the old `.aspire/modular-apphosts/repositories` receipt directory
after upgrading. Git is advertised as a required command on repository-backed resources, and `gh`
is advertised only for GitHub HTTPS repositories that select it as the credential provider.
`RefreshBuildRepositoriesOnRun` and `RefreshBuildRepositoryOnRun` are explicit opt-ins for
fast-forwarding clean, unpinned image build repositories immediately before a resource starts.

The library no longer rewrites command output, exception messages, repository origins, proxy logs,
or saved diagnostics. Configure secret masking in the local runner or CI provider and avoid passing
credentials in command-line arguments when an environment variable or credential provider is
available.

## Update image publishing

- Replace `ModuleContainerExportOptions` with `ModuleImageCommandOptions`. A project that uses
  Aspire's standard publisher should replace the old command-oriented `ExportAsContainer(...)`
  call with `ExportAsContainer(imageName)`; keep arbitrary commands only through
  `ExportAsContainerWithCommand(new ModuleImageCommandOptions(...))`.
- Delete the global/module `PublishImages` options and calls to `BuildModuleImages()`. Declare
  native project publishers with `ExportAsContainer(imageName)`, advanced project commands with
  `ExportAsContainerWithCommand(...)`, containers with `WithImagePublishCommand(...)`, or
  the image-publishing `AddResource(...)` overload. Set `PublishImage: false` only for a complete
  external-image override.
- Delete uses of the removed `ContainerRuntimeResolver`. Put
  `ModuleImageCommandOptions.ContainerRuntimePlaceholder` in the publish command or arguments;
  it resolves through Aspire's `IContainerRuntimeResolver` when the command runs.
- Replace command-backed project exports with `ExportAsContainer(imageName)` where Aspire's project
  publisher is sufficient. Factory-created Dockerfile containers can return `AddDockerfile(...)`;
  they retain Aspire's standard build and push steps and participate in module image workflow documents.
- Keep registry and repository separate: set `ImageRegistry` to a host such as `ghcr.io` and
  `ImageName` to a repository such as `example/orders-api`.
- Use the `{image}`, `{image-registry}`, `{image-repository}`, `{image-name}`, and `{image-tag}`
  placeholders instead of embedding a declaration-time image reference into a command.
- Replace annotation reads of `ProjectName` with `ResourceName`.

Run-mode image preparation now belongs to the target container's Aspire start lifecycle. Explicit-start
containers remain lazy, and `describe-images` resolves metadata without pulling, building, or tagging.
Image operations no longer share the two-minute repository timeout: configure `ImageBuildTimeout`
for publisher commands and `ImageTransferTimeout` for pulls, pushes, and tags. `RepositoryCommandTimeout`
now applies only to Git inspection and synchronization.

## Update pipeline selection

Aspire's standard `build`, `push`, and `pull` aggregates no longer accept module-specific positional
selectors. Run an individual Aspire step by name when needed:

```bash
aspire do build-orders-api
aspire do push-orders-api
aspire do pull-orders-api
```

For validated module/resource selection across several images, use the tool-owned interface:

```bash
dotnet tool run modular-apphosts -- images publish \
  --apphost src/RepoB.AppHost \
  --module orders \
  --resource api \
  --tag "$GITHUB_SHA"
```

The resulting module image workflow document is a cross-repository contract, not an Aspire application
manifest. A scoped workflow publish no longer changes which resources appear in a later Aspire
application manifest.

Replace `ModuleImageManifestDocument` and `ModuleImageManifestEntry` with
`ModuleImageWorkflowDocument` and `ModuleImageWorkflowEntry`. Rename tool invocations from
`modular-apphosts manifest publish/apply` to `modular-apphosts images publish/apply`; the default
file is now `module-image-workflow.json`. Replace `--selector` with repeatable `--module` and
`--resource` options. Workflow dispatch now accepts `--workflow-document` and
`--workflow-document-input` (default input: `image-workflow`).

## Validate the migration

1. Run `dotnet build` to find stale namespaces, async calls, and removed options.
2. Run `aspire do initialize --apphost . --non-interactive` for every repository-backed AppHost.
3. Run each sample with `aspire run` and confirm explicit-start containers do not build eagerly.
4. Run `aspire do describe-images --output-path artifacts` and confirm it performs no runtime image
   operations.
5. Exercise build, push, pull, and workflow image publication with the Docker or Podman runtime
   selected by Aspire.

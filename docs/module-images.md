# Module image workflows

This advanced guide covers module-owned image preparation and publication. Start with the
[module guide](modules.md) for contract definition, imports, generated resources, and configuration.

## Declare module image publishers

Prefer Aspire-native publishers. `ExportAsContainer(imageName)` delegates project image build and push
to Aspire, while a module factory can return `AddDockerfile(...)` to retain Aspire's Dockerfile
annotations and standard pipeline steps:

```csharp
module.AddProject("orders-api", projectPath)
    .ExportAsContainer("example/orders-api");

module.AddResource<ContainerResource>("orders-worker", context =>
    context.ApplicationBuilder
        .AddDockerfile(context.ResourceName, Path.Combine(context.RepositoryPath, "src/Worker"))
        .WithContainerRegistry(registry)
        .WithRemoteImageName("orders-worker"));
```

Use the advanced escape hatch only when an image requires an arbitrary command.
`ExportAsContainerWithCommand` configures a project command, `WithImagePublishCommand` configures an
`AddContainer` resource, and the command-oriented `AddResource` overload configures a factory-created
container. Use the runtime placeholder so the command follows Aspire's Docker or Podman selection:

```csharp
module.AddContainer("orders-static", "orders-static")
    .WithImagePublishCommand(new ModuleImageCommandOptions(
        imageName: "orders-static",
        publishCommand: ModuleImageCommandOptions.ContainerRuntimePlaceholder,
        publishArguments:
        [
            "build",
            "--tag",
            ModuleImageCommandOptions.ImageReferencePlaceholder,
            "."
        ]));
```

The placeholder resolves through Aspire's `IContainerRuntimeResolver` only when the image command runs, so module declaration remains synchronous and uses the same configured Docker or Podman runtime as the rest of the AppHost.

In run mode, an Aspire `OnBeforeResourceStarted` callback invokes an advanced publisher immediately
before its actual container starts when preparation is needed:

- When `ImageTag` is omitted, the module repository branch is lowercased and sanitized and its 12-character commit is appended (`feature/orders` becomes `feature-orders-a1b2c3d4e5f6`). Detached checkouts use `sha-a1b2c3d4e5f6`. The current checkout state is inspected without moving it; only an explicit run-time refresh may first fast-forward a clean, unpinned build repository. For repository-independent or still-unresolved definitions, the AppHost branch and commit are used, then CI branch variables, then `latest`.
- A clean repository uses `[ImageRegistry/]ImageName:ImageTag` and reuses that image when it already exists locally.
- With `PullBeforeBuild = true`, a missing clean image is pulled from its registry before the build command is considered. A successful pull skips the build; a missing or failed pull falls back to the declared command. Dirty repositories always build and never use this pull shortcut. When an explicit tag and a separate `BuildRepository` are configured but its checkout is absent, the tagged image is inspected and pulled first, so a successful acquisition avoids the checkout entirely. Existing local or managed build checkouts are resolved first so uncommitted source changes still produce a dirty image. If neither the local runtime nor the registry can supply the image, preparation reports the build-checkout recovery action instead of starting the build command in a missing directory.
- A dirty repository uses `[ImageRegistry/]ImageName:ImageTag-dirty` and rebuilds it for every AppHost session. The tag remains within the 128-character distribution limit.
- The resource starts only after image preparation succeeds.
- Explicit-start resources remain lazy; starting one prepares only that resource's image.

Dirty images can run locally but cannot be pushed. Aspire-native project and Dockerfile publishers
also run a clean-source validation step before their standard push step. This prevents a shared tag
from representing uncommitted source while leaving native local build and run behavior available.

`ImageRegistry` explicitly separates a registry host such as `ghcr.io` from an `ImageName` repository path such as `example/orders-api`. Leave it unset for local or otherwise unqualified images. Publish arguments can use the `{image}`, `{image-registry}`, `{image-repository}`, `{image-name}`, and `{image-tag}` constants on `ModuleImageCommandOptions`. The effective image reference is also available to the command as `ASPIRE_MODULE_IMAGE`.

### Push module images

In publish mode, every advanced command publisher contributes a `build-<resource>` step to Aspire's
`build` pipeline. The step executes its effective `ModuleImageCommandOptions` command and arguments in
the resolved build working directory with `ASPIRE_MODULE_IMAGE` set. `PullBeforeBuild` first attempts
to reuse or pull a clean image, dirty repositories always build, and `ProducedImageReference` is
retagged after a successful command. Native project and Dockerfile publishers retain Aspire's own
build steps. Failures and cancellation stop the pipeline.

Build every publisher, or invoke one of Aspire's generated resource steps directly:

```bash
aspire do build
aspire do build-orders-api
```

Every registry-backed module image also contributes a `push-<resource>` step. Native project and
Dockerfile publishers use Aspire's standard build and push operations. An advanced push depends on
its matching command build step, so `aspire do push` prepares a missing image before pushing it. An
explicit `ImageRegistry` pushes the effective image reference directly. A resource associated with
`AddContainerRegistry` through `WithContainerRegistry` uses Aspire's registry-aware image manager
instead. Authenticate the selected container runtime to the destination registry before invoking the
step.

After pushing an advanced command publisher's effective image, a clean publisher also tags and pushes
the same image with its sanitized source branch (`feature/orders` becomes `feature-orders`). This
branch alias is published even when configuration or `images publish --tag` selects a separate
canonical tag, so a default-branch consumer can follow a stable name while module image workflow
documents remain pinned to the exact canonical image. Detached AppHost checkouts use
`GITHUB_HEAD_REF` and then `GITHUB_REF_NAME`. Dirty repositories and detached managed revisions never
update a mutable alias. If the canonical remote tag already equals the branch alias, the duplicate
tag and push are skipped.

Remote identity resolution uses one precedence order for describe, pull, and push: an explicit pull mapping (pull only), a per-resource `WithContainerRegistry`, the qualified registry declared by the module, and finally a deployment or default registry. Registries with an endpoint participate in remote pull and push. Empty-endpoint registries, such as the local registry supplied by a Docker Compose environment, preserve the module-owned registry host.

Project exports retain the `ImageRegistry` from `ModuleImageCommandOptions` when they are represented as containers. When the destination is an Aspire registry resource instead, configure that registry and any remote-image options on the existing container-export callback:

```csharp
#pragma warning disable ASPIRECOMPUTE003, ASPIREPIPELINES003
var registry = builder.AddContainerRegistry("ghcr", "ghcr.io", "example/orders");

module.AddProject("orders-api", projectPath)
    .ExportAsContainerWithCommand(
        new ModuleImageCommandOptions(
            "orders-api",
            "dotnet",
            "publish",
            "-t:PublishContainer"),
        (_, container) => container
            .WithContainerRegistry(registry)
            .WithRemoteImageName("api")
            .WithRemoteImageTag("candidate"));
#pragma warning restore ASPIRECOMPUTE003, ASPIREPIPELINES003
```

Push every eligible image, or invoke one of Aspire's generated resource steps directly:

```bash
aspire do push
aspire do push-orders-api
```

The standard aggregates retain Aspire's normal application-wide semantics. For validated module,
resource, and mixed multi-resource selection, use repeatable `modular-apphosts images publish --module`
and `--resource` options;
the tool owns that argument contract and runs the separate `workflow-images` aggregate once. That
single AppHost invocation builds and pushes the selected graph and writes its module image workflow
document without changing the application manifest or standard `push` aggregate.

### Describe module images

Generate a machine-readable inventory from the same effective identity resolver used by the pull and push pipelines:

```bash
aspire do describe-images --output-path artifacts
```

The command writes deterministic schema-version-3 JSON to `artifacts/module-images.json` and logs a concise reference summary. Its `modules` collection contains every materialized module name and declared contract package ID, including modules without container images. Each `images` entry contains the declared resource name, effective prefixed or aliased Aspire name, resource kind, registry, repository without tag, effective tag or digest, complete run and pull references, a structured registry/repository/tag push target when a push step exists, and the resolved build command and source when the module publishes that image. Description is read-only: it resolves identities without pulling, building, or tagging images. This custom workflow inventory is distinct from Aspire's application manifest and gives automation module/image identities that the application manifest does not contain.

### Pull module images

The same registry-backed modular project exports, declared containers, and factory-created containers contribute a `pull-<resource>` step to the module-provided `pull` pipeline. An explicit `ImageRegistry` pulls the effective image reference directly. A resource associated with an Aspire registry resolves its remote image name and tag, pulls that reference, and tags it back to the local image reference used by the container resource. Resolution starts from the configured module image name and tag, preserving an owner-qualified repository such as `example/orders-api`.

Use `WithImagePullMapping` when the pull source must be declared independently of the resource image. The pipeline pulls the supplied complete remote reference and tags it as the resource's effective local image, even when the two references use different registries:

```csharp
module.AddContainer("api", "ghcr.io/api", "1-0")
    .WithImagePullMapping("mycustomregistry.io/images:api-1-0");
```

In this example, `aspire do pull-api` executes `pull mycustomregistry.io/images:api-1-0` followed by `tag mycustomregistry.io/images:api-1-0 ghcr.io/api:1-0`. The explicit mapping takes precedence over `WithContainerRegistry`, remote push-name callbacks, and default registry targets for pull resolution. The mapping applies to pulls; `aspire do push` retains the resource's configured push target. The resource's local image must be tag-based because the pipeline retags the pulled image.

Pull and re-tag lifecycle messages are reported once through the Aspire pipeline step. Unmodified
container-runtime stdout and stderr stay in the pulled resource's log stream for dashboard and
programmatic diagnostics.

Pull every eligible module image, or invoke one of Aspire's generated resource steps directly:

```bash
aspire do pull
aspire do pull-orders-api
```

Registry authentication for every referenced registry is supplied by Aspire's selected Docker or Podman runtime in the same way as push authentication.

Advanced command publishers are declared as immutable recipes while the AppHost model is built. A
recipe is evaluated by the resource-start callback or by the module build, pull, and push pipelines.
Description resolves the same identity without preparing the image. Native publishers remain Aspire
resources and annotations instead of being converted into command recipes.

Build commands that choose their own output tag can set `ProducedImageReference`. After the build
command succeeds, the selected Docker or Podman runtime retags the result to the source-specific
canonical reference and then to the stable local `aspire-run` alias used by the container. A fixed
output reference is accepted; any output that varies with resolved image identity must use the
documented image placeholders rather than relying on exact-string argument rewriting:

```csharp
new ModuleImageCommandOptions("example/orders-database", "pwsh", "./build-image.ps1")
{
    ImageRegistry = "ghcr.io",
    ProducedImageReference = "orders-database:production",
    PullBeforeBuild = true
};
```

`PullBeforeBuild` first tries the clean canonical reference. A dirty source is always rebuilt and receives a `-dirty` canonical tag. With `PublishImage: false` and a complete registry/name plus exactly one tag or digest, the resource is an external-image override: it has no build recipe and requires no source checkout.

`WorkingDirectory` is relative to the effective build repository root. It defaults to the project directory for `ExportAsContainer` when the module repository also builds the image. A separate build repository and `WithImagePublishCommand` both default to the build repository root. The command and arguments are executed directly without a shell.

### Build a resource from another repository

The repository that defines a resource and the repository that builds its image are independent. Set `BuildRepository` on the resource's export options when, for example, an application contract declares a custom database container but the Dockerfile and build inputs belong to the database repository:

```csharp
module.AddContainer("orders-database", "example/orders-database")
    .WithImagePublishCommand(new ModuleImageCommandOptions(
        imageName: "example/orders-database",
        publishCommand: ModuleImageCommandOptions.ContainerRuntimePlaceholder,
        publishArguments:
        [
            "build",
            "--tag",
            ModuleImageCommandOptions.ImageReferencePlaceholder,
            "."
        ])
    {
        BuildRepository = "https://github.com/example/orders-database.git",
        CheckoutDirectoryName = "orders-database-images",
        WorkingDirectory = "."
    });
```

The AppHost plans that checkout independently of the module repository and uses its branch, commit, and dirty state for the canonical image tag. An unpinned remote defaults to the human-readable `<sibling-parent>/orders-database`; `ModuleImageCommandOptions.CheckoutDirectoryName` or the resource-level `CheckoutDirectoryName` configuration shown below changes that one filename segment. From the AppHost directory, run `aspire do initialize --apphost . --non-interactive` when a source build is required. A missing canonical checkout is cloned with `Created` ownership and retains the configured initialization update behavior. A matching existing sibling is adopted with `Adopted` ownership and initialization never moves or updates it. A mismatched origin or same-name planning collision fails and requires a distinct explicit name; no hashed fallback is used.

When `PullBeforeBuild` and an explicit tag allow the published image to satisfy preparation, normal run and build do not inspect or require the separate checkout, whether it is absent or already initialized. Initialization still acquires it for later source builds. An exact `BuildRepositoryRevision` is initialized in a distinct hashed sibling, so selecting a database commit never detaches or moves a developer checkout; `CheckoutDirectoryName` is rejected when a revision is configured. This optionality applies only to the separate image build repository; a module definition repository required by projects or factories remains mandatory.

The receiving AppHost can replace the declaration for one environment without changing the shared contract:

```csharp
builder.ConfigureModularAppHosts(options =>
{
    options.Modules[OrdersModule.Name] = new DistributedApplicationModuleOptions
    {
        Containers =
        {
            ["orders-database"] = new DistributedApplicationModuleContainerOptions
            {
                BuildRepository = "/work/orders-database",
                BuildRepositoryRevision = "feature/new-schema",
                RefreshBuildRepositoryOnRun = false
            }
        }
    };
});
```

Relative local build-repository paths are resolved from the AppHost directory. An existing unpinned local path is used directly. An unpinned remote uses its canonical repository-name sibling; a repository paired with a revision uses a collision-resistant hashed sibling. Both are acquired only by `initialize`. `RefreshBuildRepositoryOnRun` can opt a clean, unpinned build checkout into a run-time fast-forward; it defaults to `false`, and dirty checkouts are never moved.

Legacy hashed unpinned build clones are not reused automatically. Rerun initialization to create the canonical checkout, or set `Aspire:ModularAppHosts:Modules:<module>:Containers:<resource>:CheckoutDirectoryName` (or the corresponding `Projects` key) to the legacy directory name. The value must be exactly one safe filename segment beneath the AppHost Git root's sibling parent.

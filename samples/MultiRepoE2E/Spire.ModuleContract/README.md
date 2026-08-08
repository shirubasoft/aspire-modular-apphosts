# Spire sample module contract

This sample-only package demonstrates a producer-owned Aspire module contract. It declares the
stable `multi-repo-api` container and its image build command while the configured build repository
owns the Dockerfile and build script.

At AppHost startup, the module binds `SpireModuleOptions` from its conventional
`Aspire:ModularAppHosts:Modules:multi-repo-resource-build` configuration section. The build
repository is required and its revision is optional. Modular AppHosts checks out a configured
revision into a managed worktree before invoking `build-image.sh` there. The isolated CI consumer
references this package and supplies the build repository independently.

Reference the package from an AppHost and import its typed module:

```csharp
using Spire.ModuleContract;

var spire = await builder.ImportSpireModuleAsync();
```

The complete validation lives in the repository's `samples/MultiRepoE2E` directory.

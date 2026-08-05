# Spire sample module contract

This sample-only package demonstrates a producer-owned Aspire module contract. It declares the
stable `multi-repo-api` container and its image build command without taking a project reference to
the repository that owns the Dockerfile and build script.

At AppHost startup, `MULTI_REPO_RESOURCE_BUILD_REPOSITORY` supplies that repository and
`MULTI_REPO_RESOURCE_BUILD_REVISION` supplies the exact Git revision. Modular AppHosts checks out
that revision into a managed worktree before invoking `build-image.sh` there. The isolated CI
consumer references only this package and does not compile or embed the build repository.

Reference the package from an AppHost and import its typed module:

```csharp
using Spire.ModuleContract;

var spire = await builder.ImportSpireModuleAsync();
```

The complete validation lives in the repository's `samples/MultiRepoE2E` directory.

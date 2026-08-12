# Spire sample module contract

This sample-only package demonstrates a producer-owned Aspire module contract. It declares its
definition repository, the stable `multi-repo-api` container, and its image build command while a
separately configured build repository owns the Dockerfile and build inputs.

When the AppHost constructs its model, the module binds `SpireModuleOptions` from its conventional
`Aspire:ModularAppHosts:Modules:multi-repo-resource-build` configuration section. The definition and
build repositories are required and the build revision is optional. Run the initialization command
reported by preflight before starting a configuration that needs a managed checkout. Image preparation
uses Aspire's selected Docker or Podman runtime immediately before the container starts. When a
module image workflow document supplies the complete external image identity, the isolated CI consumer proves
that neither declared repository nor its managed checkout location is touched.

Reference the package from an AppHost and import its typed module:

```csharp
using Spire.ModuleContract;

var spire = builder.ImportSpireModule();
```

The complete validation lives in the repository's `samples/MultiRepoE2E` directory.

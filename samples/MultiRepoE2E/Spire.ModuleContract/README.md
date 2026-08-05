# Spire sample module contract

This sample-only package demonstrates a producer-owned Aspire module contract. It declares the
stable `spire-api` resource and repository-backed project location without taking a project
reference to the Spire repository.

Reference the package from an AppHost and import its typed module:

```csharp
using Spire.ModuleContract;

var spire = SpireModule.ImportModule(builder);
```

The complete validation lives in the repository's `samples/MultiRepoE2E` directory.

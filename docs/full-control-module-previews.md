# Full-control module previews

Full-control previews are a separate, explicit trust mode for consumer-owned E2E workflows. A source
repository supplies one sparse manifest of effective AppHost resource names and container tags. The
consumer does not evaluate contract libraries, producer descriptors, dependency locks, image
digests, or preview policy. It checks only that GitHub's trusted caller repository is declared by at
least one module in the AppHost.

This mode intentionally gives an accepted source repository control over every tag named in its
manifest. The AppHost still owns image registries and repository names; the manifest cannot replace
either. Use the immutable preview workflow in [module-previews.md](module-previews.md) when the
consumer needs artifact policy, contract validation, commit locks, or digest verification.

## Manifest

Commit one manifest in each source repository that needs a preview:

```json
{
  "$schema": "https://raw.githubusercontent.com/shirubasoft/aspire-modular-apphosts/main/schemas/full-control-module-preview.schema.json",
  "schemaVersion": 1,
  "sourceRefResources": [
    "catalog-api",
    "catalog-worker"
  ],
  "containerTags": {
    "shared-cache": "7.2.0"
  }
}
```

Both collections are sparse. Resources omitted from both retain their AppHost-defined tag.

- `sourceRefResources` receives the trusted source ref after every character outside
  `[A-Za-z0-9_.-]` is replaced with `-`. One ref can therefore select several final resources.
- `containerTags` supplies explicit tags. A resource cannot appear in both collections.
- Keys are effective resource names after any module prefix or alias is applied.
- Tags must satisfy the container tag grammar and 128-character limit.

Unknown resources, duplicate resources, non-container targets, invalid JSON, and invalid tags fail
as structural errors. They are not artifact, package, or policy validation.

## AppHost setup

Add the opt-in configuration call after declaring AppHost-owned containers and module definitions,
but before adding or importing the modules:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var catalog = await builder.ExportModuleAsync("catalog", definition =>
{
    definition.WithRepository("https://github.com/example/catalog.git");
    definition.AddContainer("catalog-api", "ghcr.io/example/catalog-api", "main");
    definition.AddContainer("catalog-worker", "ghcr.io/example/catalog-worker", "main");
});

builder.AddContainer("shared-cache", "redis", "main");

await builder.ApplyFullControlModulePreviewFromConfigurationAsync();
await builder.AddAsync(catalog);
```

That ordering lets existing AppHost containers receive their tags immediately and lets module
resources receive authoritative options before materialization. A tagged exported project is forced
into container mode. When every repository-dependent image in a module has an override, the module
repository is not cloned. The library also reapplies and validates the resolved tags when Aspire
finalizes, starts, and publishes the model, so run and publish use the same references.

The configuration call is a no-op when `ManifestPath` is absent, which preserves normal local use.
When enabled, all three values are required:

```text
Aspire:ModularAppHosts:FullControlPreview:ManifestPath
Aspire:ModularAppHosts:FullControlPreview:SourceRepository
Aspire:ModularAppHosts:FullControlPreview:SourceRef
```

In GitHub Actions, provide them with standard configuration environment variables:

```yaml
env:
  Aspire__ModularAppHosts__FullControlPreview__ManifestPath: ${{ steps.manifest.outputs.path }}
  Aspire__ModularAppHosts__FullControlPreview__SourceRepository: "https://github.com/${{ github.repository }}.git"
  Aspire__ModularAppHosts__FullControlPreview__SourceRef: "${{ github.head_ref || github.ref_name }}"
```

Do not read `SourceRepository` from the manifest or accept it as an ordinary workflow input. In a
reusable workflow, `github.repository` identifies the caller repository and is the trust boundary.
The consumer checkout ref remains a separate workflow input and never becomes manifest data.

## Consumer-owned reusable workflow

The runnable sample includes a
[`full-control-preview.yml`](../samples/FullControlPreview/.github/workflows/full-control-preview.yml)
template. It performs four generic steps:

1. Check out the caller repository and resolve the manifest inside that checkout.
2. Bind the repository and ref from trusted GitHub caller context.
3. Check out the consumer repository at its separately selected ref.
4. Run the consumer-owned Aspire publish and E2E commands with one resolved configuration set.

Registry login, package-feed login, cloud credentials, image mirroring, and test assertions remain
consumer-owned steps. Existing mirroring that reads generated Compose image references continues to
work because full-control mode changes only tags.

## Replacing a tag-input matrix

Move each per-image workflow input into `containerTags`. Move every service that should follow the
source branch into `sourceRefResources`; list multiple resources when one repository publishes more
than one image. Omit entries that should keep their AppHost defaults.

Then replace branch-selection, input-merging, and publish/test environment duplication with the three
configuration values above. Keep the consumer checkout ref, authentication, mirroring, test command,
status reporting, and repository protections in the consumer workflow. The resulting caller surface
is only the manifest path plus the consumer ref.

See the runnable [`FullControlPreview`](../samples/FullControlPreview/README.md) sample for a complete
manifest, AppHost, reusable workflow, and publish regression gate.

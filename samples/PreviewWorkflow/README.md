# Module preview workflow sample

This sample exercises preview policy, descriptor generation, and workflow generation entirely with
local fixtures:

- `module-preview-policy.json` keeps owner artifacts required while authorizing an external
  repository for one specific immutable image;
- `external-image-request.json` is the corresponding image-only request;
- `module-preview.producer.json` drives producer workflow generation for a real publisher in the
  [`ImagePushE2E`](../ImagePushE2E) AppHost.

Run the validation from the repository root:

```bash
bash samples/PreviewWorkflow/validate.sh
```

The script performs offline policy verification, confirms that schema-v3 requirements are scoped to
the external producer's authority, and materializes the request with an explicit per-command timeout.
It asks the AppHost for its effective image document and uses
`preview descriptor generate producer --check` to prove the committed descriptor matches that real
publisher before generating a producer GitHub Actions workflow in a temporary directory. It checks
the producer-owned registry login script, the high-level AppHost production command, and GitHub CLI
dispatch and watch commands.

Replace the `example` GitHub owners, registry host, consumer workflow, and secret placeholders with
repository-owned values before committing a generated workflow. Pass `--force` when regenerating an
existing output.

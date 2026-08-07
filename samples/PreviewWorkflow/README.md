# Module preview workflow sample

This sample demonstrates two preview features without contacting GitHub, a package feed, or an OCI
registry:

- `module-preview-policy.json` keeps owner artifacts required while authorizing an external
  repository for one specific immutable image;
- `external-image-request.json` is the corresponding image-only request; and
- `module-preview.producer.json` drives producer workflow generation for a real publisher in the
  [`ImagePushE2E`](../ImagePushE2E) AppHost; and
- `workflow-settings.json` selects a runner group, pins .NET without a `global.json`, creates a
  checkout token, and adds typed steps at all five supported workflow phases.

Run the validation from the repository root:

```bash
bash samples/PreviewWorkflow/validate.sh
```

The script performs offline policy verification, confirms that schema-v3 requirements are scoped to
the external producer's authority, and materializes the request with an explicit per-command timeout.
It asks the AppHost for its effective image document and uses
`preview descriptor generate producer --check` to prove the committed descriptor matches that real
publisher before generating a producer GitHub Actions workflow in a temporary directory. It also
checks the producer-owned registry login hook, the high-level AppHost production command, native
GitHub CLI dispatch and watch commands, and the absence of generated `jq` parsing. It also validates
that every configured hook is rendered on the documented side of its generated phase boundary.

The `example` GitHub owners, registry host, consumer workflow, and secret names are deliberately
non-operational placeholders. Replace them with repository-owned values before committing a generated
workflow. The generator refuses to overwrite an existing output unless `--force` is supplied.

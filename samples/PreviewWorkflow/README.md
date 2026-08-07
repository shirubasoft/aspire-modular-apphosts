# Module preview workflow sample

This sample demonstrates two preview features without contacting GitHub, a package feed, or an OCI
registry:

- `module-preview-policy.json` authorizes an external repository for one specific immutable image;
- `external-image-request.json` is the corresponding image-only request; and
- `module-preview.producer.json` drives producer workflow generation for a real publisher in the
  [`ImagePushE2E`](../ImagePushE2E) AppHost.

Run the validation from the repository root:

```bash
bash samples/PreviewWorkflow/validate.sh
```

The script performs offline policy verification, asks the AppHost for its effective image document,
confirms that the descriptor selects exactly one buildable push target, and generates a producer
GitHub Actions workflow in a temporary directory. It also checks the producer-owned registry login
hook and the generated describe, push, and trigger steps.

The `example` GitHub owners, registry host, consumer workflow, and secret names are deliberately
non-operational placeholders. Replace them with repository-owned values before committing a generated
workflow. The generator refuses to overwrite an existing output unless `--force` is supplied.

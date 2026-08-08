# External AppHost producer workflow sample

This sample models a build producer and an AppHost owner:

- `Producer` owns `ImageFixture/Dockerfile` and an image-only preview descriptor;
- `Consumer/ExternalConsumer.AppHost` owns the module declaration and its image publishing command.

Run the sample from the repository root:

```bash
bash samples/ExternalAppHostWorkflow/validate.sh
```

Running `aspire` from this sample directory also starts the consumer AppHost with the adjacent
producer directory configured as its build repository. Set `ExternalAppHost__RegistryEndpoint` to
use a different registry.

The validation generates a producer workflow configured with a trusted external AppHost repository
and ref. It checks that the workflow records the exact detached AppHost commit in the preview module
pin and maps every descriptor image's `BuildRepository` to the producer checkout through standard
.NET configuration keys. The secret named by `--github-token-secret` authenticates `gh repo clone`,
and the verified producer commit supplies `BuildRepositoryRevision`.

It then starts a temporary local OCI registry and runs the consumer-owned AppHost's real aggregate
`push` pipeline with `resource:external-image`. The image exists in the producer fixture, so the
successful build and push verify the configured cross-repository build context. The script cleans up
its registry container and generated workflow.

Replace the checked-in repository, workflow, registry, and secret placeholders with repository-owned
values. Scope the token to the producer, external AppHost, and consumer repositories. External
AppHost mode accepts image-only descriptors; contract-producing workflows use a producer-owned
AppHost.

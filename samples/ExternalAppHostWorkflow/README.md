# External AppHost producer workflow sample

This sample models two repository owners:

- `Producer` owns `ImageFixture/Dockerfile` and an image-only preview descriptor, but contains no
  AppHost or .NET project;
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
.NET configuration keys. The secret named by `--github-token-secret` authenticates `gh repo clone`.
It also pins `BuildRepositoryRevision` to the verified producer commit so a revision declared by the
external module cannot redirect the build.

It then starts a temporary local OCI registry and runs the consumer-owned AppHost's real aggregate
`push` pipeline with `resource:external-image`. The consumer fixture intentionally has no
`ImageFixture`, so the successful build and push prove that the publishing command used the
no-AppHost producer directory. The script cleans up its registry container and generated workflow.

The checked-in repository, workflow, registry, and secret names are deliberately non-operational
placeholders. Scope the token to the producer, external AppHost, and consumer repositories. External
AppHost workflow generation accepts contracts only when the producer owns the AppHost; the external
mode is image-only so it cannot attest a contract built from another source identity.

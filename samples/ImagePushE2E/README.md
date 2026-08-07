# Module image pipeline sample

This AppHost declares all three supported module image publishers: a declared container, a project
exported as a container, and a factory-created container. A second module contributes another image
publisher so the push sample proves both single- and multi-module selection. It also declares a consumed image with a
pull mapping. A Docker Compose compute environment proves that its local, empty registry does not
replace the remote identities declared by the module or by `WithContainerRegistry`. Publisher
resources use explicit start so running the AppHost demonstrates the model without requiring the
example registry to exist:

```bash
cd samples/ImagePushE2E/ImagePush.E2E.AppHost
aspire
```

The pipeline scripts exercise the same AppHost through Aspire's real deployment steps:

```bash
bash samples/ImagePushE2E/test-image-describe.sh
bash samples/ImagePushE2E/test-image-push.sh
bash samples/ImagePushE2E/test-image-pull.sh
```

`test-image-describe.sh` verifies the deterministic `module-images.json` document, including distinct
run, pull, and push references, build metadata, a registry mapping, and a consumed image with no push
or build operation. The push script proves resource, `module:<name>`, and multi-module scopes and
checks that an unselected module is not even built. The push and pull scripts create temporary local
registries and validate the operations against them. Set `ASPIRE_CONTAINER_RUNTIME=podman` to use
Podman instead of Docker.

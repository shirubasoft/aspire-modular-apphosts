# Module image pipeline sample

This AppHost declares all three supported module image publishers: a declared container, a project
exported as a container, and a factory-created container. A second module contributes another image
publisher so the push sample proves both single- and multi-module selection. It also declares a
consumed image with a pull mapping. A Docker Compose compute environment verifies that module and
`WithContainerRegistry` declarations retain their remote identities. Publisher resources use
explicit start, allowing the AppHost model to run against local fixtures:

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
run, pull, and push references, build metadata, a registry mapping, and a consumed image. The push
script proves resource, `module:<name>`, and multi-module scopes, including build isolation and the
sanitized source-branch alias for selected publishers. The push and pull scripts create temporary
local registries and validate the operations against them. Set `ASPIRE_CONTAINER_RUNTIME=podman` to
run them with Podman.

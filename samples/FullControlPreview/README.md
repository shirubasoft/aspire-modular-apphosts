# Full-control module preview

This sample is the opt-in, tag-only preview path. The caller owns one sparse JSON manifest. The
consumer AppHost continues to own every registry and image repository, and trusted CI context
supplies the caller repository and source ref separately.

Run the sample directly:

```bash
cd samples/FullControlPreview/FullControlPreview.AppHost
aspire run
```

The committed configuration uses `alpine` as the trusted source ref, so both source-ref resources
run as `nginx:alpine`; the explicit override runs the remaining resource as `redis:7.2.0`. No source
repository is cloned.

Run the publish regression gate from the repository root:

```bash
bash samples/FullControlPreview/validate.sh
```

Copy [`.github/workflows/full-control-preview.yml`](.github/workflows/full-control-preview.yml) into
the consumer repository and replace its repository, AppHost, authentication, and consumer-owned test
steps. A caller then needs only a tiny workflow:

```yaml
jobs:
  preview:
    uses: example/consumer/.github/workflows/full-control-preview.yml@main
    with:
      preview-manifest: .github/full-control-preview.json
      consumer-ref: main
```

The reusable workflow obtains the source repository and ref from GitHub's caller context; neither is
read from the manifest. Its consumer checkout ref is a separate input. Registry and package-feed
authentication remain consumer-owned steps and can be inserted before the consumer test command.

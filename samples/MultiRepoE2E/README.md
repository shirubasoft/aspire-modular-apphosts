# Multi-repository E2E sample

This sample is a consumer AppHost whose `spire-sample` module lives in the separate public
[`Shirubasoft/spire`](https://github.com/Shirubasoft/spire) repository. `Spire.ModuleContract`
models the producer-owned integration boundary: it contains the stable resource contract,
repository identity, and relative path to Spire's `Sample.ApiService`, but has no project reference
to that repository. The local project reference keeps normal development simple.

With sibling discovery enabled, starting the AppHost creates this layout when `spire` is missing:

```text
<workspace>/
├── consumer/  # this repository and the Spire.Consumer.AppHost
└── spire/     # cloned by: gh repo clone Shirubasoft/spire ...
```

CI packs that contract, removes its source from the isolated consumer, and switches the AppHost to
the package reference. It then confirms that no `spire` sibling exists, starts the AppHost, waits
for `spire-api` to become healthy, verifies the cloned repository's origin, and requests Spire's
`/weatherforecast` endpoint. This covers both package ownership and the real Git/repository/runtime
boundary instead of only testing two projects in one checkout.

To exercise the sample manually without reusing or changing an existing sibling checkout, clone
this repository into a temporary parent directory and run:

```bash
dotnet tool restore
dotnet tool run aspire -- start --apphost samples/MultiRepoE2E/Spire.Consumer.AppHost --non-interactive
dotnet tool run aspire -- wait spire-api --apphost samples/MultiRepoE2E/Spire.Consumer.AppHost --timeout 180 --non-interactive
curl --fail http://localhost:55201/weatherforecast
dotnet tool run aspire -- stop --apphost samples/MultiRepoE2E/Spire.Consumer.AppHost --non-interactive
```

The sample requires the .NET 10 SDK, Aspire CLI 13.4 or later, Git, GitHub CLI, and access to
GitHub. Authentication remains a GitHub CLI concern; CI supplies its scoped workflow token.

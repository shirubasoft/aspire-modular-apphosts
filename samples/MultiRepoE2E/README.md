# Multi-repository E2E sample

This sample is a consumer AppHost whose `spire-sample` module lives in the separate public
[`Shirubasoft/spire`](https://github.com/Shirubasoft/spire) repository. The module contract contains
the repository identity and the relative path to Spire's `Sample.ApiService`; it has no project
reference to that repository.

With sibling discovery enabled, starting the AppHost creates this layout when `spire` is missing:

```text
<workspace>/
├── consumer/  # this repository and the Spire.Consumer.AppHost
└── spire/     # cloned by: gh repo clone Shirubasoft/spire ...
```

CI validates the complete path from a clean workspace: it stages this repository as the isolated
`consumer` Git root, confirms that no `spire` sibling exists, starts the AppHost, waits for
`spire-api` to become healthy, verifies the cloned repository's origin, and requests Spire's
`/weatherforecast` endpoint.

To exercise the sample manually without reusing or changing an existing sibling checkout, clone
this repository into a temporary parent directory and run:

```bash
aspire start --apphost samples/MultiRepoE2E/Spire.Consumer.AppHost --non-interactive
aspire wait spire-api --apphost samples/MultiRepoE2E/Spire.Consumer.AppHost --timeout 180 --non-interactive
curl --fail http://localhost:55201/weatherforecast
aspire stop --apphost samples/MultiRepoE2E/Spire.Consumer.AppHost --non-interactive
```

The sample requires the .NET 10 SDK, Aspire CLI 13.4 or later, Git, GitHub CLI, and access to
GitHub. Authentication remains a GitHub CLI concern; CI supplies its scoped workflow token.

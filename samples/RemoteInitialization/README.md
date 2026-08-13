# Remote repository initialization

This minimal sample imports `notification-service` from the existing
[`shirubasoft/spire-external-repo-sample`](https://github.com/shirubasoft/spire-external-repo-sample)
repository. The repository is intentionally remote and unpinned so the initialization workflow is
required and can later fast-forward a clean checkout when `main` changes.

The sample requires the .NET 10 SDK, Aspire CLI 13.4.6 or later, Git, and the GitHub CLI.

From this directory, start the AppHost without initializing it:

```bash
aspire
```

Aspire cannot discover the missing remote project, so AppHost startup fails with an AppHost-aware
recovery command instead of only reporting that the project file is absent. Copy that exact command
and run it. It has this form:

```bash
aspire do initialize --apphost "<absolute-path-to-this-directory>" --non-interactive
```

Initialization clones the external repository into an initializer-owned directory next to this
repository. Start the sample again:

```bash
aspire
```

The resource now becomes healthy, and its `/notifications` endpoint is available from the Aspire
dashboard. Run the initialization command again whenever you want to fast-forward the clean,
unpinned checkout to the latest `main` commit.

`notification-service` uses the specialized module project contract, so it also contributes
developer-local mode-switching steps. Its checked-in configuration keeps a fresh clone
runnable as a project. To run its .NET SDK container image instead, build it, select container mode,
and restart the AppHost:

```bash
aspire do build-notification-service --apphost . --non-interactive
aspire do use-container-notification-service --apphost . --non-interactive
aspire
```

Use `aspire do use-project-notification-service --apphost . --non-interactive` to force direct
project execution, or `aspire do use-configured-modes --apphost . --non-interactive` to remove all
developer-local mode overrides.

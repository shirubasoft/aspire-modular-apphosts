# Remote repository initialization

This minimal sample imports `notification-service` from the existing
[`shirubasoft/spire-external-repo-sample`](https://github.com/shirubasoft/spire-external-repo-sample)
repository. The repository is intentionally remote and unpinned so the initialization workflow is
required and can later fast-forward a clean checkout when `main` changes.

The sample requires the .NET 10 SDK, Aspire CLI 13.4.6 or later, and the GitHub CLI. Git can already
be installed or can be supplied by the initialization step described below.

Git is modeled as a required-tool resource instead of only as a startup warning. Its health check
resolves `git` on the AppHost machine, `notification-service` waits for that health check, and the
dashboard exposes both the Git downloads page and an **Install** command. The same platform-specific
installer is registered as a prerequisite of `aspire do initialize`, so Git is installed before the
repository-clone step when it is missing. The installer uses Winget on Windows, Homebrew on macOS,
and `sudo apt-get` on Linux.

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

Initialization first verifies or installs the required Git CLI, then clones the external repository
into the human-readable canonical sibling
`<workspace>/spire-external-repo-sample`, records `Created` ownership in the environment-independent
`modular-apphosts.json` state file, and can fast-forward that clean initializer-managed checkout on
later runs. If a matching sibling already exists before the first initialization, it is instead
recorded as `Adopted` and never moved or updated by initialization. This
sample deliberately initializes with the pipeline's default Production environment and starts with
its Development launch profile. Start the sample again:

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

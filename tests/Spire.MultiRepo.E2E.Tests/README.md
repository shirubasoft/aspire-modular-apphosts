# Multi-repository E2E tests

This xUnit project owns the MultiRepo sample's lifecycle and isolation validation. The checked-in
consumer and producer AppHosts run through `Aspire.Hosting.Testing`, which supplies resource health
notifications, endpoint discovery, HTTP clients, random host ports, and async cleanup.

The adjacent `Spire.MultiRepo.E2E.Support` executable handles the contracts that still require the
real Aspire CLI: explicit initialization, fail-fast remediation commands, process isolation, and
container-runtime selection. Its components are separated by responsibility:

- `MultiRepositoryScenario` coordinates the behavior under test.
- `TrackedRepositoryFixture` copies only paths returned by `git ls-files`; it never recursively copies
  a developer worktree.
- `GitProxy` records each invocation and rejects every command shape outside the exact read-only
  allowlist during normal run.
- `RuntimeProxy` proves the executable selected through Aspire's container runtime resolver.
- `ProcessExecutor` uses CliWrap with bounded cancellation and process-tree termination.
- `E2ERedactor`, `FailureBundle`, and cleanup/assertion components sanitize every emitted diagnostic.

Run the full Docker-backed suite from the repository root:

```bash
dotnet tool restore
MULTI_REPO_E2E=true ASPIRE_CONTAINER_RUNTIME=docker \
  dotnet test tests/Spire.MultiRepo.E2E.Tests/Spire.MultiRepo.E2E.Tests.csproj \
  --configuration Release
```

Use `ASPIRE_CONTAINER_RUNTIME=podman` for local Podman coverage. CI validates Docker end to end. On
failure, the support executable writes a sanitized bundle under `artifacts/e2e/multi-repo-failure`;
CI uploads that directory without retaining the temporary repositories.

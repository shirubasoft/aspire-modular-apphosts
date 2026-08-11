Never synchronously block on async work (including `.GetAwaiter().GetResult()`, `.Result`, or task `.Wait()`); propagate async to the caller instead.

Backward compatibility is not a constraint. When the best implementation of a feature requires a breaking change, make the breaking change directly instead of preserving legacy APIs or adding compatibility shims.

Mark every breaking change with Conventional Commits syntax (for example, `feat!:` or `fix!:`) and include a `BREAKING CHANGE:` footer that explains the impact and migration path. A plain `feat:` or `fix:` commit does not trigger a major release.

Samples should work by default by just executing `aspire` running the sample.

Every major feature must include a runnable sample that is validated in CI.

Prefer standard configuration APIs (`IConfiguration`/`IOptions<T>`) over reading environment variables directly.

Tool commands must have the same invocation and behavior locally and in CI.

Keep tool-backed CI workflows script-free; put orchestration in the tool instead of inline shell or jq.

Run local container-backed tests with Docker or Podman; check which runtime is installed and available before choosing.

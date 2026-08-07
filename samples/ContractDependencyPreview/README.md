# Contract dependency preview

This sample exports an Aspire module whose producer contract directly consumes a separately packed
shared contract. Run the AppHost with `aspire` from `ContractDependencyPreview.AppHost`.

The CI validation creates a local package feed, restores the producer contract, generates its exact
dependency lock from `project.assets.json`, accepts the matching consumer policy, and proves that a
different exact version is rejected. It then materializes the contract from a clean local Git source,
runs the real restore and pack commands, verifies the restored dependency, and records the lock in the
resolution:

```bash
bash samples/ContractDependencyPreview/validate.sh
```

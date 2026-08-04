# Two modular AppHosts

This sample demonstrates one AppHost exporting a mixed module and another AppHost importing it.

```text
AppHost A
├── sample-api project ── podman build ──> modular-sample-api:dev
└── sample-static container (nginx:alpine)
             │
             └── exported module "AppHostA"
                         │
                         ▼
AppHost B imports sample-api + sample-static
└── dependency-gateway container
    ├── references both HTTP endpoints
    ├── waits for both resources to be healthy
    └── reports healthy only after probing both upstreams
```

The shared module definition is in [`ModuleContract/AppHostAModule.cs`](ModuleContract/AppHostAModule.cs). Its project export supplies the exact command that must produce the configured image:

```text
podman build --tag modular-sample-api:dev .
```

The extension does not generate or alter this command.

## Prerequisites

- .NET 10 SDK
- Aspire CLI 13.4 or later
- A running Podman-compatible container runtime

## Run AppHost A

```bash
cd samples/AppHostA
aspire run
```

AppHost A materializes its local module. `sample-api-installer` builds the project image before `sample-api` starts, while `sample-static` runs directly from `nginx:alpine`.

## Run AppHost B

Stop AppHost A, then run the importing host:

```bash
cd samples/AppHostB
aspire run
```

AppHost B points `module-repository-base-location` at the sample source directory, imports the `AppHostA` module, and starts its own `dependency-gateway` container. In another terminal, verify readiness through Aspire:

```bash
aspire wait sample-api
aspire wait sample-static
aspire wait dependency-gateway
aspire describe --include-hidden
```

The dashboard graph shows `Reference` and `WaitFor` relationships from `dependency-gateway` to both imported containers. Open the gateway endpoint shown by the dashboard; `/health` returns HTTP 200 only while both upstreams respond successfully.

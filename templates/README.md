# Shirubasoft Aspire Modular AppHosts templates

Install the template package and add a typed module contract to a project that references
`Shirubasoft.Aspire.ModularAppHosts`:

```bash
dotnet new install Shirubasoft.Aspire.ModularAppHosts.Templates
dotnet new aspire-module --name OrdersModule --moduleName orders
```

The generated contract starts with an nginx container so it can be materialized immediately. Replace that resource with the projects, containers, and integrations owned by the module.

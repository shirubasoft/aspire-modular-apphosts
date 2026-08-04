using Aspire.Hosting.ModularAppHosts;
using EShop.Modules;

var builder = DistributedApplication.CreateBuilder(args);
var sampleRoot = Path.GetFullPath("..", builder.AppHostDirectory);

var compose = builder.AddDockerComposeEnvironment("e2e")
    .WithDashboard(false);
var ordersApiKey = builder.AddParameter("orders-api-key", secret: true);

var catalogDefinition = CatalogModule.Register(builder, sampleRoot);
var catalog = CatalogModule.AddModule(builder, catalogDefinition);

var ordersDefinition = OrdersModule.Register(builder, sampleRoot);
var orders = OrdersModule.AddModule(builder, ordersDefinition);

orders.Api
    .WithReference(catalog.Api.GetEndpoint("http"))
    .WithEnvironment("Catalog__Endpoint", catalog.Api.GetEndpoint("http"))
    .WithEnvironment("Orders__ApiKey", ordersApiKey)
    .WaitFor(catalog.Api);

compose
    .WithTestEndpoint(CatalogModule.ApiResourceName, catalog.Api.GetEndpoint("http"))
    .WithTestEndpoint(OrdersModule.ApiResourceName, orders.Api.GetEndpoint("http"))
    .WithTestValue("orders-api-key", ordersApiKey.Resource);

builder.Build().Run();

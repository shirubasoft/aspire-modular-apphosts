using Aspire.Hosting;
using Aspire.Hosting.ModularAppHosts;
using Xunit;

namespace Aspire.Hosting.ModularAppHosts.Tests;

internal static class TestCancellationExtensions
{
    public static Task<IDistributedApplicationModule> ExportModuleAsync(
        this IDistributedApplicationBuilder builder,
        string name,
        Action<IDistributedApplicationModuleBuilder> moduleBuilder)
    {
        return DistributedApplicationModuleExtensions.ExportModuleAsync(
            builder,
            name,
            moduleBuilder,
            TestContext.Current.CancellationToken);
    }

    public static Task<IDistributedApplicationModule> DefineModuleAsync(
        this IDistributedApplicationBuilder builder,
        string name,
        string version,
        Action<IDistributedApplicationModuleBuilder> moduleBuilder)
    {
        return DistributedApplicationModuleExtensions.DefineModuleAsync(
            builder,
            name,
            version,
            moduleBuilder,
            TestContext.Current.CancellationToken);
    }

    public static Task<IDistributedApplicationModule> AddAsync(
        this IDistributedApplicationBuilder builder,
        IDistributedApplicationModule module)
    {
        return DistributedApplicationModuleExtensions.AddAsync(
            builder,
            module,
            TestContext.Current.CancellationToken);
    }

    public static Task<IDistributedApplicationModule> ImportModuleAsync(
        this IDistributedApplicationBuilder builder,
        string name)
    {
        return DistributedApplicationModuleExtensions.ImportModuleAsync(
            builder,
            name,
            TestContext.Current.CancellationToken);
    }

    public static Task<IDistributedApplicationModule> ImportModuleAsync(
        this IDistributedApplicationBuilder builder,
        string name,
        ModuleImportOptions importOptions)
    {
        return DistributedApplicationModuleExtensions.ImportModuleAsync(
            builder,
            name,
            importOptions,
            TestContext.Current.CancellationToken);
    }
}

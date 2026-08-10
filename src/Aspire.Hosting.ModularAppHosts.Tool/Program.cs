namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal static class Program
{
    private static async Task<int> Main(string[] args) =>
        await ToolApplication.RunAsync(
            args,
            new CliWrapProcessRunner(Console.Out, Console.Error),
            new SystemEnvironmentAccessor(),
            Console.Out,
            Console.Error,
            CancellationToken.None).ConfigureAwait(false);
}

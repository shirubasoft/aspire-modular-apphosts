using CliWrap;
using CliWrap.Buffered;

namespace Spire.MultiRepo.E2E.Support;

internal static partial class Program
{
    internal sealed record AspireCommand(string FileName, IReadOnlyList<string> PrefixArguments)
    {
        public static AspireCommand Create(string? aspirePath) =>
            string.IsNullOrWhiteSpace(aspirePath)
                ? new AspireCommand("dotnet", ["tool", "run", "aspire", "--"])
                : new AspireCommand(aspirePath, []);
    }

    internal sealed record ProcessInvocation(
        string FileName,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,
        IReadOnlyDictionary<string, string?>? Environment = null);

    internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public bool IsSuccess => ExitCode == 0;

        public string CombinedOutput => Redact($"{StandardOutput}{Environment.NewLine}{StandardError}");
    }

    internal sealed class ProcessExecutor(TimeSpan? processTimeout = null)
    {
        private readonly TimeSpan _processTimeout = processTimeout ?? TimeSpan.FromMinutes(10);

        public async Task<ProcessResult> RunAsync(
            ProcessInvocation invocation,
            CancellationToken cancellationToken)
        {
            var command = Cli.Wrap(invocation.FileName)
                .WithArguments(invocation.Arguments)
                .WithWorkingDirectory(invocation.WorkingDirectory)
                .WithValidation(CommandResultValidation.None);
            if (invocation.Environment is not null)
            {
                command = command.WithEnvironmentVariables(invocation.Environment);
            }

            using var timeout = new CancellationTokenSource(_processTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            try
            {
                var result = await command.ExecuteBufferedAsync(linked.Token).ConfigureAwait(false);
                return new ProcessResult(
                    result.ExitCode,
                    Redact(result.StandardOutput),
                    Redact(result.StandardError));
            }
            catch (OperationCanceledException) when (
                timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Process '{invocation.FileName}' exceeded the {_processTimeout} E2E timeout.");
            }
        }
    }
}

using System.Text;
using CliWrap;
using CliCommand = global::CliWrap.Cli;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal enum ProcessOutputMode
{
    Stream,
    Capture
}

internal sealed record ProcessInvocation(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? EnvironmentVariables = null,
    ProcessOutputMode OutputMode = ProcessOutputMode.Stream,
    string? StandardInput = null);

internal sealed record ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool IsSuccess => ExitCode == 0;
}

internal interface IProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(
        ProcessInvocation invocation,
        CancellationToken cancellationToken);
}

internal sealed class CliWrapProcessRunner(
    Stream input,
    TextWriter output,
    TextWriter error) : IProcessRunner
{
    public async Task<ProcessExecutionResult> RunAsync(
        ProcessInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var command = CliCommand.Wrap(invocation.FileName)
            .WithArguments(invocation.Arguments)
            .WithWorkingDirectory(invocation.WorkingDirectory)
            .WithValidation(CommandResultValidation.None);
        command = invocation.OutputMode switch
        {
            ProcessOutputMode.Capture => command
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(standardOutput))
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(standardError)),
            ProcessOutputMode.Stream => command
                .WithStandardOutputPipe(PipeTarget.ToDelegate(output.WriteLine))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(error.WriteLine)),
            _ => throw new ArgumentOutOfRangeException(nameof(invocation))
        };
        if (invocation.EnvironmentVariables is not null)
        {
            command = command.WithEnvironmentVariables(invocation.EnvironmentVariables);
        }
        command = command.WithStandardInputPipe(
            invocation.StandardInput is not null
                ? PipeSource.FromString(invocation.StandardInput)
                : PipeSource.FromStream(input));

        var result = await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessExecutionResult(
            result.ExitCode,
            standardOutput.ToString(),
            standardError.ToString());
    }
}

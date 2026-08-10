using System.Text;
using CliWrap;
using CliCommand = global::CliWrap.Cli;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal sealed record ProcessInvocation(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? EnvironmentVariables = null,
    string? StandardInput = null,
    bool CaptureOutput = true);

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

internal sealed class CliWrapProcessRunner(TextWriter output, TextWriter error) : IProcessRunner
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
        if (invocation.CaptureOutput)
        {
            command = command
                .WithStandardOutputPipe(PipeTarget.Merge(
                    PipeTarget.ToStringBuilder(standardOutput),
                    PipeTarget.ToDelegate(output.WriteLine)))
                .WithStandardErrorPipe(PipeTarget.Merge(
                    PipeTarget.ToStringBuilder(standardError),
                    PipeTarget.ToDelegate(error.WriteLine)));
        }
        if (invocation.EnvironmentVariables is not null)
        {
            command = command.WithEnvironmentVariables(invocation.EnvironmentVariables);
        }

        if (invocation.StandardInput is not null)
        {
            command = command.WithStandardInputPipe(PipeSource.FromString(invocation.StandardInput));
        }

        var result = await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessExecutionResult(
            result.ExitCode,
            standardOutput.ToString(),
            standardError.ToString());
    }
}

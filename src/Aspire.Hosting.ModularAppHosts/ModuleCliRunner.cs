using System.Text;
using System.Text.RegularExpressions;
using CliWrap;
using CliCommand = global::CliWrap.Cli;

namespace Aspire.Hosting;

internal sealed record ModuleCliResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool IsSuccess => ExitCode == 0;
}

internal static class ModuleCliRunner
{
    public static async Task<ModuleCliResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        string operation,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The command timeout must be positive.");
        }

        var redactedOperation = ModuleCliOutputRedactor.Redact(operation);
        progress ??= line => Console.WriteLine($"[{redactedOperation}] {line}");
        var progressLock = new object();
        void ReportProgress(string line)
        {
            lock (progressLock)
            {
                progress(ModuleCliOutputRedactor.Redact(line));
            }
        }

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            var result = await CliCommand.Wrap(executable)
                .WithArguments(arguments)
                .WithWorkingDirectory(workingDirectory)
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.Merge(
                    PipeTarget.ToStringBuilder(standardOutput),
                    PipeTarget.ToDelegate(ReportProgress)))
                .WithStandardErrorPipe(PipeTarget.Merge(
                    PipeTarget.ToStringBuilder(standardError),
                    PipeTarget.ToDelegate(ReportProgress)))
                .ExecuteAsync(linkedSource.Token);

            return new ModuleCliResult(
                result.ExitCode,
                ModuleCliOutputRedactor.Redact(standardOutput.ToString()),
                ModuleCliOutputRedactor.Redact(standardError.ToString()));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{redactedOperation} exceeded the configured timeout of {timeout}.",
                exception);
        }
    }
}

internal static partial class ModuleCliOutputRedactor
{
    private const string RedactedValue = "[REDACTED]";

    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var redacted = UriRegex().Replace(value, static match => RedactUri(match.Value));
        redacted = CredentialHelperRegex().Replace(
            redacted,
            static match => $"{match.Groups["prefix"].Value}{RedactedValue}");
        redacted = CredentialRegex().Replace(redacted, static match =>
        {
            var prefix = match.Groups["prefix"].Value;
            if (match.Groups["doubleQuoted"].Success)
            {
                return $"{prefix}\"{RedactedValue}\"";
            }

            if (match.Groups["singleQuoted"].Success)
            {
                return $"{prefix}'{RedactedValue}'";
            }

            return $"{prefix}{RedactedValue}";
        });
        redacted = AuthorizationRegex().Replace(
            redacted,
            static match => $"{match.Groups["prefix"].Value}{RedactedValue}");
        return EnvironmentAssignmentRegex().Replace(redacted, static match =>
        {
            var prefix = match.Groups["prefix"].Value;
            if (match.Groups["doubleQuoted"].Success)
            {
                return $"{prefix}\"{RedactedValue}\"";
            }

            if (match.Groups["singleQuoted"].Success)
            {
                return $"{prefix}'{RedactedValue}'";
            }

            return $"{prefix}{RedactedValue}";
        });
    }

    private static string RedactUri(string value)
    {
        const string schemeSeparator = "://";
        var authorityStart = value.IndexOf(schemeSeparator, StringComparison.Ordinal) + schemeSeparator.Length;
        var authorityEnd = value.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = value.Length;
        }

        var userInfoEnd = value.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
        if (userInfoEnd >= authorityStart)
        {
            value = string.Concat(
                value.AsSpan(0, authorityStart),
                RedactedValue,
                "@",
                value.AsSpan(userInfoEnd + 1));
        }

        var queryStart = value.IndexOf('?', authorityStart);
        var fragmentStart = value.IndexOf('#', authorityStart);
        if (queryStart >= 0 && (fragmentStart < 0 || queryStart < fragmentStart))
        {
            return fragmentStart >= 0
                ? $"{value[..queryStart]}?{RedactedValue}#{RedactedValue}"
                : $"{value[..queryStart]}?{RedactedValue}";
        }

        return fragmentStart >= 0
            ? $"{value[..fragmentStart]}#{RedactedValue}"
            : value;
    }

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]*://[^\s<>\""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriRegex();

    [GeneratedRegex(
        @"(?<prefix>\bcredential\.[^\r\n=]+\.helper\s*=\s*)[^\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialHelperRegex();

    [GeneratedRegex(
        @"(?<prefix>\b(?:[a-z0-9]+[-_])*(?:password|passwd|pwd|passphrase|token|access[-_]?token|refresh[-_]?token|id[-_]?token|auth[-_]?token|api[-_]?key|client[-_]?secret|secret)\b[\""']?\s*[:=]\s*)(?:\""(?<doubleQuoted>[^\""\r\n]*)\""|'(?<singleQuoted>[^'\r\n]*)'|(?<unquoted>[^\s,;]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialRegex();

    [GeneratedRegex(
        @"(?<prefix>\b(?:authorization\s*:\s*)?(?:bearer|basic)\s+)[a-z0-9._~+/=-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(
        @"(?<prefix>\b[A-Z_][A-Z0-9_]*\s*=\s*)(?:\""(?<doubleQuoted>[^\""\r\n]*)\""|'(?<singleQuoted>[^'\r\n]*)'|(?<unquoted>[^\s,;]+))",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentAssignmentRegex();
}

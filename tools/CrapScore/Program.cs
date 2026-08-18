using System.Xml.Linq;

namespace CrapScore;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out var options))
        {
            Console.Error.WriteLine(
                "Usage: dotnet run --project tools/CrapScore -- <report-directory> "
                + "[--output <markdown-file>] [--base-reports <target-report-directory>]");
            return 2;
        }

        var orderedScores = await ReadScoresAsync(options.ReportDirectory);
        var report = CrapReportRenderer.Render(orderedScores, Directory.GetCurrentDirectory());
        Console.Write(report);

        if (options.OutputPath is not null)
        {
            var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
            if (outputDirectory is not null)
            {
                Directory.CreateDirectory(outputDirectory);
            }

            await File.WriteAllTextAsync(options.OutputPath, report);
        }

        if (options.BaseReportDirectory is not null)
        {
            var baseScores = await ReadScoresAsync(options.BaseReportDirectory);
            var gate = CrapGate.Evaluate(orderedScores[0].Score, baseScores[0].Score);
            Console.WriteLine(gate.Message);
            return gate.Passed ? 0 : 1;
        }

        return 0;
    }

    private static bool TryParseArguments(string[] args, out Options options)
    {
        options = new Options(string.Empty, null, null);
        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            return false;
        }

        string? outputPath = null;
        string? baseReportDirectory = null;
        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--output" when TryReadValue(args, ref index, out var value):
                    outputPath = value;
                    break;
                case "--base-reports" when TryReadValue(args, ref index, out var value):
                    baseReportDirectory = value;
                    break;
                default:
                    return false;
            }
        }

        options = new Options(args[0], outputPath, baseReportDirectory);
        return true;
    }

    private static async Task<MethodCrapScore[]> ReadScoresAsync(string reportDirectory)
    {
        if (!Directory.Exists(reportDirectory))
        {
            throw new InvalidOperationException($"Coverage report directory does not exist: {reportDirectory}");
        }

        var reportPaths = Directory
            .EnumerateFiles(reportDirectory, "*.opencover.xml", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (reportPaths.Length == 0)
        {
            throw new InvalidOperationException($"No *.opencover.xml files found in: {reportDirectory}");
        }

        var scores = new List<MethodCrapScore>();
        foreach (var reportPath in reportPaths)
        {
            await using var stream = File.OpenRead(reportPath);
            var document = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None);
            scores.AddRange(OpenCoverCrapReport.Parse(document));
        }

        return scores
            .OrderByDescending(score => score.Score)
            .ThenBy(score => score.Assembly, StringComparer.Ordinal)
            .ThenBy(score => score.Method, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryReadValue(string[] args, ref int index, out string value)
    {
        index++;
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            value = string.Empty;
            return false;
        }

        value = args[index];
        return true;
    }

    private sealed record Options(
        string ReportDirectory,
        string? OutputPath,
        string? BaseReportDirectory);
}

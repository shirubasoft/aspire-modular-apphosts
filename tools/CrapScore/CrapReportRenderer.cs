using System.Globalization;
using System.Text;

namespace CrapScore;

internal static class CrapReportRenderer
{
    private const int MethodLimit = 20;

    public static string Render(IReadOnlyList<MethodCrapScore> scores, string workingDirectory)
    {
        if (scores.Count == 0)
        {
            throw new InvalidOperationException("The OpenCover reports did not contain any methods with CRAP inputs.");
        }

        var maximum = scores[0];
        var methodsOverThreshold = scores.Count(score => score.Score > CrapMetric.ConventionalThreshold);
        var builder = new StringBuilder()
            .AppendLine("# CRAP score")
            .AppendLine()
            .Append("Maximum method CRAP score: **")
            .Append(maximum.Score.ToString("F2", CultureInfo.InvariantCulture))
            .AppendLine("**")
            .Append("Methods over the conventional threshold of 30: **")
            .Append(methodsOverThreshold.ToString(CultureInfo.InvariantCulture))
            .Append(" of ")
            .Append(scores.Count.ToString(CultureInfo.InvariantCulture))
            .AppendLine("**")
            .AppendLine()
            .AppendLine("CRAP = complexity² × (1 − sequence coverage)³ + complexity.")
            .AppendLine()
            .AppendLine("| CRAP | Complexity | Coverage | Assembly | Source | Method |")
            .AppendLine("| ---: | ---: | ---: | --- | --- | --- |");

        foreach (var score in scores.Take(MethodLimit))
        {
            builder
                .Append("| ")
                .Append(score.Score.ToString("F2", CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(score.CyclomaticComplexity.ToString(CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(score.SequenceCoverage.ToString("F2", CultureInfo.InvariantCulture))
                .Append("% | ")
                .Append(Escape(score.Assembly))
                .Append(" | ")
                .Append(Escape(FormatSource(score, workingDirectory)))
                .Append(" | ")
                .Append(Escape(score.Method))
                .AppendLine(" |");
        }

        return builder.ToString();
    }

    private static string FormatSource(MethodCrapScore score, string workingDirectory)
    {
        if (score.SourceFile is null)
        {
            return "unknown";
        }

        var source = Path.IsPathRooted(score.SourceFile)
            ? Path.GetRelativePath(workingDirectory, score.SourceFile)
            : score.SourceFile;
        return score.SourceLine is null ? source : $"{source}:{score.SourceLine.Value}";
    }

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}

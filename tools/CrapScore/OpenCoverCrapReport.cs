using System.Globalization;
using System.Xml.Linq;

namespace CrapScore;

internal sealed record MethodCrapScore(
    string Assembly,
    string Method,
    string? SourceFile,
    int? SourceLine,
    int CyclomaticComplexity,
    double SequenceCoverage,
    double Score);

internal static class OpenCoverCrapReport
{
    public static IReadOnlyList<MethodCrapScore> Parse(XDocument document)
    {
        var scores = new List<MethodCrapScore>();

        foreach (var module in document.Descendants().Where(element => element.Name.LocalName == "Module"))
        {
            var assembly = ChildValue(module, "ModuleName") ?? "Unknown assembly";
            var files = module
                .Descendants()
                .Where(element => element.Name.LocalName == "File")
                .Select(element => new
                {
                    Id = AttributeValue(element, "uid"),
                    Path = AttributeValue(element, "fullPath"),
                })
                .Where(file => file.Id is not null && file.Path is not null)
                .ToDictionary(file => file.Id!, file => file.Path!, StringComparer.Ordinal);

            foreach (var method in module.Descendants().Where(element => element.Name.LocalName == "Method"))
            {
                if (!TryReadIntAttribute(method, "cyclomaticComplexity", out var complexity)
                    || !TryReadDoubleAttribute(method, "sequenceCoverage", out var coverage))
                {
                    continue;
                }

                var name = ChildValue(method, "Name") ?? "Unknown method";
                var fileId = method
                    .Descendants()
                    .FirstOrDefault(element => element.Name.LocalName == "FileRef")?
                    .Attributes()
                    .FirstOrDefault(attribute => attribute.Name.LocalName == "uid")?
                    .Value;
                var firstSequencePoint = method
                    .Descendants()
                    .FirstOrDefault(element => element.Name.LocalName == "SequencePoint");
                fileId ??= AttributeValue(firstSequencePoint, "fileid");

                int? sourceLine = null;
                if (int.TryParse(
                    AttributeValue(firstSequencePoint, "sl"),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedSourceLine))
                {
                    sourceLine = parsedSourceLine;
                }

                scores.Add(new MethodCrapScore(
                    assembly,
                    name,
                    fileId is not null && files.TryGetValue(fileId, out var sourceFile) ? sourceFile : null,
                    sourceLine,
                    complexity,
                    coverage,
                    CrapMetric.Calculate(complexity, coverage)));
            }
        }

        return scores
            .OrderByDescending(score => score.Score)
            .ThenBy(score => score.Assembly, StringComparer.Ordinal)
            .ThenBy(score => score.Method, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? ChildValue(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName == localName)?.Value;

    private static string? AttributeValue(XElement? element, string localName) =>
        element?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value;

    private static bool TryReadIntAttribute(XElement element, string name, out int value) =>
        int.TryParse(AttributeValue(element, name), NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool TryReadDoubleAttribute(XElement element, string name, out double value) =>
        double.TryParse(AttributeValue(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

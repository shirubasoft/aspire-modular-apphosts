using System.Text;

namespace Aspire.Hosting.ModularAppHosts;

internal static class DotEnvFile
{
    public static Dictionary<string, string> Load(string filePath)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var line in File.ReadLines(filePath))
        {
            lineNumber++;
            var content = line.TrimStart('\uFEFF').TrimStart();
            if (content.Length == 0 || content[0] == '#')
            {
                continue;
            }

            if (content.StartsWith("export", StringComparison.Ordinal) &&
                content.Length > "export".Length &&
                char.IsWhiteSpace(content["export".Length]))
            {
                content = content["export".Length..].TrimStart();
            }

            var separator = content.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw InvalidLine(filePath, lineNumber, "expected KEY=VALUE");
            }

            var key = content[..separator].Trim();
            if (!IsValidKey(key))
            {
                throw InvalidLine(filePath, lineNumber, $"'{key}' is not a valid environment variable name");
            }

            var value = ParseValue(content[(separator + 1)..], filePath, lineNumber);
            if (!values.TryAdd(key, value))
            {
                throw InvalidLine(filePath, lineNumber, $"environment variable '{key}' is defined more than once");
            }
        }

        return values;
    }

    private static string ParseValue(string value, string filePath, int lineNumber)
    {
        var content = value.TrimStart();
        if (content.Length == 0)
        {
            return string.Empty;
        }

        return content[0] switch
        {
            '\'' => ParseSingleQuotedValue(content, filePath, lineNumber),
            '"' => ParseDoubleQuotedValue(content, filePath, lineNumber),
            _ => ParseUnquotedValue(content)
        };
    }

    private static string ParseSingleQuotedValue(string content, string filePath, int lineNumber)
    {
        var closingQuote = content.IndexOf('\'', 1);
        if (closingQuote < 0)
        {
            throw InvalidLine(filePath, lineNumber, "single-quoted value is not terminated");
        }

        ValidateRemainder(content[(closingQuote + 1)..], filePath, lineNumber);
        return content[1..closingQuote];
    }

    private static string ParseDoubleQuotedValue(string content, string filePath, int lineNumber)
    {
        var value = new StringBuilder(content.Length);
        for (var index = 1; index < content.Length; index++)
        {
            var character = content[index];
            if (character == '"')
            {
                ValidateRemainder(content[(index + 1)..], filePath, lineNumber);
                return value.ToString();
            }

            if (character != '\\')
            {
                value.Append(character);
                continue;
            }

            if (++index >= content.Length)
            {
                throw InvalidLine(filePath, lineNumber, "double-quoted value ends with an incomplete escape");
            }

            var escaped = content[index];
            if (escaped is not ('n' or 'r' or 't' or '"' or '\\'))
            {
                value.Append('\\').Append(escaped);
                continue;
            }

            value.Append(escaped switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '"' => '"',
                '\\' => '\\',
                _ => escaped
            });
        }

        throw InvalidLine(filePath, lineNumber, "double-quoted value is not terminated");
    }

    private static string ParseUnquotedValue(string content)
    {
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '#' && (index == 0 || char.IsWhiteSpace(content[index - 1])))
            {
                return content[..index].TrimEnd();
            }
        }

        return content.TrimEnd();
    }

    private static void ValidateRemainder(string remainder, string filePath, int lineNumber)
    {
        var trailing = remainder.TrimStart();
        if (trailing.Length > 0 && trailing[0] != '#')
        {
            throw InvalidLine(filePath, lineNumber, "quoted value has unexpected trailing content");
        }
    }

    private static bool IsValidKey(string key)
    {
        return key.Length > 0 &&
            (char.IsLetter(key[0]) || key[0] == '_') &&
            key.All(character => char.IsLetterOrDigit(character) || character is '_' or '.' or '-');
    }

    private static InvalidOperationException InvalidLine(string filePath, int lineNumber, string detail) =>
        new($"The environment file '{filePath}' is invalid at line {lineNumber}: {detail}.");
}

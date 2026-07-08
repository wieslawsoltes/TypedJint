using System.Text.RegularExpressions;

namespace TypedJint;

public sealed record JavaScriptFunctionSource(
    string Name,
    string Source,
    bool HasJsDoc,
    int Start,
    int End);

public static class JavaScriptFunctionSourceScanner
{
    private static readonly Regex FunctionHeaderRegex = new(
        @"(?<doc>/\*\*[\s\S]*?\*/\s*)?function\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*\((?<params>[^)]*)\)\s*\{",
        RegexOptions.Compiled);

    public static IReadOnlyList<JavaScriptFunctionSource> Scan(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var result = new List<JavaScriptFunctionSource>();
        var position = 0;
        while (position < source.Length)
        {
            var match = FunctionHeaderRegex.Match(source, position);
            if (!match.Success)
            {
                break;
            }

            var bodyStart = match.Index + match.Length;
            var bodyEnd = FindMatchingBrace(source, bodyStart - 1);
            var functionSource = source.Substring(match.Index, bodyEnd - match.Index + 1);
            result.Add(new JavaScriptFunctionSource(
                match.Groups["name"].Value,
                functionSource,
                match.Groups["doc"].Success && !string.IsNullOrWhiteSpace(match.Groups["doc"].Value),
                match.Index,
                bodyEnd));

            position = bodyEnd + 1;
        }

        return result;
    }

    private static int FindMatchingBrace(string source, int openBrace)
    {
        var depth = 0;
        var inString = false;
        var inLineComment = false;
        var inBlockComment = false;
        var quote = '\0';

        for (var i = openBrace; i < source.Length; i++)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inLineComment)
            {
                if (c == '\n')
                {
                    inLineComment = false;
                }

                continue;
            }

            if (inBlockComment)
            {
                if (c == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (inString)
            {
                if (c == quote && !IsEscaped(source, i))
                {
                    inString = false;
                }

                continue;
            }

            if (c == '/' && next == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (c == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                inString = true;
                quote = c;
                continue;
            }

            if (c == '{')
            {
                depth++;
            }

            if (c == '}')
            {
                depth--;
            }

            if (depth == 0)
            {
                return i;
            }
        }

        throw new FormatException("Unterminated function body.");
    }

    private static bool IsEscaped(string source, int index)
    {
        var slashCount = 0;
        for (var i = index - 1; i >= 0 && source[i] == '\\'; i--)
        {
            slashCount++;
        }

        return slashCount % 2 != 0;
    }
}

using System.Text.RegularExpressions;

namespace TypedJint;

public sealed record JavaScriptClassSource(
    string Name,
    string? BaseName,
    string Source,
    string Body,
    int Start,
    int End);

public static class JavaScriptClassSourceScanner
{
    private static readonly Regex ClassHeaderRegex = new(
        @"class\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)(?:\s+extends\s+(?<base>[A-Za-z_$][A-Za-z0-9_$.]*))?\s*\{",
        RegexOptions.Compiled);

    public static IReadOnlyList<JavaScriptClassSource> Scan(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var result = new List<JavaScriptClassSource>();
        var position = 0;
        while (position < source.Length)
        {
            var match = ClassHeaderRegex.Match(source, position);
            if (!match.Success)
            {
                break;
            }

            var bodyStart = match.Index + match.Length;
            var bodyEnd = FindMatchingBrace(source, bodyStart - 1);
            var body = source[bodyStart..bodyEnd];
            var classSource = source.Substring(match.Index, bodyEnd - match.Index + 1);
            result.Add(new JavaScriptClassSource(
                match.Groups["name"].Value,
                match.Groups["base"].Success ? match.Groups["base"].Value : null,
                classSource,
                body,
                match.Index,
                bodyEnd));

            position = bodyEnd + 1;
        }

        return result;
    }

    internal static int FindMatchingBrace(string source, int openBrace)
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

        throw new FormatException("Unterminated class body.");
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

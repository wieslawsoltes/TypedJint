using System.Text;
using System.Text.RegularExpressions;

namespace TypedJint;

public static class JavaScriptClassCSharpPreviewGenerator
{
    private static readonly Regex MemberHeaderRegex = new(
        @"(?<static>static\s+)?(?<kind>get\s+|set\s+)?(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*\((?<params>[^)]*)\)\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex ThisPropertyRegex = new(
        @"\bthis\.(?<name>[A-Za-z_$][A-Za-z0-9_$]*)",
        RegexOptions.Compiled);

    public static string Generate(IReadOnlyList<JavaScriptClassSource> classes)
    {
        ArgumentNullException.ThrowIfNull(classes);

        var builder = new StringBuilder();
        foreach (var cls in classes)
        {
            EmitClass(builder, cls);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static void EmitClass(StringBuilder builder, JavaScriptClassSource cls)
    {
        var members = ParseMembers(cls);
        var properties = members
            .SelectMany(member => ThisPropertyRegex.Matches(member.Body).Select(match => match.Groups["name"].Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        builder.Append("public sealed class ").Append(SanitizeIdentifier(cls.Name));
        if (!string.IsNullOrWhiteSpace(cls.BaseName))
        {
            builder.Append(" : ").Append(SanitizeQualifiedName(cls.BaseName));
        }

        builder.AppendLine();
        builder.AppendLine("{");

        foreach (var property in properties)
        {
            builder.Append("    public dynamic? ")
                .Append(SanitizeIdentifier(property))
                .AppendLine(" { get; set; }");
        }

        if (properties.Length > 0)
        {
            builder.AppendLine();
        }

        foreach (var member in members)
        {
            EmitMember(builder, cls.Name, member);
            builder.AppendLine();
        }

        builder.AppendLine("}");
    }

    private static IReadOnlyList<JavaScriptClassMember> ParseMembers(JavaScriptClassSource cls)
    {
        var result = new List<JavaScriptClassMember>();
        var position = 0;
        while (position < cls.Body.Length)
        {
            var match = MemberHeaderRegex.Match(cls.Body, position);
            if (!match.Success)
            {
                break;
            }

            var bodyStart = match.Index + match.Length;
            var bodyEnd = JavaScriptClassSourceScanner.FindMatchingBrace(cls.Body, bodyStart - 1);
            result.Add(new JavaScriptClassMember(
                match.Groups["name"].Value,
                match.Groups["params"].Value,
                cls.Body[bodyStart..bodyEnd],
                match.Groups["static"].Success,
                match.Groups["kind"].Value.Trim()));

            position = bodyEnd + 1;
        }

        return result;
    }

    private static void EmitMember(StringBuilder builder, string className, JavaScriptClassMember member)
    {
        if (member.Kind == "get")
        {
            EmitGetter(builder, member);
            return;
        }

        if (member.Kind == "set")
        {
            EmitSetter(builder, member);
            return;
        }

        var parameters = SplitParameters(member.Parameters)
            .Select(parameter => "dynamic? " + SanitizeIdentifier(parameter))
            .ToArray();

        if (member.Name == "constructor")
        {
            builder.Append("    public ")
                .Append(SanitizeIdentifier(className))
                .Append('(')
                .Append(string.Join(", ", parameters))
                .AppendLine(")");
        }
        else
        {
            builder.Append(member.IsStatic ? "    public static dynamic? " : "    public dynamic? ")
                .Append(SanitizeIdentifier(member.Name))
                .Append('(')
                .Append(string.Join(", ", parameters))
                .AppendLine(")");
        }

        EmitBody(builder, member.Body, member.Name == "constructor");
    }

    private static void EmitGetter(StringBuilder builder, JavaScriptClassMember member)
    {
        builder.Append("    public dynamic? ")
            .Append(SanitizeIdentifier(member.Name))
            .AppendLine(" => ")
            .Append("        ")
            .Append(RewriteExpression(ExtractReturnedExpression(member.Body) ?? "null"))
            .AppendLine(";");
    }

    private static void EmitSetter(StringBuilder builder, JavaScriptClassMember member)
    {
        var parameter = SplitParameters(member.Parameters).FirstOrDefault() ?? "value";
        builder.Append("    public void ")
            .Append(SanitizeIdentifier(member.Name))
            .Append("(dynamic? ")
            .Append(SanitizeIdentifier(parameter))
            .AppendLine(")");
        EmitBody(builder, member.Body, isConstructor: true);
    }

    private static void EmitBody(StringBuilder builder, string body, bool isConstructor)
    {
        builder.AppendLine("    {");

        foreach (var statement in SplitStatements(body))
        {
            var translated = TranslateStatement(statement);
            if (!string.IsNullOrWhiteSpace(translated))
            {
                builder.Append("        ").AppendLine(translated);
            }
        }

        if (!isConstructor && !ContainsReturn(body))
        {
            builder.AppendLine("        return null;");
        }

        builder.AppendLine("    }");
    }

    private static string TranslateStatement(string statement)
    {
        var text = statement.Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        text = text.TrimEnd(';').Trim();
        if (text.StartsWith("let ", StringComparison.Ordinal) ||
            text.StartsWith("const ", StringComparison.Ordinal) ||
            text.StartsWith("var ", StringComparison.Ordinal))
        {
            var firstSpace = text.IndexOf(' ', StringComparison.Ordinal);
            return "dynamic? " + RewriteExpression(text[(firstSpace + 1)..]) + ";";
        }

        if (text.StartsWith("return ", StringComparison.Ordinal))
        {
            return "return " + RewriteExpression(text[7..]) + ";";
        }

        if (text == "return")
        {
            return "return null;";
        }

        return RewriteExpression(text) + ";";
    }

    private static string? ExtractReturnedExpression(string body)
    {
        foreach (var statement in SplitStatements(body))
        {
            var text = statement.Trim().TrimEnd(';').Trim();
            if (text.StartsWith("return ", StringComparison.Ordinal))
            {
                return text[7..];
            }
        }

        return null;
    }

    private static string RewriteExpression(string expression)
    {
        return CSharpIntrinsicRewriter.Rewrite(expression)
            .Replace("===", "==", StringComparison.Ordinal)
            .Replace("!==", "!=", StringComparison.Ordinal);
    }

    private static IEnumerable<string> SplitStatements(string body)
    {
        var start = 0;
        var depth = 0;
        var inString = false;
        var quote = '\0';

        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];

            if (inString)
            {
                if (c == quote && !IsEscaped(body, i))
                {
                    inString = false;
                }

                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                inString = true;
                quote = c;
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
            }

            if (c is ')' or ']' or '}')
            {
                depth--;
            }

            if (c == ';' && depth == 0)
            {
                yield return body[start..(i + 1)];
                start = i + 1;
            }
        }

        var tail = body[start..].Trim();
        if (tail.Length > 0)
        {
            foreach (var statement in SplitNewLineStatements(tail))
            {
                yield return statement;
            }
        }
    }

    private static IEnumerable<string> SplitNewLineStatements(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }

    private static bool ContainsReturn(string body) =>
        Regex.IsMatch(body, @"(^|[;\s])return(\s|;|$)");

    private static IReadOnlyList<string> SplitParameters(string parameters) =>
        parameters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string SanitizeQualifiedName(string value) =>
        string.Join(".", value.Split('.').Select(SanitizeIdentifier));

    private static string SanitizeIdentifier(string value) => value switch
    {
        "class" or "namespace" or "public" or "private" or "protected" or "internal" or "static" or "void" or "double" or "string" or "bool" or "object" or "return" => "@" + value,
        _ => value
    };

    private static bool IsEscaped(string source, int index)
    {
        var slashCount = 0;
        for (var i = index - 1; i >= 0 && source[i] == '\\'; i--)
        {
            slashCount++;
        }

        return slashCount % 2 != 0;
    }

    private sealed record JavaScriptClassMember(
        string Name,
        string Parameters,
        string Body,
        bool IsStatic,
        string Kind);
}

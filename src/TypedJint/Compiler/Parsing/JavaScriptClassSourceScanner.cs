using System;
using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;

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
    public static IReadOnlyList<JavaScriptClassSource> Scan(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var result = new List<JavaScriptClassSource>();
        try
        {
            var parser = new Parser();
            var program = parser.ParseScript(source);

            var visitor = new ClassScannerVisitor(source);
            visitor.Visit(program);
            return visitor.Classes;
        }
        catch
        {
            return Array.Empty<JavaScriptClassSource>();
        }
    }

    private sealed class ClassScannerVisitor : AstVisitor
    {
        private readonly string _source;
        public List<JavaScriptClassSource> Classes { get; } = new();

        public ClassScannerVisitor(string source)
        {
            _source = source;
        }

        protected override object? VisitClassDeclaration(ClassDeclaration node)
        {
            var name = node.Id?.Name ?? string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                return base.VisitClassDeclaration(node);
            }

            var baseName = FormatSuperClass(node.SuperClass);
            var start = node.Range.Start;
            var end = node.Range.End;
            var classSource = _source.Substring(start, end - start);
            
            // Extract body text inside braces
            var bodyStart = node.Body.Range.Start + 1;
            var bodyLength = node.Body.Range.End - node.Body.Range.Start - 2;
            var body = bodyLength > 0 ? _source.Substring(bodyStart, bodyLength) : string.Empty;

            Classes.Add(new JavaScriptClassSource(
                name,
                baseName,
                classSource,
                body,
                start,
                end));

            return base.VisitClassDeclaration(node);
        }

        private static string? FormatSuperClass(Expression? superClass)
        {
            if (superClass == null) return null;
            return superClass switch
            {
                Identifier id => id.Name,
                MemberExpression mem => FormatMemberExpression(mem),
                _ => superClass.ToString()
            };
        }

        private static string FormatMemberExpression(MemberExpression mem)
        {
            var obj = mem.Object switch
            {
                Identifier id => id.Name,
                MemberExpression nested => FormatMemberExpression(nested),
                _ => mem.Object.ToString()
            };
            var prop = mem.Property is Identifier propId ? propId.Name : mem.Property.ToString();
            return $"{obj}.{prop}";
        }
    }
}

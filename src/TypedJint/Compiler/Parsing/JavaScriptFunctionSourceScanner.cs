using System;
using System.Collections.Generic;
using System.Linq;
using Acornima;
using Acornima.Ast;

namespace TypedJint;

public sealed record JavaScriptFunctionSource(
    string Name,
    string Source,
    bool HasJsDoc,
    int Start,
    int End);

public static class JavaScriptFunctionSourceScanner
{
    public static IReadOnlyList<JavaScriptFunctionSource> Scan(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var result = new List<JavaScriptFunctionSource>();
        try
        {
            var comments = new List<Comment>();
            var options = new ParserOptions
            {
                OnComment = delegate (in Comment comment) { comments.Add(comment); },
                Tolerant = true
            };
            var parser = new Parser(options);
            var program = parser.ParseScript(source);

            var visitor = new FunctionScannerVisitor(source, comments);
            visitor.Visit(program);
            return visitor.Functions;
        }
        catch
        {
            return Array.Empty<JavaScriptFunctionSource>();
        }
    }

    private sealed class FunctionScannerVisitor : AstVisitor
    {
        private readonly string _source;
        private readonly List<Comment> _comments;
        public List<JavaScriptFunctionSource> Functions { get; } = new();

        public FunctionScannerVisitor(string source, List<Comment> comments)
        {
            _source = source;
            _comments = comments;
        }

        protected override object? VisitFunctionDeclaration(FunctionDeclaration node)
        {
            var name = node.Id?.Name ?? string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                return base.VisitFunctionDeclaration(node);
            }

            var jsDoc = FindJSDocForNode(node);
            var start = jsDoc != null ? jsDoc.Value.Start : node.Range.Start;
            var end = node.Range.End;
            var funcSource = _source.Substring(start, end - start);

            Functions.Add(new JavaScriptFunctionSource(
                name,
                funcSource,
                jsDoc != null,
                start,
                end));

            return base.VisitFunctionDeclaration(node);
        }

        private Comment? FindJSDocForNode(Node node)
        {
            foreach (var comment in _comments)
            {
                var commentText = _source.Substring(comment.ContentRange.Start, comment.ContentRange.End - comment.ContentRange.Start);
                if (comment.Kind == CommentKind.Block && commentText.StartsWith("*") && comment.End <= node.Start)
                {
                    var isAdjacent = true;
                    for (int i = comment.End; i < node.Start; i++)
                    {
                        if (!char.IsWhiteSpace(_source[i]))
                        {
                            isAdjacent = false;
                            break;
                        }
                    }
                    if (isAdjacent)
                    {
                        return comment;
                    }
                }
            }
            return null;
        }
    }
}

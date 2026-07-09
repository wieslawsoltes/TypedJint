using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;
using AstExpression = Acornima.Ast.Expression;

namespace TypedJint;

public static class SimpleJsParser
{
    private static readonly Regex ParamRegex = new(@"@param\s*\{\s*(?<type>[^}]+)\s*\}\s*(?<name>[A-Za-z_$][A-Za-z0-9_$]*)", RegexOptions.Compiled);
    private static readonly Regex ReturnRegex = new(@"@returns?\s*\{\s*(?<type>[^}]+)\s*\}", RegexOptions.Compiled);

    public static IReadOnlyList<JsFunctionDeclaration> ParseFunctions(string source)
    {
        var result = new List<JsFunctionDeclaration>();
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
            
            var walker = new FunctionVisitor(source, comments);
            walker.Visit(program);
            return walker.Functions;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ParseFunctions ERROR] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return Array.Empty<JsFunctionDeclaration>();
        }
    }

    public static IReadOnlyList<JsStatement> ParseStatements(string source)
    {
        try
        {
            var parser = new Parser();
            var program = parser.ParseScript(source);
            return program.Body.Select(MapStatement).ToList();
        }
        catch
        {
            return Array.Empty<JsStatement>();
        }
    }

    internal static JsExpression ParseExpression(string expression)
    {
        var parser = new Parser();
        var expr = parser.ParseExpression(expression);
        return MapExpression(expr);
    }

    internal static SourceSpan ComputeSpan(string source, int start, int length)
    {
        var line = 1; var column = 1;
        for (var i = 0; i < start; i++) { if (source[i] == '\n') { line++; column = 1; } else column++; }
        return new SourceSpan(start, length, line, column);
    }

    private static FunctionAnnotation? ParseAnnotation(Comment? comment, string source)
    {
        if (comment == null) return null;
        var doc = source.Substring(comment.Value.ContentRange.Start, comment.Value.ContentRange.End - comment.Value.ContentRange.Start);
        if (string.IsNullOrWhiteSpace(doc)) return null;
        var parameters = new Dictionary<string, JsStaticType>(StringComparer.Ordinal);
        foreach (Match match in ParamRegex.Matches(doc)) parameters[match.Groups["name"].Value] = JsStaticType.Parse(match.Groups["type"].Value);
        var returnMatch = ReturnRegex.Match(doc);
        var returnType = returnMatch.Success ? JsStaticType.Parse(returnMatch.Groups["type"].Value) : JsStaticType.Object;
        return new FunctionAnnotation(parameters, returnType);
    }

    private static JsStatement MapStatement(Statement stmt)
    {
        return stmt switch
        {
            BlockStatement block => new JsBlockStatement(block.Body.Select(MapStatement).ToList()),
            VariableDeclaration varDecl => MapVariableDeclaration(varDecl),
            ReturnStatement ret => new JsReturnStatement(ret.Argument != null ? MapExpression(ret.Argument) : null),
            ExpressionStatement exprStmt => MapExpressionStatement(exprStmt),
            IfStatement ifStmt => new JsIfStatement(MapExpression(ifStmt.Test), MapStatement(ifStmt.Consequent), ifStmt.Alternate != null ? MapStatement(ifStmt.Alternate) : null),
            WhileStatement whileStmt => new JsWhileStatement(MapExpression(whileStmt.Test), MapStatement(whileStmt.Body)),
            ForStatement forStmt => MapForStatement(forStmt),
            BreakStatement => new JsBreakStatement(),
            ContinueStatement => new JsContinueStatement(),
            ImportDeclaration => new JsBlockStatement(Array.Empty<JsStatement>()),
            ExportNamedDeclaration => new JsBlockStatement(Array.Empty<JsStatement>()),
            ExportDefaultDeclaration => new JsBlockStatement(Array.Empty<JsStatement>()),
            ExportAllDeclaration => new JsBlockStatement(Array.Empty<JsStatement>()),
            ThrowStatement throwStmt => new JsThrowStatement(MapExpression(throwStmt.Argument)),
            TryStatement tryStmt => MapTryStatement(tryStmt),
            SwitchStatement switchStmt => MapSwitchStatement(switchStmt),
            _ => throw new NotSupportedException($"Unsupported statement: {stmt.Type}")
        };
    }

    private static JsStatement MapTryStatement(TryStatement tryStmt)
    {
        var block = MapStatement(tryStmt.Block);
        string? handlerParam = tryStmt.Handler?.Param is Identifier id ? id.Name : null;
        JsStatement? handlerBlock = tryStmt.Handler != null ? MapStatement(tryStmt.Handler.Body) : null;
        JsStatement? finalizer = tryStmt.Finalizer != null ? MapStatement(tryStmt.Finalizer) : null;
        return new JsTryStatement(block, handlerParam, handlerBlock, finalizer);
    }

    private static JsStatement MapSwitchStatement(SwitchStatement switchStmt)
    {
        var discriminant = MapExpression(switchStmt.Discriminant);
        var cases = new List<JsSwitchCase>();
        foreach (var c in switchStmt.Cases)
        {
            var test = c.Test != null ? MapExpression(c.Test) : null;
            var consequent = c.Consequent.Select(MapStatement).ToList();
            cases.Add(new JsSwitchCase(test, consequent));
        }
        return new JsSwitchStatement(discriminant, cases);
    }

    private static JsStatement MapVariableDeclaration(VariableDeclaration varDecl)
    {
        if (varDecl.Declarations.Count == 0)
        {
            return new JsBlockStatement(Array.Empty<JsStatement>());
        }
        if (varDecl.Declarations.Count == 1)
        {
            var decl = varDecl.Declarations[0];
            var name = decl.Id is Identifier id ? id.Name : throw new NotSupportedException("Destructuring variable declaration not supported natively");
            var init = decl.Init != null ? MapExpression(decl.Init) : new JsLiteralExpression(null);
            return new JsVariableStatement(name, init);
        }
        var list = new List<JsStatement>();
        foreach (var decl in varDecl.Declarations)
        {
            var name = decl.Id is Identifier id ? id.Name : throw new NotSupportedException("Destructuring variable declaration not supported natively");
            var init = decl.Init != null ? MapExpression(decl.Init) : new JsLiteralExpression(null);
            list.Add(new JsVariableStatement(name, init));
        }
        return new JsBlockStatement(list);
    }

    private static JsStatement MapExpressionStatement(ExpressionStatement exprStmt)
    {
        return new JsExpressionStatement(MapExpression(exprStmt.Expression));
    }

    private static string GetAssignmentOperatorString(Operator op)
    {
        return op switch
        {
            Operator.Assignment => "=",
            Operator.AdditionAssignment => "+=",
            Operator.SubtractionAssignment => "-=",
            Operator.MultiplicationAssignment => "*=",
            Operator.DivisionAssignment => "/=",
            Operator.RemainderAssignment => "%=",
            Operator.BitwiseAndAssignment => "&=",
            Operator.BitwiseOrAssignment => "|=",
            Operator.BitwiseXorAssignment => "^=",
            Operator.LeftShiftAssignment => "<<=",
            Operator.RightShiftAssignment => ">>=",
            Operator.UnsignedRightShiftAssignment => ">>>=",
            _ => throw new NotSupportedException($"Unsupported assignment operator: {op}")
        };
    }

    private static string GetBinaryOperatorString(Operator op)
    {
        return op switch
        {
            Operator.Addition => "+",
            Operator.Subtraction => "-",
            Operator.Multiplication => "*",
            Operator.Division => "/",
            Operator.Remainder => "%",
            Operator.LessThan => "<",
            Operator.LessThanOrEqual => "<=",
            Operator.GreaterThan => ">",
            Operator.GreaterThanOrEqual => ">=",
            Operator.Equality => "==",
            Operator.Inequality => "!=",
            Operator.StrictEquality => "===",
            Operator.StrictInequality => "!==",
            Operator.LogicalAnd => "&&",
            Operator.LogicalOr => "||",
            Operator.NullishCoalescing => "??",
            Operator.BitwiseAnd => "&",
            Operator.BitwiseOr => "|",
            Operator.BitwiseXor => "^",
            Operator.LeftShift => "<<",
            Operator.RightShift => ">>",
            Operator.UnsignedRightShift => ">>>",
            _ => throw new NotSupportedException($"Unsupported binary operator: {op}")
        };
    }

    private static string GetUnaryOperatorString(Operator op)
    {
        return op switch
        {
            Operator.UnaryPlus => "+",
            Operator.UnaryNegation => "-",
            Operator.LogicalNot => "!",
            Operator.BitwiseNot => "~",
            _ => throw new NotSupportedException($"Unsupported unary operator: {op}")
        };
    }

    private static string GetUpdateOperatorString(Operator op)
    {
        return op switch
        {
            Operator.Increment => "++",
            Operator.Decrement => "--",
            _ => throw new NotSupportedException($"Unsupported update operator: {op}")
        };
    }

    private static JsExpression MapExpression(AstExpression? expr)
    {
        if (expr is null)
        {
            return new JsLiteralExpression(null);
        }

        return expr switch
        {
            Literal lit => MapLiteral(lit),
            Identifier id => new JsIdentifierExpression(id.Name),
            Acornima.Ast.MemberExpression member => MapMemberExpression(member),
            CallExpression call => new JsCallExpression(MapExpression(call.Callee), call.Arguments.Select(MapExpression).ToList()),
            LogicalExpression log => new JsBinaryExpression(GetBinaryOperatorString(log.Operator), MapExpression(log.Left), MapExpression(log.Right)),
            Acornima.Ast.BinaryExpression bin => new JsBinaryExpression(GetBinaryOperatorString(bin.Operator), MapExpression(bin.Left), MapExpression(bin.Right)),
            UpdateExpression upd => new JsUpdateExpression(MapExpression(upd.Argument), GetUpdateOperatorString(upd.Operator), upd.Prefix),
            Acornima.Ast.UnaryExpression un => new JsUnaryExpression(GetUnaryOperatorString(un.Operator), MapExpression(un.Argument)),
            ArrayExpression arr => new JsArrayExpression(arr.Elements.Select(MapExpression).ToList()),
            Acornima.Ast.ConditionalExpression cond => new JsConditionalExpression(MapExpression(cond.Test), MapExpression(cond.Consequent), MapExpression(cond.Alternate)),
            FunctionExpression funcExpr => MapFunctionExpression(funcExpr),
            ArrowFunctionExpression arrowExpr => MapArrowFunctionExpression(arrowExpr),
            ObjectExpression objExpr => MapObjectExpression(objExpr),
            Acornima.Ast.NewExpression newExpr => MapNewExpression(newExpr),
            ThisExpression => new JsThisExpression(),
            TemplateLiteral tempLit => MapTemplateLiteral(tempLit),
            ParenthesizedExpression paren => MapExpression(paren.Expression),
            AssignmentExpression assign => new JsAssignmentExpression(MapExpression((AstExpression)assign.Left), GetAssignmentOperatorString(assign.Operator), MapExpression(assign.Right)),
            _ => throw new NotSupportedException($"Unsupported expression: {expr.Type}")
        };
    }

    private static JsExpression MapObjectExpression(ObjectExpression objExpr)
    {
        var properties = new Dictionary<string, JsExpression>(StringComparer.Ordinal);
        foreach (var prop in objExpr.Properties)
        {
            if (prop is Property p)
            {
                string key;
                if (p.Key is Identifier id) key = id.Name;
                else if (p.Key is Literal lit) key = Convert.ToString(lit.Value) ?? string.Empty;
                else key = p.Key.ToString() ?? string.Empty;

                properties[key] = MapExpression(p.Value as AstExpression);
            }
        }
        return new JsObjectExpression(properties);
    }

    private static JsExpression MapNewExpression(Acornima.Ast.NewExpression newExpr)
    {
        string callee = newExpr.Callee is Identifier id ? id.Name : newExpr.Callee.ToString() ?? "Object";
        var arguments = newExpr.Arguments.Select(arg => MapExpression(arg as AstExpression)).ToList();
        return new JsNewExpression(callee, arguments);
    }

    private static JsExpression MapTemplateLiteral(TemplateLiteral tempLit)
    {
        var quasis = tempLit.Quasis.Select(q => q.Value.Raw ?? string.Empty).ToList();
        var expressions = tempLit.Expressions.Select(expr => MapExpression(expr as AstExpression)).ToList();
        return new JsTemplateLiteralExpression(quasis, expressions);
    }

    private static JsExpression MapFunctionExpression(FunctionExpression funcExpr)
    {
        var parameters = funcExpr.Params.Select(p => p is Identifier id ? id.Name : (p.ToString() ?? "")).ToList();
        var body = new List<JsStatement>();
        if (funcExpr.Body != null)
        {
            foreach (var stmt in funcExpr.Body.Body)
            {
                body.Add(MapStatement(stmt));
            }
        }
        return new JsFunctionExpression(parameters, body);
    }

    private static JsExpression MapArrowFunctionExpression(ArrowFunctionExpression arrowExpr)
    {
        var parameters = arrowExpr.Params.Select(p => p is Identifier id ? id.Name : (p.ToString() ?? "")).ToList();
        var body = new List<JsStatement>();
        if (arrowExpr.Body is BlockStatement block)
        {
            foreach (var stmt in block.Body)
            {
                body.Add(MapStatement(stmt));
            }
        }
        else if (arrowExpr.Body is AstExpression expr)
        {
            body.Add(new JsReturnStatement(MapExpression(expr)));
        }
        return new JsArrowFunctionExpression(parameters, body);
    }

    private static JsExpression MapLiteral(Literal lit)
    {
        return lit switch
        {
            NullLiteral => new JsLiteralExpression(null),
            BooleanLiteral boolean => new JsLiteralExpression(boolean.Value),
            NumericLiteral numeric => new JsLiteralExpression(numeric.Value),
            StringLiteral stringLit => new JsLiteralExpression(stringLit.Value),
            _ => new JsLiteralExpression(lit.Value)
        };
    }

    private static JsExpression MapMemberExpression(Acornima.Ast.MemberExpression member)
    {
        var target = MapExpression(member.Object);
        if (member.Computed)
        {
            var property = MapExpression(member.Property);
            return new JsIndexExpression(target, property);
        }
        else
        {
            var propName = member.Property is Identifier id ? id.Name : throw new NotSupportedException("Non-identifier static member access not supported");
            return new JsMemberExpression(target, propName);
        }
    }

    private static JsStatement MapForStatement(ForStatement forStmt)
    {
        JsStatement? init = null;
        if (forStmt.Init != null)
        {
            init = forStmt.Init switch
            {
                VariableDeclaration varDecl => MapVariableDeclaration(varDecl),
                AstExpression expr => new JsExpressionStatement(MapExpression(expr)),
                _ => throw new NotSupportedException($"Unsupported for-init type: {forStmt.Init.Type}")
            };
        }
        JsExpression? test = forStmt.Test != null ? MapExpression(forStmt.Test) : null;
        JsStatement? update = null;
        if (forStmt.Update != null)
        {
            update = new JsExpressionStatement(MapExpression(forStmt.Update));
        }
        JsStatement body = MapStatement(forStmt.Body);
        return new JsForStatement(init, test, update, body);
    }

    private sealed class FunctionVisitor : AstVisitor
    {
        private readonly string _source;
        private readonly List<Comment> _comments;
        public List<JsFunctionDeclaration> Functions { get; } = new();

        public FunctionVisitor(string source, List<Comment> comments)
        {
            _source = source;
            _comments = comments;
        }

        protected override object? VisitFunctionDeclaration(FunctionDeclaration node)
        {
            var name = node.Id?.Name ?? string.Empty;
            var parameters = node.Params.Select(p => p is Identifier id ? id.Name : (p.ToString() ?? "")).ToList();
            var jsDoc = FindJSDocForNode(node);
            var annotation = ParseAnnotation(jsDoc, _source) ?? JavaScriptTypeInferenceEngine.Infer(node) with { IsInferred = true };
            
            var body = new List<JsStatement>();
            if (node.Body != null)
            {
                foreach (var stmt in node.Body.Body)
                {
                    body.Add(MapStatement(stmt));
                }
            }
            
            var start = node.Range.Start;
            var end = node.Range.End;
            var length = end - start;
            var span = ComputeSpan(_source, start, length);
            
            Functions.Add(new JsFunctionDeclaration(name, parameters, annotation, body, span));
            
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

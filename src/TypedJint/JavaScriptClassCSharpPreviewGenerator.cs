using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Acornima;
using Acornima.Ast;

namespace TypedJint;

public static class JavaScriptClassCSharpPreviewGenerator
{
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
        try
        {
            var parser = new Parser();
            var program = parser.ParseScript(cls.Source);
            var classDecl = program.Body.OfType<ClassDeclaration>().FirstOrDefault();
            if (classDecl == null) return;

            var properties = FindThisProperties(classDecl);

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

            if (properties.Count > 0)
            {
                builder.AppendLine();
            }

            foreach (var element in classDecl.Body.Body)
            {
                if (element is MethodDefinition method)
                {
                    EmitMethod(builder, cls.Name, method);
                    builder.AppendLine();
                }
                else if (element is PropertyDefinition field)
                {
                    EmitField(builder, field);
                    builder.AppendLine();
                }
            }

            builder.AppendLine("}");
        }
        catch (Exception ex)
        {
            builder.AppendLine($"// error parsing class {cls.Name}: {ex.Message}");
        }
    }

    private static HashSet<string> FindThisProperties(ClassDeclaration classDecl)
    {
        var properties = new HashSet<string>(StringComparer.Ordinal);
        var constructor = classDecl.Body.Body
            .OfType<MethodDefinition>()
            .FirstOrDefault(m => m.Kind == PropertyKind.Constructor);

        if (constructor != null && constructor.Value?.Body != null)
        {
            var visitor = new ThisPropertyVisitor();
            visitor.Visit(constructor.Value.Body);
            foreach (var prop in visitor.Properties)
            {
                properties.Add(prop);
            }
        }
        return properties;
    }

    private sealed class ThisPropertyVisitor : AstVisitor
    {
        public HashSet<string> Properties { get; } = new(StringComparer.Ordinal);

        protected override object? VisitAssignmentExpression(AssignmentExpression node)
        {
            if (node.Left is MemberExpression mem && mem.Object is ThisExpression && mem.Property is Identifier id)
            {
                Properties.Add(id.Name);
            }
            return base.VisitAssignmentExpression(node);
        }
    }

    private static void EmitField(StringBuilder builder, PropertyDefinition field)
    {
        var name = field.Key is Identifier id ? id.Name : field.Key.ToString();
        builder.Append(field.Static ? "    public static dynamic? " : "    public dynamic? ")
            .Append(SanitizeIdentifier(name))
            .AppendLine(" { get; set; }");
    }

    private static void EmitMethod(StringBuilder builder, string className, MethodDefinition method)
    {
        var methodValue = method.Value;
        if (methodValue == null) return;

        var name = method.Key is Identifier id ? id.Name : method.Key.ToString();
        var parameters = methodValue.Params.Select(p => "dynamic? " + SanitizeIdentifier(p is Identifier paramId ? paramId.Name : p.ToString())).ToArray();

        if (method.Kind == PropertyKind.Get)
        {
            builder.Append("    public dynamic? ")
                .Append(SanitizeIdentifier(name))
                .AppendLine(" => ");
            var returnedExpr = ExtractReturnedExpression(methodValue.Body) ?? "null";
            builder.Append("        ").Append(returnedExpr).AppendLine(";");
        }
        else if (method.Kind == PropertyKind.Set)
        {
            var parameter = parameters.FirstOrDefault() ?? "dynamic? value";
            builder.Append("    public void ")
                .Append(SanitizeIdentifier(name))
                .Append("(")
                .Append(parameter)
                .AppendLine(")");
            EmitBody(builder, methodValue.Body, isConstructor: false);
        }
        else if (method.Kind == PropertyKind.Constructor)
        {
            builder.Append("    public ")
                .Append(SanitizeIdentifier(className))
                .Append("(")
                .Append(string.Join(", ", parameters))
                .AppendLine(")");
            EmitBody(builder, methodValue.Body, isConstructor: true);
        }
        else
        {
            builder.Append(method.Static ? "    public static dynamic? " : "    public dynamic? ")
                .Append(SanitizeIdentifier(name))
                .Append("(")
                .Append(string.Join(", ", parameters))
                .AppendLine(")");
            EmitBody(builder, methodValue.Body, isConstructor: false);
        }
    }

    private static void EmitBody(StringBuilder builder, FunctionBody? body, bool isConstructor)
    {
        builder.AppendLine("    {");
        if (body != null)
        {
            var visitor = new PreviewBodyVisitor();
            visitor.Visit(body);
            foreach (var line in visitor.Lines)
            {
                builder.Append("        ").AppendLine(line);
            }
        }
        if (!isConstructor && (body == null || !HasReturn(body)))
        {
            builder.AppendLine("        return null;");
        }
        builder.AppendLine("    }");
    }

    private static bool HasReturn(FunctionBody body)
    {
        var checker = new ReturnChecker();
        checker.Visit(body);
        return checker.Found;
    }

    private sealed class ReturnChecker : AstVisitor
    {
        public bool Found { get; private set; }
        protected override object? VisitReturnStatement(ReturnStatement node)
        {
            Found = true;
            return base.VisitReturnStatement(node);
        }
    }

    private sealed class PreviewBodyVisitor : AstVisitor
    {
        public List<string> Lines { get; } = new();

        protected override object? VisitVariableDeclaration(VariableDeclaration node)
        {
            foreach (var decl in node.Declarations)
            {
                var name = decl.Id is Identifier id ? id.Name : decl.Id.ToString();
                var init = decl.Init != null ? FormatExpression(decl.Init) : "null";
                Lines.Add($"dynamic? {SanitizeIdentifier(name)} = {init};");
            }
            return null;
        }

        protected override object? VisitReturnStatement(ReturnStatement node)
        {
            var expr = node.Argument != null ? FormatExpression(node.Argument) : "null";
            Lines.Add($"return {expr};");
            return null;
        }

        protected override object? VisitExpressionStatement(ExpressionStatement node)
        {
            Lines.Add(FormatExpression(node.Expression) + ";");
            return null;
        }

        protected override object? VisitIfStatement(IfStatement node)
        {
            var test = FormatExpression(node.Test);
            Lines.Add($"if ({test})");
            Lines.Add("{");
            var visitor = new PreviewBodyVisitor();
            visitor.Visit(node.Consequent);
            foreach (var line in visitor.Lines) Lines.Add("    " + line);
            Lines.Add("}");
            if (node.Alternate != null)
            {
                Lines.Add("else");
                Lines.Add("{");
                var altVisitor = new PreviewBodyVisitor();
                altVisitor.Visit(node.Alternate);
                foreach (var line in altVisitor.Lines) Lines.Add("    " + line);
                Lines.Add("}");
            }
            return null;
        }

        protected override object? VisitWhileStatement(WhileStatement node)
        {
            var test = FormatExpression(node.Test);
            Lines.Add($"while ({test})");
            Lines.Add("{");
            var bodyVisitor = new PreviewBodyVisitor();
            bodyVisitor.Visit(node.Body);
            foreach (var line in bodyVisitor.Lines) Lines.Add("    " + line);
            Lines.Add("}");
            return null;
        }

        protected override object? VisitForStatement(ForStatement node)
        {
            string init = "";
            if (node.Init != null)
            {
                if (node.Init is VariableDeclaration varDecl)
                {
                    init = string.Join(", ", varDecl.Declarations.Select(d => "dynamic? " + SanitizeIdentifier(d.Id is Identifier id ? id.Name : d.Id.ToString()) + " = " + (d.Init != null ? FormatExpression(d.Init) : "null")));
                }
                else
                {
                    init = FormatExpression((Expression)node.Init);
                }
            }
            var test = node.Test != null ? FormatExpression(node.Test) : "";
            var update = node.Update != null ? FormatExpression(node.Update) : "";
            Lines.Add($"for ({init}; {test}; {update})");
            Lines.Add("{");
            var bodyVisitor = new PreviewBodyVisitor();
            bodyVisitor.Visit(node.Body);
            foreach (var line in bodyVisitor.Lines) Lines.Add("    " + line);
            Lines.Add("}");
            return null;
        }

        protected override object? VisitBreakStatement(BreakStatement node)
        {
            Lines.Add("break;");
            return null;
        }

        protected override object? VisitContinueStatement(ContinueStatement node)
        {
            Lines.Add("continue;");
            return null;
        }
    }

    private static string FormatExpression(Node? expr)
    {
        if (expr is null) return "null";
        return expr switch
        {
            Literal lit => FormatLiteral(lit),
            Identifier id => SanitizeIdentifier(id.Name),
            ThisExpression => "this",
            MemberExpression mem => FormatMemberExpression(mem),
            AssignmentExpression assign => $"{FormatExpression(assign.Left)} {MapOperator(assign.Operator)} {FormatExpression(assign.Right)}",
            CallExpression call => $"{FormatExpression(call.Callee)}({string.Join(", ", call.Arguments.Select(FormatExpression))})",
            LogicalExpression log => $"({FormatExpression(log.Left)} {MapOperator(log.Operator)} {FormatExpression(log.Right)})",
            BinaryExpression bin => $"({FormatExpression(bin.Left)} {MapOperator(bin.Operator)} {FormatExpression(bin.Right)})",
            UpdateExpression upd => upd.Prefix ? $"{MapUpdateOperator(upd.Operator)}{FormatExpression(upd.Argument)}" : $"{FormatExpression(upd.Argument)}{MapUpdateOperator(upd.Operator)}",
            UnaryExpression un => $"({MapUnaryOperator(un.Operator)}{FormatExpression(un.Argument)})",
            ArrayExpression arr => $"new[] {{ {string.Join(", ", arr.Elements.Select(FormatExpression))} }}",
            ConditionalExpression cond => $"({FormatExpression(cond.Test)} ? {FormatExpression(cond.Consequent)} : {FormatExpression(cond.Alternate)})",
            NewExpression n => $"new {FormatExpression(n.Callee)}({string.Join(", ", n.Arguments.Select(FormatExpression))})",
            TemplateLiteral t => FormatTemplateLiteral(t),
            ObjectExpression obj => FormatObjectExpression(obj),
            ArrowFunctionExpression arrow => FormatArrowFunction(arrow),
            _ => expr.ToString() ?? "null"
        };
    }

    private static string MapOperator(Operator op) => op switch
    {
        Operator.StrictEquality or Operator.Equality => "==",
        Operator.StrictInequality or Operator.Inequality => "!=",
        Operator.Addition => "+",
        Operator.Subtraction => "-",
        Operator.Multiplication => "*",
        Operator.Division => "/",
        Operator.Remainder => "%",
        Operator.LessThan => "<",
        Operator.LessThanOrEqual => "<=",
        Operator.GreaterThan => ">",
        Operator.GreaterThanOrEqual => ">=",
        Operator.LogicalAnd => "&&",
        Operator.LogicalOr => "||",
        Operator.Assignment => "=",
        Operator.AdditionAssignment => "+=",
        Operator.SubtractionAssignment => "-=",
        Operator.MultiplicationAssignment => "*=",
        Operator.DivisionAssignment => "/=",
        _ => op.ToString()
    };

    private static string MapUnaryOperator(Operator op) => op switch
    {
        Operator.UnaryPlus => "+",
        Operator.UnaryNegation => "-",
        Operator.LogicalNot => "!",
        _ => op.ToString()
    };

    private static string MapUpdateOperator(Operator op) => op switch
    {
        Operator.Increment => "++",
        Operator.Decrement => "--",
        _ => op.ToString()
    };

    private static string FormatLiteral(Literal lit)
    {
        if (lit is NullLiteral) return "null";
        if (lit is BooleanLiteral b) return b.Value ? "true" : "false";
        if (lit is StringLiteral s) return "\"" + s.Value.Replace("\"", "\\\"") + "\"";
        if (lit is NumericLiteral n) return n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return lit.Value?.ToString() ?? "null";
    }

    private static string FormatTemplateLiteral(TemplateLiteral t)
    {
        var builder = new StringBuilder();
        builder.Append("$\"");
        for (int i = 0; i < t.Quasis.Count; i++)
        {
            var quasi = t.Quasis[i];
            builder.Append(quasi.Value.Raw);
            if (i < t.Expressions.Count)
            {
                builder.Append("{").Append(FormatExpression(t.Expressions[i])).Append("}");
            }
        }
        builder.Append("\"");
        return builder.ToString();
    }

    private static string FormatObjectExpression(ObjectExpression obj)
    {
        return "new { " + string.Join(", ", obj.Properties.Select(p =>
        {
            if (p is Property prop)
            {
                var key = prop.Key is Identifier id ? id.Name : prop.Key.ToString();
                return $"{SanitizeIdentifier(key)} = {FormatExpression((Expression)prop.Value)}";
            }
            return p.ToString();
        })) + " }";
    }

    private static string FormatArrowFunction(ArrowFunctionExpression arrow)
    {
        var parameters = string.Join(", ", arrow.Params.Select(p => p is Identifier id ? id.Name : p.ToString()));
        var body = arrow.Body switch
        {
            Expression expr => FormatExpression(expr),
            _ => arrow.Body.ToString()
        };
        return $"({parameters}) => {body}";
    }

    private static string FormatMemberExpression(MemberExpression mem)
    {
        var obj = mem.Object switch
        {
            Identifier id => id.Name,
            MemberExpression nested => FormatMemberExpression(nested),
            ThisExpression => "this",
            _ => mem.Object.ToString()
        };
        var prop = mem.Property is Identifier propId ? propId.Name : mem.Property.ToString();
        return mem.Computed ? $"{obj}[{prop}]" : $"{obj}.{prop}";
    }

    private static string? ExtractReturnedExpression(FunctionBody? body)
    {
        if (body == null) return null;
        foreach (var stmt in body.Body)
        {
            if (stmt is ReturnStatement ret)
            {
                return ret.Argument != null ? FormatExpression(ret.Argument) : "null";
            }
        }
        return null;
    }

    private static string SanitizeQualifiedName(string value) =>
        string.Join(".", value.Split('.').Select(SanitizeIdentifier));

    private static string SanitizeIdentifier(string? value) => value switch
    {
        null => string.Empty,
        "class" or "namespace" or "public" or "private" or "protected" or "internal" or "static" or "void" or "double" or "string" or "bool" or "object" or "return" => "@" + value,
        _ => value
    };
}

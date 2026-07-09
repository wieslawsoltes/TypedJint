using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TypedJint.Runtime;

namespace TypedJint;

public static class TypedJintTranspiler
{
    [ThreadStatic]
    private static HashSet<string>? _currentStaticVars;
    [ThreadStatic]
    private static HashSet<string>? _currentStaticParameters;

    private static HashSet<string> CollectStaticVariables(JsFunctionDeclaration function)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (function.Annotation != null)
        {
            foreach (var kv in function.Annotation.Parameters)
            {
                if (kv.Value.Kind == JsStaticTypeKind.Number || kv.Value.Kind == JsStaticTypeKind.String || kv.Value.Kind == JsStaticTypeKind.Boolean)
                {
                    set.Add(kv.Key);
                }
            }
        }
        
        foreach (var stmt in function.Body)
        {
            CollectVariablesInStatement(stmt, set);
        }

        var dynamicVars = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stmt in function.Body)
        {
            ScanDynamicAssignments(stmt, set, dynamicVars);
        }
        set.ExceptWith(dynamicVars);
        return set;
    }

    private static void ScanDynamicAssignments(JsStatement stmt, HashSet<string> staticSet, HashSet<string> dynamicVars)
    {
        switch (stmt)
        {
            case JsVariableStatement variable:
                ScanAssignmentsInExpression(variable.Initializer, staticSet, dynamicVars);
                break;
            case JsExpressionStatement exprStmt:
                ScanAssignmentsInExpression(exprStmt.Expression, staticSet, dynamicVars);
                break;
            case JsReturnStatement ret:
                if (ret.Value != null) ScanAssignmentsInExpression(ret.Value, staticSet, dynamicVars);
                break;
            case JsIfStatement ifs:
                ScanAssignmentsInExpression(ifs.Test, staticSet, dynamicVars);
                ScanDynamicAssignments(ifs.Consequent, staticSet, dynamicVars);
                if (ifs.Alternate != null) ScanDynamicAssignments(ifs.Alternate, staticSet, dynamicVars);
                break;
            case JsWhileStatement whiles:
                ScanAssignmentsInExpression(whiles.Test, staticSet, dynamicVars);
                ScanDynamicAssignments(whiles.Body, staticSet, dynamicVars);
                break;
            case JsForStatement fors:
                if (fors.Init != null) ScanDynamicAssignments(fors.Init, staticSet, dynamicVars);
                if (fors.Test != null) ScanAssignmentsInExpression(fors.Test, staticSet, dynamicVars);
                if (fors.Update != null) ScanDynamicAssignments(fors.Update, staticSet, dynamicVars);
                ScanDynamicAssignments(fors.Body, staticSet, dynamicVars);
                break;
            case JsBlockStatement block:
                foreach (var child in block.Statements) ScanDynamicAssignments(child, staticSet, dynamicVars);
                break;
            case JsSwitchStatement switchStmt:
                ScanAssignmentsInExpression(switchStmt.Discriminant, staticSet, dynamicVars);
                foreach (var c in switchStmt.Cases)
                {
                    if (c.Test != null) ScanAssignmentsInExpression(c.Test, staticSet, dynamicVars);
                    foreach (var child in c.Consequent) ScanDynamicAssignments(child, staticSet, dynamicVars);
                }
                break;
            case JsTryStatement tryStmt:
                ScanDynamicAssignments(tryStmt.Block, staticSet, dynamicVars);
                if (tryStmt.HandlerBlock != null) ScanDynamicAssignments(tryStmt.HandlerBlock, staticSet, dynamicVars);
                if (tryStmt.Finalizer != null) ScanDynamicAssignments(tryStmt.Finalizer, staticSet, dynamicVars);
                break;
            case JsThrowStatement throwStmt:
                ScanAssignmentsInExpression(throwStmt.Value, staticSet, dynamicVars);
                break;
        }
    }

    private static void ScanAssignmentsInExpression(JsExpression expr, HashSet<string> staticSet, HashSet<string> dynamicVars)
    {
        switch (expr)
        {
            case JsAssignmentExpression assign:
                if (assign.Target is JsIdentifierExpression id)
                {
                    if (!IsStaticTypeInternal(assign.Value, staticSet))
                    {
                        dynamicVars.Add(id.Name);
                    }
                }
                ScanAssignmentsInExpression(assign.Target, staticSet, dynamicVars);
                ScanAssignmentsInExpression(assign.Value, staticSet, dynamicVars);
                break;
            case JsBinaryExpression bin:
                ScanAssignmentsInExpression(bin.Left, staticSet, dynamicVars);
                ScanAssignmentsInExpression(bin.Right, staticSet, dynamicVars);
                break;
            case JsUnaryExpression unary:
                ScanAssignmentsInExpression(unary.Operand, staticSet, dynamicVars);
                break;
            case JsUpdateExpression update:
                ScanAssignmentsInExpression(update.Target, staticSet, dynamicVars);
                break;
            case JsConditionalExpression cond:
                ScanAssignmentsInExpression(cond.Test, staticSet, dynamicVars);
                ScanAssignmentsInExpression(cond.Consequent, staticSet, dynamicVars);
                ScanAssignmentsInExpression(cond.Alternate, staticSet, dynamicVars);
                break;
            case JsCallExpression call:
                ScanAssignmentsInExpression(call.Target, staticSet, dynamicVars);
                foreach (var arg in call.Arguments) ScanAssignmentsInExpression(arg, staticSet, dynamicVars);
                break;
            case JsArrayExpression array:
                foreach (var el in array.Elements) ScanAssignmentsInExpression(el, staticSet, dynamicVars);
                break;
            case JsObjectExpression obj:
                foreach (var prop in obj.Properties.Values) ScanAssignmentsInExpression(prop, staticSet, dynamicVars);
                break;
            case JsNewExpression newExpr:
                foreach (var arg in newExpr.Arguments) ScanAssignmentsInExpression(arg, staticSet, dynamicVars);
                break;
            case JsTemplateLiteralExpression temp:
                foreach (var ex in temp.Expressions) ScanAssignmentsInExpression(ex, staticSet, dynamicVars);
                break;
        }
    }

    private static bool IsStaticTypeInternal(JsExpression expr, HashSet<string> staticSet)
    {
        return expr switch
        {
            JsLiteralExpression => true,
            JsUnaryExpression => true,
            JsUpdateExpression => true,
            JsBinaryExpression bin => bin.Operator != "||" && bin.Operator != "&&" && bin.Operator != "??" && IsStaticTypeInternal(bin.Left, staticSet) && IsStaticTypeInternal(bin.Right, staticSet),
            JsIdentifierExpression id => staticSet.Contains(id.Name),
            _ => false
        };
    }

    private static void CollectVariablesInStatement(JsStatement stmt, HashSet<string> set)
    {
        if (stmt is JsVariableStatement variable)
        {
            if (variable.Initializer is JsLiteralExpression lit && lit.Value != null)
            {
                if (lit.Value is double || lit.Value is int || lit.Value is string || lit.Value is bool)
                {
                    set.Add(variable.Name);
                }
            }
            else if (variable.Initializer is JsArrayExpression)
            {
                set.Add(variable.Name);
            }
        }
        else if (stmt is JsBlockStatement block)
        {
            foreach (var child in block.Statements) CollectVariablesInStatement(child, set);
        }
        else if (stmt is JsIfStatement ifs)
        {
            CollectVariablesInStatement(ifs.Consequent, set);
            if (ifs.Alternate != null) CollectVariablesInStatement(ifs.Alternate, set);
        }
        else if (stmt is JsWhileStatement whiles)
        {
            CollectVariablesInStatement(whiles.Body, set);
        }
        else if (stmt is JsForStatement fors)
        {
            if (fors.Init != null) CollectVariablesInStatement(fors.Init, set);
            CollectVariablesInStatement(fors.Body, set);
        }
    }

    private static bool IsStaticType(JsExpression expr)
    {
        return expr switch
        {
            JsLiteralExpression => true,
            JsUnaryExpression => true,
            JsUpdateExpression => true,
            JsBinaryExpression bin => bin.Operator != "||" && bin.Operator != "&&" && bin.Operator != "??" && IsStaticType(bin.Left) && IsStaticType(bin.Right),
            JsIdentifierExpression id => (_currentStaticVars?.Contains(id.Name) == true) || (_currentStaticParameters?.Contains(id.Name) == true),
            _ => false
        };
    }

    public static string TranspileToCSharp(string source, string className = "ScriptModule")
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("#nullable disable warnings");
        builder.AppendLine("using TypedJint;");
        builder.AppendLine("using TypedJint.Runtime;");
        builder.AppendLine();
        builder.Append("public static class ").Append(SanitizeIdentifier(className)).AppendLine();
        builder.AppendLine("{");
        foreach (var fn in SimpleJsParser.ParseFunctions(source))
        {
            EmitFunction(builder, fn, 1);
            builder.AppendLine();
        }
        builder.AppendLine("}");
        return builder.ToString();
    }

    public static string TranspileFunctionToCSharp(JsFunctionDeclaration function)
    {
        var builder = new StringBuilder();
        EmitFunction(builder, function, 0);
        return builder.ToString();
    }

    private static void EmitFunction(StringBuilder builder, JsFunctionDeclaration function, int indent)
    {
        var pad = Pad(indent);
        var returnType = function.Annotation is null ? "object?" : ToCSharpType(function.Annotation.ReturnType);
        var parameters = function.Parameters.Select(parameter =>
        {
            var type = function.Annotation is not null && function.Annotation.Parameters.TryGetValue(parameter, out var staticType) ? ToCSharpType(staticType) : "object?";
            return type + " " + SanitizeIdentifier(parameter);
        });

        builder.Append(pad).Append("public static ").Append(returnType).Append(' ').Append(SanitizeIdentifier(function.Name)).Append('(').Append(string.Join(", ", parameters)).AppendLine(")");
        builder.Append(pad).AppendLine("{");
        
        var prevStaticVars = _currentStaticVars;
        _currentStaticVars = CollectStaticVariables(function);
        var prevStaticParams = _currentStaticParameters;
        _currentStaticParameters = new HashSet<string>(StringComparer.Ordinal);
        if (function.Annotation != null)
        {
            foreach (var kv in function.Annotation.Parameters)
            {
                if (kv.Value.Kind != JsStaticTypeKind.Object)
                {
                    _currentStaticParameters.Add(kv.Key);
                }
            }
        }
        try
        {
            foreach (var statement in function.Body)
            {
                EmitStatement(builder, statement, indent + 1);
            }
            if (returnType != "void")
            {
                var defaultReturn = returnType switch
                {
                    "double" => "return 0.0;",
                    "bool" => "return false;",
                    "string" => "return \"\";",
                    _ => "return null;"
                };
                builder.Append(Pad(indent + 1)).AppendLine(defaultReturn);
            }
        }
        finally
        {
            _currentStaticVars = prevStaticVars;
            _currentStaticParameters = prevStaticParams;
        }
        
        builder.Append(pad).AppendLine("}");
    }

    private static void EmitStatement(StringBuilder builder, JsStatement statement, int indent)
    {
        var pad = Pad(indent);
        switch (statement)
        {
            case JsBlockStatement block:
                builder.Append(pad).AppendLine("{");
                foreach (var child in block.Statements) EmitStatement(builder, child, indent + 1);
                builder.Append(pad).AppendLine("}");
                break;
            case JsVariableStatement variable:
                if (variable.Initializer is JsLiteralExpression { Value: null })
                {
                    builder.Append(pad).Append("object? ").Append(SanitizeIdentifier(variable.Name)).AppendLine(" = null;");
                }
                else
                {
                    var typeStr = _currentStaticVars?.Contains(variable.Name) == true ? "var" : "object?";
                    builder.Append(pad).Append(typeStr).Append(' ').Append(SanitizeIdentifier(variable.Name)).Append(" = ").Append(EmitExpression(variable.Initializer)).AppendLine(";");
                }
                break;
            case JsReturnStatement ret:
                builder.Append(pad).Append("return");
                if (ret.Value is not null) builder.Append(' ').Append(EmitExpression(ret.Value));
                builder.AppendLine(";");
                break;
            case JsExpressionStatement expression:
                builder.Append(pad).Append(EmitExpression(expression.Expression)).AppendLine(";");
                break;

            case JsIfStatement ifStatement:
                builder.Append(pad).Append("if (").Append(EmitExpression(ifStatement.Test)).AppendLine(")");
                EmitEmbeddedStatement(builder, ifStatement.Consequent, indent);
                if (ifStatement.Alternate is not null)
                {
                    builder.Append(pad).AppendLine("else");
                    EmitEmbeddedStatement(builder, ifStatement.Alternate, indent);
                }
                break;
            case JsWhileStatement whileStatement:
                builder.Append(pad).Append("while (").Append(EmitExpression(whileStatement.Test)).AppendLine(")");
                EmitEmbeddedStatement(builder, whileStatement.Body, indent);
                break;
            case JsForStatement forStatement:
                builder.Append(pad).Append("for (").Append(EmitForPart(forStatement.Init)).Append("; ").Append(forStatement.Test is null ? string.Empty : EmitExpression(forStatement.Test)).Append("; ").Append(EmitForPart(forStatement.Update)).AppendLine(")");
                EmitEmbeddedStatement(builder, forStatement.Body, indent);
                break;
            case JsBreakStatement:
                builder.Append(pad).AppendLine("break;");
                break;
            case JsContinueStatement:
                builder.Append(pad).AppendLine("continue;");
                break;
            case JsThrowStatement throwStmt:
                if (throwStmt.Value is JsNewExpression { Callee: "Error" } newErr && newErr.Arguments.Count > 0)
                {
                    builder.Append(pad).Append("throw new Exception(").Append(EmitExpression(newErr.Arguments[0])).AppendLine(");");
                }
                else
                {
                    builder.Append(pad).Append("throw new Exception(Convert.ToString(").Append(EmitExpression(throwStmt.Value)).AppendLine("));");
                }
                break;
            case JsTryStatement tryStmt:
                builder.Append(pad).AppendLine("try");
                EmitEmbeddedStatement(builder, tryStmt.Block, indent);
                if (tryStmt.HandlerBlock is not null)
                {
                    builder.Append(pad).AppendLine("catch (Exception __ex)");
                    builder.Append(pad).AppendLine("{");
                    if (!string.IsNullOrEmpty(tryStmt.HandlerParam))
                    {
                        builder.Append(Pad(indent + 1)).Append("string ").Append(SanitizeIdentifier(tryStmt.HandlerParam)).AppendLine(" = __ex.Message;");
                    }
                    EmitStatement(builder, tryStmt.HandlerBlock, indent + 1);
                    builder.Append(pad).AppendLine("}");
                }
                if (tryStmt.Finalizer is not null)
                {
                    builder.Append(pad).AppendLine("finally");
                    EmitEmbeddedStatement(builder, tryStmt.Finalizer, indent);
                }
                break;
            case JsSwitchStatement switchStmt:
                builder.Append(pad).Append("switch (").Append(EmitExpression(switchStmt.Discriminant)).AppendLine(")");
                builder.Append(pad).AppendLine("{");
                foreach (var c in switchStmt.Cases)
                {
                    if (c.Test is not null)
                    {
                        builder.Append(Pad(indent + 1)).Append("case ").Append(EmitExpression(c.Test)).AppendLine(":");
                    }
                    else
                    {
                        builder.Append(Pad(indent + 1)).AppendLine("default:");
                    }
                    foreach (var s in c.Consequent)
                    {
                        EmitStatement(builder, s, indent + 2);
                    }
                }
                builder.Append(pad).AppendLine("}");
                break;
            default:
                builder.Append(pad).Append("// unsupported: ").AppendLine(statement.GetType().Name);
                break;
        }
    }

    private static void EmitEmbeddedStatement(StringBuilder builder, JsStatement statement, int indent)
    {
        if (statement is JsBlockStatement)
        {
            EmitStatement(builder, statement, indent);
            return;
        }

        builder.Append(Pad(indent)).AppendLine("{");
        EmitStatement(builder, statement, indent + 1);
        builder.Append(Pad(indent)).AppendLine("}");
    }

    private static string EmitForPart(JsStatement? statement)
    {
        return statement switch
        {
            null => string.Empty,
            JsVariableStatement variable => (variable.Initializer is JsLiteralExpression { Value: null })
                ? "object? " + SanitizeIdentifier(variable.Name) + " = null"
                : "var " + SanitizeIdentifier(variable.Name) + " = " + EmitExpression(variable.Initializer),
            JsExpressionStatement expression => EmitExpression(expression.Expression),
            _ => statement.GetType().Name
        };
    }

    private static string EmitExpression(JsExpression expression)
    {
        return expression switch
        {
            JsLiteralExpression { Value: null } => "null",
            JsLiteralExpression { Value: string text } => FormatStringLiteral(text),
            JsLiteralExpression { Value: bool value } => value ? "true" : "false",
            JsLiteralExpression { Value: double value } => value.ToString("R", CultureInfo.InvariantCulture),
            JsLiteralExpression literal => Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? string.Empty,
            JsIdentifierExpression identifier => SanitizeIdentifier(identifier.Name),
            JsMemberExpression member => EmitMemberExpression(member),
            JsIndexExpression index => IsStaticType(index.Target)
                ? EmitExpression(index.Target) + "[" + EmitExpression(index.Index) + "]"
                : "JavaScriptRuntimeEngine.GetIndex(" + EmitExpression(index.Target) + ", " + EmitExpression(index.Index) + ")",
            JsCallExpression call => EmitCallExpression(call),
            JsBinaryExpression binary => binary.Operator switch
            {
                "||" => $"JavaScriptRuntimeEngine.LogicalOr({EmitExpression(binary.Left)}, {EmitExpression(binary.Right)})",
                "&&" => $"JavaScriptRuntimeEngine.LogicalAnd({EmitExpression(binary.Left)}, {EmitExpression(binary.Right)})",
                "??" => $"JavaScriptRuntimeEngine.Coalesce({EmitExpression(binary.Left)}, {EmitExpression(binary.Right)})",
                "+" when !IsStaticType(binary.Left) || !IsStaticType(binary.Right) => $"JavaScriptRuntimeEngine.Add({EmitExpression(binary.Left)}, {EmitExpression(binary.Right)})",
                "-" when !IsStaticType(binary.Left) || !IsStaticType(binary.Right) => $"JavaScriptRuntimeEngine.Subtract({EmitExpression(binary.Left)}, {EmitExpression(binary.Right)})",
                "*" when !IsStaticType(binary.Left) || !IsStaticType(binary.Right) => $"JavaScriptRuntimeEngine.Multiply({EmitExpression(binary.Left)}, {EmitExpression(binary.Right)})",
                "/" when !IsStaticType(binary.Left) || !IsStaticType(binary.Right) => $"JavaScriptRuntimeEngine.Divide({EmitExpression(binary.Left)}, {EmitExpression(binary.Right)})",
                "%" when !IsStaticType(binary.Left) || !IsStaticType(binary.Right) => $"JavaScriptRuntimeEngine.Modulo({EmitExpression(binary.Left)}, {EmitExpression(binary.Right)})",
                _ => "(" + EmitExpression(binary.Left) + " " + MapOperator(binary.Operator) + " " + EmitExpression(binary.Right) + ")"
            },
            JsUnaryExpression unary => "(" + unary.Operator + EmitExpression(unary.Operand) + ")",
            JsUpdateExpression update => update.Prefix ? update.Operator + EmitExpression(update.Target) : EmitExpression(update.Target) + update.Operator,
            JsArrayExpression array => "new[] { " + string.Join(", ", array.Elements.Select(EmitExpression)) + " }",
            JsConditionalExpression conditional => "(" + EmitExpression(conditional.Test) + " ? " + EmitExpression(conditional.Consequent) + " : " + EmitExpression(conditional.Alternate) + ")",
            JsFunctionExpression func => EmitFunctionExpression(func),
            JsArrowFunctionExpression arrow => EmitArrowFunctionExpression(arrow),
            JsObjectExpression obj => "new Dictionary<string, object?>(StringComparer.Ordinal) { " + string.Join(", ", obj.Properties.Select(p => $"[\"{p.Key}\"] = {EmitExpression(p.Value)}")) + " }",
            JsNewExpression newExpr => "new " + SanitizeIdentifier(newExpr.Callee) + "(" + string.Join(", ", newExpr.Arguments.Select(EmitExpression)) + ")",
            JsThisExpression => "this",
            JsTemplateLiteralExpression temp => EmitTemplateLiteral(temp),
            JsAssignmentExpression assign => EmitAssignmentExpression(assign),
            _ => expression.GetType().Name
        };
    }

    private static string EmitTemplateLiteral(JsTemplateLiteralExpression temp)
    {
        var parts = new List<string>();
        for (int i = 0; i < temp.Quasis.Count; i++)
        {
            var raw = temp.Quasis[i];
            if (!string.IsNullOrEmpty(raw))
            {
                parts.Add(FormatStringLiteral(raw));
            }
            if (i < temp.Expressions.Count)
            {
                parts.Add("Convert.ToString(" + EmitExpression(temp.Expressions[i]) + ")");
            }
        }
        if (parts.Count == 0) return "\"\"";
        return string.Join(" + ", parts);
    }

    private static string EmitLambdaBody(IReadOnlyList<JsStatement> body, int indent)
    {
        var sb = new StringBuilder();
        foreach (var stmt in body)
        {
            EmitStatement(sb, stmt, indent);
        }
        sb.Append(Pad(indent)).AppendLine("return null;");
        return sb.ToString();
    }

    private static string EmitFunctionExpression(JsFunctionExpression func)
    {
        var paramTypes = string.Join(", ", Enumerable.Repeat("object?", func.Parameters.Count + 1));
        var paramDecl = string.Join(", ", func.Parameters.Select(p => $"object? {SanitizeIdentifier(p)}"));
        var bodyStr = EmitLambdaBody(func.Body, 2);
        return $"new Func<{paramTypes}>(( {paramDecl} ) => {{\n{bodyStr}{Pad(1)}}})";
    }

    private static string EmitArrowFunctionExpression(JsArrowFunctionExpression func)
    {
        var paramTypes = string.Join(", ", Enumerable.Repeat("object?", func.Parameters.Count + 1));
        var paramDecl = string.Join(", ", func.Parameters.Select(p => $"object? {SanitizeIdentifier(p)}"));
        var bodyStr = EmitLambdaBody(func.Body, 2);
        return $"new Func<{paramTypes}>(( {paramDecl} ) => {{\n{bodyStr}{Pad(1)}}})";
    }

    private static string EmitAssignmentExpression(JsAssignmentExpression assign)
    {
        if (assign.Target is JsMemberExpression member && !IsStaticType(member.Target))
        {
            var targetStr = EmitExpression(member.Target);
            var valueStr = EmitExpression(assign.Value);
            if (assign.Operator == "=")
            {
                return $"JavaScriptRuntimeEngine.SetProperty({targetStr}, \"{member.Member}\", {valueStr})";
            }
            else
            {
                var op = assign.Operator.Substring(0, assign.Operator.Length - 1);
                var currentVal = $"JavaScriptRuntimeEngine.GetProperty({targetStr}, \"{member.Member}\")";
                var mappedOp = op switch { "+" => "Add", "-" => "Subtract", "*" => "Multiply", "/" => "Divide", "%" => "Modulo", _ => null };
                if (mappedOp != null)
                {
                    return $"JavaScriptRuntimeEngine.SetProperty({targetStr}, \"{member.Member}\", JavaScriptRuntimeEngine.{mappedOp}({currentVal}, {valueStr}))";
                }
                return $"JavaScriptRuntimeEngine.SetProperty({targetStr}, \"{member.Member}\", {currentVal} {op} {valueStr})";
            }
        }
        else if (assign.Target is JsIndexExpression index && !IsStaticType(index.Target))
        {
            var targetStr = EmitExpression(index.Target);
            var indexStr = EmitExpression(index.Index);
            var valueStr = EmitExpression(assign.Value);
            if (assign.Operator == "=")
            {
                return $"JavaScriptRuntimeEngine.SetIndex({targetStr}, {indexStr}, {valueStr})";
            }
            else
            {
                var op = assign.Operator.Substring(0, assign.Operator.Length - 1);
                var currentVal = $"JavaScriptRuntimeEngine.GetIndex({targetStr}, {indexStr})";
                var mappedOp = op switch { "+" => "Add", "-" => "Subtract", "*" => "Multiply", "/" => "Divide", "%" => "Modulo", _ => null };
                if (mappedOp != null)
                {
                    return $"JavaScriptRuntimeEngine.SetIndex({targetStr}, {indexStr}, JavaScriptRuntimeEngine.{mappedOp}({currentVal}, {valueStr}))";
                }
                return $"JavaScriptRuntimeEngine.SetIndex({targetStr}, {indexStr}, {currentVal} {op} {valueStr})";
            }
        }
        
        return EmitExpression(assign.Target) + " " + assign.Operator + " " + EmitExpression(assign.Value);
    }

    private static string EmitMemberExpression(JsMemberExpression member)
    {
        var targetStr = EmitExpression(member.Target);
        if (member.Member == "length")
        {
            return IsStaticType(member.Target)
                ? targetStr + ".Length"
                : $"((dynamic){targetStr}).Length";
        }
        if (!IsStaticType(member.Target))
        {
            return $"JavaScriptRuntimeEngine.GetProperty({targetStr}, \"{member.Member}\")";
        }
        return targetStr + "." + SanitizeIdentifier(member.Member);
    }

    private static string EmitCallExpression(JsCallExpression call)
    {
        if (call.Target is JsIdentifierExpression identifier)
        {
            var mappedGlobal = identifier.Name switch
            {
                "fetch" => "JavaScriptStandardLibrary.Fetch",
                "setTimeout" => "JavaScriptStandardLibrary.setTimeout",
                "clearTimeout" => "JavaScriptStandardLibrary.clearTimeout",
                "setInterval" => "JavaScriptStandardLibrary.setInterval",
                "clearInterval" => "JavaScriptStandardLibrary.clearInterval",
                _ => null
            };

            if (mappedGlobal != null)
            {
                return mappedGlobal + "(" + string.Join(", ", call.Arguments.Select(EmitExpression)) + ")";
            }
        }

        if (call.Target is JsMemberExpression member)
        {
            var targetName = EmitExpression(member.Target);
            var memberName = member.Member;
            var fullName = $"{targetName}.{memberName}";

            var mappedName = fullName switch
            {
                "Math.abs" => "Math.Abs",
                "Math.sqrt" => "Math.Sqrt",
                "Math.pow" => "Math.Pow",
                "Math.min" => "Math.Min",
                "Math.max" => "Math.Max",
                "Math.floor" => "Math.Floor",
                "Math.ceil" => "Math.Ceiling",
                "Math.round" => "Math.Round",
                "Math.sin" => "Math.Sin",
                "Math.cos" => "Math.Cos",
                "Math.tan" => "Math.Tan",
                "Math.log" => "Math.Log",
                "Math.exp" => "Math.Exp",
                "Math.sign" => "JavaScriptMath.Instance.sign",
                "Math.trunc" => "JavaScriptMath.Instance.trunc",
                "Math.cbrt" => "JavaScriptMath.Instance.cbrt",
                "Math.clz32" => "JavaScriptMath.Instance.clz32",
                "Math.log2" => "JavaScriptMath.Instance.log2",
                "Math.log10" => "JavaScriptMath.Instance.log10",
                "Math.log1p" => "JavaScriptMath.Instance.log1p",
                "Math.expm1" => "JavaScriptMath.Instance.expm1",
                "Math.sinh" => "JavaScriptMath.Instance.sinh",
                "Math.cosh" => "JavaScriptMath.Instance.cosh",
                "Math.tanh" => "JavaScriptMath.Instance.tanh",
                "Math.asinh" => "JavaScriptMath.Instance.asinh",
                "Math.acosh" => "JavaScriptMath.Instance.acosh",
                "Math.atanh" => "JavaScriptMath.Instance.atanh",
                "Math.hypot" => "JavaScriptMath.Instance.hypot",
                "Math.fround" => "JavaScriptMath.Instance.fround",
                "Math.imul" => "JavaScriptMath.Instance.imul",
                "console.log" => "Console.WriteLine",
                "console.info" => "Console.WriteLine",
                "console.debug" => "Console.WriteLine",
                "console.warn" => "Console.WriteLine",
                "console.error" => "Console.Error.WriteLine",
                "console.write" => "Console.Write",
                "console.writeLine" => "Console.WriteLine",
                "net.getString" => "JavaScriptNetwork.Instance.getString",
                "net.getBytes" => "JavaScriptNetwork.Instance.getBytes",
                "net.postString" => "JavaScriptNetwork.Instance.postString",
                "encoding.base64Encode" => "JavaScriptEncoding.Instance.base64Encode",
                "encoding.base64Decode" => "JavaScriptEncoding.Instance.base64Decode",
                "encoding.uriEncode" => "JavaScriptEncoding.Instance.uriEncode",
                "encoding.uriDecode" => "JavaScriptEncoding.Instance.uriDecode",
                "encoding.utf8ByteCount" => "JavaScriptEncoding.Instance.utf8ByteCount",
                "json.stringify" => "JavaScriptJson.Instance.stringify",
                "json.parse" => "JavaScriptJson.Instance.parse",
                "JSON.stringify" => "JavaScriptJson.Instance.stringify",
                "JSON.parse" => "JavaScriptJson.Instance.parse",
                "time.nowUnixMilliseconds" => "JavaScriptTime.Instance.nowUnixMilliseconds",
                "time.utcNowIsoString" => "JavaScriptTime.Instance.utcNowIsoString",
                _ => null
            };

            if (mappedName != null)
            {
                return mappedName + "(" + string.Join(", ", call.Arguments.Select(EmitExpression)) + ")";
            }
        }

        if (call.Target is JsMemberExpression memberExpr && !IsStaticType(memberExpr.Target))
        {
            var targetStr = EmitExpression(memberExpr.Target);
            var argsStr = string.Join(", ", call.Arguments.Select(x => $"({EmitExpression(x)})"));
            return $"JavaScriptRuntimeEngine.InvokeMethod({targetStr}, \"{memberExpr.Member}\", new object?[] {{ {argsStr} }})";
        }

        return EmitExpression(call.Target) + "(" + string.Join(", ", call.Arguments.Select(EmitExpression)) + ")";
    }

    private static string MapOperator(string op) => op switch { "===" => "==", "!==" => "!=", _ => op };
    private static string ToCSharpType(JsStaticType type) => type.Kind switch
    {
        JsStaticTypeKind.Void => "void",
        JsStaticTypeKind.Number => "double",
        JsStaticTypeKind.String => "string",
        JsStaticTypeKind.Boolean => "bool",
        JsStaticTypeKind.Clr => type.ClrType.Name,
        _ => "object?"
    };

    private static string FormatStringLiteral(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";
    private static string SanitizeIdentifier(string value) => value switch { "class" or "namespace" or "public" or "private" or "protected" or "internal" or "static" or "void" or "double" or "string" or "bool" or "object" or "return" => "@" + value, _ => value };
    private static string Pad(int indent) => new(' ', indent * 4);
}

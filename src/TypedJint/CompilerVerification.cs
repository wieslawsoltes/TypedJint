using System.Globalization;
using System.Reflection;
using System.Text;

namespace TypedJint;

public sealed class VerifiedTypedCompilationResult
{
    public required TypedCompilationResult Compilation { get; init; }
    public required IReadOnlyDictionary<string, VerifiedCompilerOutput> CompilerOutputs { get; init; }
    public required IReadOnlyDictionary<string, RuntimeFunctionVerificationResult> RuntimeOutputs { get; init; }

    public bool Verified =>
        CompilerOutputs.Values.All(x => x.Verified)
        && RuntimeOutputs.Values.All(x => x.Verified)
        && !Compilation.Diagnostics.Any(x => x.Severity == TypedDiagnosticSeverity.Error);

    public void ThrowIfUnverified()
    {
        if (Verified)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine("TypedJint verification failed.");

        foreach (var output in CompilerOutputs.Values.Where(x => !x.Verified))
        {
            builder.AppendLine(output.ToMarkdown());
        }

        foreach (var output in RuntimeOutputs.Values.Where(x => !x.Verified))
        {
            builder.AppendLine(output.ToMarkdown());
        }

        throw new InvalidOperationException(builder.ToString());
    }
}

public sealed record VerifiedCompilerOutput(
    string FunctionName,
    bool Verified,
    string SemanticSignature,
    string DelegateSignature,
    string NormalizedIr,
    IReadOnlyList<TypedDiagnostic> Diagnostics)
{
    public string CSharpPreview { get; init; } = string.Empty;

    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        builder.Append("## ").Append(FunctionName).AppendLine();
        builder.Append("Verified: ").Append(Verified).AppendLine();
        builder.Append("Semantic signature: `").Append(SemanticSignature).AppendLine("`");
        builder.Append("Delegate signature: `").Append(DelegateSignature).AppendLine("`");
        builder.AppendLine();
        builder.AppendLine("```text");
        builder.Append(NormalizedIr);
        builder.AppendLine("```");

        if (!string.IsNullOrWhiteSpace(CSharpPreview))
        {
            builder.AppendLine();
            builder.AppendLine("```csharp");
            builder.Append(CSharpPreview);
            builder.AppendLine("```");
        }

        foreach (var diagnostic in Diagnostics)
        {
            builder.Append("- ").Append(diagnostic.Code).Append(' ').Append(diagnostic.Severity).Append(": ").AppendLine(diagnostic.Message);
        }

        return builder.ToString();
    }
}

public sealed record RuntimeVerificationObservation(
    IReadOnlyList<object?> Arguments,
    object? CompiledResult,
    object? JintResult,
    bool Equivalent,
    string? Message);

public sealed record RuntimeFunctionVerificationResult(
    string FunctionName,
    bool Verified,
    IReadOnlyList<RuntimeVerificationObservation> Observations)
{
    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        builder.Append("## runtime ").Append(FunctionName).AppendLine();
        builder.Append("Verified: ").Append(Verified).AppendLine();
        foreach (var observation in Observations)
        {
            builder
                .Append("- args=[")
                .Append(string.Join(", ", observation.Arguments.Select(x => x?.ToString() ?? "null")))
                .Append("] compiled=")
                .Append(observation.CompiledResult?.ToString() ?? "null")
                .Append(" jint=")
                .Append(observation.JintResult?.ToString() ?? "null")
                .Append(" equivalent=")
                .Append(observation.Equivalent);

            if (!string.IsNullOrWhiteSpace(observation.Message))
            {
                builder.Append(" message=").Append(observation.Message);
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }
}

public static class TypedJintVerificationExtensions
{
    public static VerifiedTypedCompilationResult ExecuteVerified(
        this TypedJintEngine engine,
        string source,
        IReadOnlyDictionary<string, object?[][]>? runtimeCases = null,
        double numericTolerance = 1e-9)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(source);

        var compilation = engine.Execute(source);
        var compilerOutputs = TypedCompilerOutputVerifier.Verify(source, compilation);
        var runtimeOutputs = new Dictionary<string, RuntimeFunctionVerificationResult>(StringComparer.Ordinal);

        if (runtimeCases is not null)
        {
            foreach (var pair in runtimeCases)
            {
                runtimeOutputs[pair.Key] = engine.VerifyAgainstJint(pair.Key, pair.Value, numericTolerance);
            }
        }

        return new VerifiedTypedCompilationResult
        {
            Compilation = compilation,
            CompilerOutputs = compilerOutputs,
            RuntimeOutputs = runtimeOutputs
        };
    }

    public static RuntimeFunctionVerificationResult VerifyAgainstJint(
        this TypedJintEngine engine,
        string functionName,
        object?[][] cases,
        double numericTolerance = 1e-9)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        ArgumentNullException.ThrowIfNull(cases);

        var observations = new List<RuntimeVerificationObservation>();

        foreach (var arguments in cases)
        {
            var compiledResult = engine.Invoke(functionName, arguments);
            var jintResult = engine.Jint.Invoke(functionName, arguments).ToObject();
            var equivalent = RuntimeValueVerifier.AreEquivalent(compiledResult, jintResult, numericTolerance, out var message);
            observations.Add(new RuntimeVerificationObservation(arguments, compiledResult, jintResult, equivalent, message));
        }

        return new RuntimeFunctionVerificationResult(functionName, observations.All(x => x.Equivalent), observations);
    }
}

public static class TypedCompilerOutputVerifier
{
    public static IReadOnlyDictionary<string, VerifiedCompilerOutput> Verify(string source, TypedCompilationResult result)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(result);

        var functions = SimpleJsParser.ParseFunctions(source).ToDictionary(x => x.Name, StringComparer.Ordinal);
        var outputs = new Dictionary<string, VerifiedCompilerOutput>(StringComparer.Ordinal);

        foreach (var pair in result.CompiledFunctions)
        {
            var diagnostics = new List<TypedDiagnostic>();
            if (!functions.TryGetValue(pair.Key, out var declaration))
            {
                diagnostics.Add(new TypedDiagnostic(
                    "TJ0601",
                    TypedDiagnosticSeverity.Error,
                    $"Compiled function '{pair.Key}' was not found in parsed source."));

                outputs[pair.Key] = new VerifiedCompilerOutput(pair.Key, false, pair.Key + "(?)", FormatDelegate(pair.Value.Delegate), string.Empty, diagnostics);
                continue;
            }

            var semanticSignature = FormatSemanticSignature(declaration, diagnostics);
            var delegateSignature = FormatDelegate(pair.Value.Delegate);
            var normalizedIr = VerifiedIrPrinter.Print(declaration);
            var csharpPreview = TypedJintTranspiler.TranspileFunctionToCSharp(declaration);
            VerifyDelegateSignature(declaration, pair.Value.Delegate, diagnostics);

            var verified = diagnostics.All(x => x.Severity != TypedDiagnosticSeverity.Error);
            if (verified)
            {
                diagnostics.Add(new TypedDiagnostic(
                    "TJ0600",
                    TypedDiagnosticSeverity.Info,
                    $"Compiler output for '{pair.Key}' is structurally verified.",
                    declaration.Span));
            }

            outputs[pair.Key] = new VerifiedCompilerOutput(pair.Key, verified, semanticSignature, delegateSignature, normalizedIr, diagnostics)
            {
                CSharpPreview = csharpPreview
            };
        }

        return outputs;
    }

    private static void VerifyDelegateSignature(JsFunctionDeclaration declaration, Delegate @delegate, List<TypedDiagnostic> diagnostics)
    {
        if (declaration.Annotation is null)
        {
            diagnostics.Add(new TypedDiagnostic(
                "TJ0610",
                TypedDiagnosticSeverity.Error,
                $"Function '{declaration.Name}' has no annotation but was compiled.",
                declaration.Span));
            return;
        }

        var invoke = GetInvoke(@delegate);
        var actualParameters = invoke.GetParameters();

        if (actualParameters.Length != declaration.Parameters.Count)
        {
            diagnostics.Add(new TypedDiagnostic(
                "TJ0611",
                TypedDiagnosticSeverity.Error,
                $"Function '{declaration.Name}' parameter count mismatch. Expected {declaration.Parameters.Count}, got {actualParameters.Length}.",
                declaration.Span));
            return;
        }

        for (var i = 0; i < declaration.Parameters.Count; i++)
        {
            var parameterName = declaration.Parameters[i];
            if (!declaration.Annotation.Parameters.TryGetValue(parameterName, out var expectedType))
            {
                diagnostics.Add(new TypedDiagnostic(
                    "TJ0612",
                    TypedDiagnosticSeverity.Error,
                    $"Parameter '{parameterName}' has no annotation.",
                    declaration.Span));
                continue;
            }

            var actualType = actualParameters[i].ParameterType;
            if (actualType != expectedType.ClrType)
            {
                diagnostics.Add(new TypedDiagnostic(
                    "TJ0613",
                    TypedDiagnosticSeverity.Error,
                    $"Parameter '{parameterName}' type mismatch. Expected {expectedType.ClrType.FullName}, got {actualType.FullName}.",
                    declaration.Span));
            }
        }

        if (invoke.ReturnType != declaration.Annotation.ReturnType.ClrType)
        {
            diagnostics.Add(new TypedDiagnostic(
                "TJ0614",
                TypedDiagnosticSeverity.Error,
                $"Return type mismatch. Expected {declaration.Annotation.ReturnType.ClrType.FullName}, got {invoke.ReturnType.FullName}.",
                declaration.Span));
        }
    }

    private static string FormatSemanticSignature(JsFunctionDeclaration declaration, List<TypedDiagnostic> diagnostics)
    {
        if (declaration.Annotation is null)
        {
            diagnostics.Add(new TypedDiagnostic("TJ0620", TypedDiagnosticSeverity.Error, $"Function '{declaration.Name}' has no annotation.", declaration.Span));
            return declaration.Name + "(?)";
        }

        var parameters = declaration.Parameters.Select(parameter =>
        {
            if (declaration.Annotation.Parameters.TryGetValue(parameter, out var type))
            {
                return DisplayType(type) + " " + parameter;
            }

            diagnostics.Add(new TypedDiagnostic("TJ0621", TypedDiagnosticSeverity.Error, $"Parameter '{parameter}' has no annotation.", declaration.Span));
            return "unknown " + parameter;
        });

        return declaration.Name + "(" + string.Join(", ", parameters) + "): " + DisplayType(declaration.Annotation.ReturnType);
    }

    private static string FormatDelegate(Delegate @delegate)
    {
        var invoke = GetInvoke(@delegate);
        var parameters = string.Join(", ", invoke.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name));
        return invoke.ReturnType.Name + " (" + parameters + ")";
    }

    private static MethodInfo GetInvoke(Delegate @delegate) =>
        @delegate.GetType().GetMethod("Invoke") ?? throw new InvalidOperationException("Delegate has no Invoke method.");

    private static string DisplayType(JsStaticType type)
    {
        return type.Kind switch
        {
            JsStaticTypeKind.Void => "void",
            JsStaticTypeKind.Number => "number",
            JsStaticTypeKind.String => "string",
            JsStaticTypeKind.Boolean => "boolean",
            JsStaticTypeKind.Object => "object",
            JsStaticTypeKind.Clr => type.ClrType.Name,
            _ => type.ClrType.Name
        };
    }
}

internal static class RuntimeValueVerifier
{
    public static bool AreEquivalent(object? compiled, object? interpreted, double numericTolerance, out string? message)
    {
        if (ReferenceEquals(compiled, interpreted))
        {
            message = null;
            return true;
        }

        if (compiled is null || interpreted is null)
        {
            message = "One side is null.";
            return false;
        }

        if (IsNumber(compiled) && IsNumber(interpreted))
        {
            var left = Convert.ToDouble(compiled, CultureInfo.InvariantCulture);
            var right = Convert.ToDouble(interpreted, CultureInfo.InvariantCulture);
            var equivalent = Math.Abs(left - right) <= numericTolerance;
            message = equivalent ? null : $"Numeric mismatch: compiled={left:R}, jint={right:R}.";
            return equivalent;
        }

        if (compiled.Equals(interpreted))
        {
            message = null;
            return true;
        }

        message = $"Value mismatch: compiled={compiled}, jint={interpreted}.";
        return false;
    }

    private static bool IsNumber(object value) => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
}

internal static class VerifiedIrPrinter
{
    public static string Print(JsFunctionDeclaration declaration)
    {
        var builder = new StringBuilder();
        builder.Append("fn ").Append(declaration.Name).AppendLine();
        builder.AppendLine("{");
        foreach (var statement in declaration.Body)
        {
            PrintStatement(builder, statement, 1);
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void PrintStatement(StringBuilder builder, JsStatement statement, int indent)
    {
        var pad = new string(' ', indent * 2);
        switch (statement)
        {
            case JsBlockStatement block:
                builder.Append(pad).AppendLine("block");
                builder.Append(pad).AppendLine("{");
                foreach (var child in block.Statements) PrintStatement(builder, child, indent + 1);
                builder.Append(pad).AppendLine("}");
                break;
            case JsVariableStatement variable:
                builder.Append(pad).Append("let ").Append(variable.Name).Append(" = ").AppendLine(PrintExpression(variable.Initializer));
                break;
            case JsReturnStatement ret:
                builder.Append(pad).Append("return");
                if (ret.Value is not null)
                {
                    builder.Append(' ').Append(PrintExpression(ret.Value));
                }

                builder.AppendLine();
                break;
            case JsExpressionStatement expression:
                builder.Append(pad).AppendLine(PrintExpression(expression.Expression));
                break;
            case JsAssignmentStatement assignment:
                builder.Append(pad).Append(PrintExpression(assignment.Target)).Append(" = ").AppendLine(PrintExpression(assignment.Value));
                break;
            case JsIfStatement ifStatement:
                builder.Append(pad).Append("if ").AppendLine(PrintExpression(ifStatement.Test));
                PrintStatement(builder, ifStatement.Consequent, indent + 1);
                if (ifStatement.Alternate is not null)
                {
                    builder.Append(pad).AppendLine("else");
                    PrintStatement(builder, ifStatement.Alternate, indent + 1);
                }
                break;
            case JsWhileStatement whileStatement:
                builder.Append(pad).Append("while ").AppendLine(PrintExpression(whileStatement.Test));
                PrintStatement(builder, whileStatement.Body, indent + 1);
                break;
            case JsForStatement forStatement:
                builder.Append(pad).Append("for init=").Append(PrintStatementPart(forStatement.Init)).Append(" test=").Append(forStatement.Test is null ? string.Empty : PrintExpression(forStatement.Test)).Append(" update=").AppendLine(PrintStatementPart(forStatement.Update));
                PrintStatement(builder, forStatement.Body, indent + 1);
                break;
            default:
                builder.Append(pad).AppendLine(statement.GetType().Name);
                break;
        }
    }

    private static string PrintStatementPart(JsStatement? statement)
    {
        return statement switch
        {
            null => string.Empty,
            JsVariableStatement variable => "let " + variable.Name + " = " + PrintExpression(variable.Initializer),
            JsAssignmentStatement assignment => PrintExpression(assignment.Target) + " = " + PrintExpression(assignment.Value),
            JsExpressionStatement expression => PrintExpression(expression.Expression),
            _ => statement.GetType().Name
        };
    }

    private static string PrintExpression(JsExpression expression)
    {
        return expression switch
        {
            JsLiteralExpression { Value: null } => "null",
            JsLiteralExpression { Value: string text } => "\"" + text.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
            JsLiteralExpression { Value: bool value } => value ? "true" : "false",
            JsLiteralExpression { Value: double value } => value.ToString("R", CultureInfo.InvariantCulture),
            JsLiteralExpression literal => Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? string.Empty,
            JsIdentifierExpression identifier => identifier.Name,
            JsMemberExpression member => PrintExpression(member.Target) + "." + member.Member,
            JsCallExpression call => PrintExpression(call.Target) + "(" + string.Join(", ", call.Arguments.Select(PrintExpression)) + ")",
            JsBinaryExpression binary => "(" + PrintExpression(binary.Left) + " " + binary.Operator + " " + PrintExpression(binary.Right) + ")",
            JsUnaryExpression unary => "(" + unary.Operator + PrintExpression(unary.Operand) + ")",
            JsUpdateExpression update => update.Prefix ? "(" + update.Operator + PrintExpression(update.Target) + ")" : "(" + PrintExpression(update.Target) + update.Operator + ")",
            _ => expression.GetType().Name
        };
    }
}

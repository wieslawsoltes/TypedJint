using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Linq.Expressions;
using TypedJint.Runtime;

namespace TypedJint;

public enum TypedBackendKind { ExpressionTrees, CSharp, IL }
public enum TypedCompilationMode { Disabled, CompileAnnotatedFunctionsOnly, CompileSafeFunctionsOnly, CompileAggressively }
public enum TypedDiagnosticSeverity { Info, Warning, Error }

public sealed class TypedJintOptions
{
    public bool EnableCompilation { get; init; } = true;
    public bool ExecuteOriginalSourceInJint { get; init; } = true;
    public TypedCompilationMode CompilationMode { get; init; } = TypedCompilationMode.CompileAnnotatedFunctionsOnly;
    public TypedBackendKind Backend { get; init; } = TypedBackendKind.ExpressionTrees;
    public bool ThrowOnCompilationFailure { get; init; }
    public Action<TypedDiagnostic>? DiagnosticSink { get; init; }
    public TypeScriptTypeRegistry? TypeScriptRegistry { get; init; }
}

public sealed record SourceSpan(int Start, int Length, int Line, int Column);
public sealed record TypedDiagnostic(string Code, TypedDiagnosticSeverity Severity, string Message, SourceSpan? Span = null);
public sealed record FallbackInfo(string FunctionName, string Reason, SourceSpan? Span = null);

public sealed class TypedCompilationResult
{
    public required IReadOnlyDictionary<string, ICompiledFunction> CompiledFunctions { get; init; }
    public required IReadOnlyDictionary<string, FallbackInfo> Fallbacks { get; init; }
    public required IReadOnlyList<TypedDiagnostic> Diagnostics { get; init; }
}

public interface ICompiledFunction
{
    string Name { get; }
    Delegate Delegate { get; }
    object? Invoke(params object?[] arguments);
}

public sealed class CompiledFunction : ICompiledFunction
{
    private readonly Func<object?[], object?> _invoker;

    public CompiledFunction(string name, Delegate del)
    {
        Name = name;
        Delegate = del;
        _invoker = CreateInvoker(del);
    }

    public string Name { get; }
    public Delegate Delegate { get; }
    public object? Invoke(params object?[] arguments) => _invoker(arguments);

    public static object? ConvertArgument(object? value, Type targetType)
    {
        if (value is null)
        {
            if (targetType.IsValueType)
            {
                if (Nullable.GetUnderlyingType(targetType) != null) return null;
                return Activator.CreateInstance(targetType);
            }
            return null;
        }
        if (targetType.IsInstanceOfType(value)) return value;
        return Convert.ChangeType(value, Nullable.GetUnderlyingType(targetType) ?? targetType, CultureInfo.InvariantCulture);
    }

    private static Func<object?[], object?> CreateInvoker(Delegate del)
    {
        var delegateType = del.GetType();
        var invokeMethod = delegateType.GetMethod("Invoke") ?? throw new InvalidOperationException("Delegate has no Invoke method.");
        var parameters = invokeMethod.GetParameters();
        var argsParam = System.Linq.Expressions.Expression.Parameter(typeof(object?[]), "args");

        var argExpressions = new List<System.Linq.Expressions.Expression>();
        var convertMethod = typeof(CompiledFunction).GetMethod(nameof(ConvertArgument), BindingFlags.Public | BindingFlags.Static)!;
        for (int i = 0; i < parameters.Length; i++)
        {
            var indexExpr = System.Linq.Expressions.Expression.ArrayIndex(argsParam, System.Linq.Expressions.Expression.Constant(i));
            var fallbackVal = System.Linq.Expressions.Expression.Default(parameters[i].ParameterType);
            var actualVal = System.Linq.Expressions.Expression.Convert(
                System.Linq.Expressions.Expression.Call(convertMethod, indexExpr, System.Linq.Expressions.Expression.Constant(parameters[i].ParameterType)),
                parameters[i].ParameterType
            );

            var conditionExpr = System.Linq.Expressions.Expression.Condition(
                System.Linq.Expressions.Expression.LessThan(System.Linq.Expressions.Expression.Constant(i), System.Linq.Expressions.Expression.ArrayLength(argsParam)),
                actualVal,
                fallbackVal
            );
            argExpressions.Add(conditionExpr);
        }

        var targetExpr = System.Linq.Expressions.Expression.Constant(del);
        var callExpr = System.Linq.Expressions.Expression.Invoke(targetExpr, argExpressions);

        System.Linq.Expressions.Expression bodyExpr;
        if (invokeMethod.ReturnType == typeof(void))
        {
            bodyExpr = System.Linq.Expressions.Expression.Block(
                callExpr,
                System.Linq.Expressions.Expression.Constant(null, typeof(object))
            );
        }
        else
        {
            bodyExpr = System.Linq.Expressions.Expression.Convert(callExpr, typeof(object));
        }

        var lambda = System.Linq.Expressions.Expression.Lambda<Func<object?[], object?>>(bodyExpr, argsParam);
        return lambda.Compile();
    }
}

public sealed class GeneratedScriptCompiledFunction : ICompiledFunction
{
    private readonly GeneratedCSharpScriptInstance _instance;
    private readonly MethodInfo _method;
    private readonly bool _isNative;

    public GeneratedScriptCompiledFunction(string name, GeneratedCSharpScriptInstance instance, MethodInfo method, bool isNative, Delegate del)
    {
        Name = name;
        _instance = instance;
        _method = method;
        _isNative = isNative;
        Delegate = del;
    }

    public string Name { get; }
    public Delegate Delegate { get; }

    public object? Invoke(params object?[] arguments)
    {
        if (_isNative)
        {
            return _invoker(arguments);
        }
        else
        {
            return _instance.InvokeRuntime(Name, arguments);
        }
    }

    private Func<object?[], object?> _invoker => _lazyInvoker ??= CreateInvoker(Delegate);
    private Func<object?[], object?>? _lazyInvoker;

    private static Func<object?[], object?> CreateInvoker(Delegate del)
    {
        var delegateType = del.GetType();
        var invokeMethod = delegateType.GetMethod("Invoke") ?? throw new InvalidOperationException("Delegate has no Invoke method.");
        var parameters = invokeMethod.GetParameters();
        var argsParam = System.Linq.Expressions.Expression.Parameter(typeof(object?[]), "args");

        var argExpressions = new List<System.Linq.Expressions.Expression>();
        var convertMethod = typeof(CompiledFunction).GetMethod(nameof(CompiledFunction.ConvertArgument), BindingFlags.Public | BindingFlags.Static)!;
        for (int i = 0; i < parameters.Length; i++)
        {
            var indexExpr = System.Linq.Expressions.Expression.ArrayIndex(argsParam, System.Linq.Expressions.Expression.Constant(i));
            var fallbackVal = System.Linq.Expressions.Expression.Default(parameters[i].ParameterType);
            var actualVal = System.Linq.Expressions.Expression.Convert(
                System.Linq.Expressions.Expression.Call(convertMethod, indexExpr, System.Linq.Expressions.Expression.Constant(parameters[i].ParameterType)),
                parameters[i].ParameterType
            );

            var conditionExpr = System.Linq.Expressions.Expression.Condition(
                System.Linq.Expressions.Expression.LessThan(System.Linq.Expressions.Expression.Constant(i), System.Linq.Expressions.Expression.ArrayLength(argsParam)),
                actualVal,
                fallbackVal
            );
            argExpressions.Add(conditionExpr);
        }

        var targetExpr = System.Linq.Expressions.Expression.Constant(del);
        var callExpr = System.Linq.Expressions.Expression.Invoke(targetExpr, argExpressions);

        System.Linq.Expressions.Expression bodyExpr;
        if (invokeMethod.ReturnType == typeof(void))
        {
            bodyExpr = System.Linq.Expressions.Expression.Block(
                callExpr,
                System.Linq.Expressions.Expression.Constant(null, typeof(object))
            );
        }
        else
        {
            bodyExpr = System.Linq.Expressions.Expression.Convert(callExpr, typeof(object));
        }

        var lambda = System.Linq.Expressions.Expression.Lambda<Func<object?[], object?>>(bodyExpr, argsParam);
        return lambda.Compile();
    }
}

public enum JsStaticTypeKind { Void, Number, String, Boolean, Object, Clr }

public sealed record JsStaticType(JsStaticTypeKind Kind, Type ClrType)
{
    public static readonly JsStaticType Void = new(JsStaticTypeKind.Void, typeof(void));
    public static readonly JsStaticType Number = new(JsStaticTypeKind.Number, typeof(double));
    public static readonly JsStaticType String = new(JsStaticTypeKind.String, typeof(string));
    public static readonly JsStaticType Boolean = new(JsStaticTypeKind.Boolean, typeof(bool));
    public static readonly JsStaticType Object = new(JsStaticTypeKind.Object, typeof(object));
    public static JsStaticType Clr(Type type) => new(JsStaticTypeKind.Clr, type);
    public static JsStaticType Parse(string text) => text.Trim() switch
    {
        "number" or "double" => Number,
        "string" => String,
        "boolean" or "bool" => Boolean,
        "void" => Void,
        "Document" or "DomDocument" => Clr(typeof(DomDocument)),
        "Element" or "HTMLElement" or "HTMLButtonElement" or "DomElement" => Clr(typeof(DomElement)),
        "TextNode" or "DomTextNode" => Clr(typeof(DomTextNode)),
        "Event" or "DomEvent" => Clr(typeof(DomEvent)),
        "HTMLCanvasElement" or "Canvas" => Clr(typeof(HTMLCanvasElement)),
        "CanvasRenderingContext2D" => Clr(typeof(CanvasRenderingContext2D)),
        "WebGLRenderingContext" => Clr(typeof(WebGLRenderingContext)),
        _ => Object
    };
}

public sealed record FunctionAnnotation(IReadOnlyDictionary<string, JsStaticType> Parameters, JsStaticType ReturnType, bool IsInferred = false);
public sealed record JsFunctionDeclaration(string Name, IReadOnlyList<string> Parameters, FunctionAnnotation? Annotation, IReadOnlyList<JsStatement> Body, SourceSpan Span);

public abstract record JsStatement;
public sealed record JsBlockStatement(IReadOnlyList<JsStatement> Statements) : JsStatement;
public sealed record JsVariableStatement(string Name, JsExpression Initializer) : JsStatement;
public sealed record JsReturnStatement(JsExpression? Value) : JsStatement;
public sealed record JsExpressionStatement(JsExpression Expression) : JsStatement;

public sealed record JsIfStatement(JsExpression Test, JsStatement Consequent, JsStatement? Alternate) : JsStatement;
public sealed record JsWhileStatement(JsExpression Test, JsStatement Body) : JsStatement;
public sealed record JsForStatement(JsStatement? Init, JsExpression? Test, JsStatement? Update, JsStatement Body) : JsStatement;
public sealed record JsBreakStatement : JsStatement;
public sealed record JsContinueStatement : JsStatement;
public sealed record JsThrowStatement(JsExpression Value) : JsStatement;
public sealed record JsTryStatement(JsStatement Block, string? HandlerParam, JsStatement? HandlerBlock, JsStatement? Finalizer) : JsStatement;
public sealed record JsSwitchCase(JsExpression? Test, IReadOnlyList<JsStatement> Consequent);
public sealed record JsSwitchStatement(JsExpression Discriminant, IReadOnlyList<JsSwitchCase> Cases) : JsStatement;

public abstract record JsExpression;
public sealed record JsLiteralExpression(object? Value) : JsExpression;
public sealed record JsIdentifierExpression(string Name) : JsExpression;
public sealed record JsMemberExpression(JsExpression Target, string Member) : JsExpression;
public sealed record JsIndexExpression(JsExpression Target, JsExpression Index) : JsExpression;
public sealed record JsCallExpression(JsExpression Target, IReadOnlyList<JsExpression> Arguments) : JsExpression;
public sealed record JsBinaryExpression(string Operator, JsExpression Left, JsExpression Right) : JsExpression;
public sealed record JsUnaryExpression(string Operator, JsExpression Operand) : JsExpression;
public sealed record JsUpdateExpression(JsExpression Target, string Operator, bool Prefix) : JsExpression;
public sealed record JsArrayExpression(IReadOnlyList<JsExpression> Elements) : JsExpression;
public sealed record JsConditionalExpression(JsExpression Test, JsExpression Consequent, JsExpression Alternate) : JsExpression;
public sealed record JsFunctionExpression(IReadOnlyList<string> Parameters, IReadOnlyList<JsStatement> Body) : JsExpression;
public sealed record JsArrowFunctionExpression(IReadOnlyList<string> Parameters, IReadOnlyList<JsStatement> Body) : JsExpression;
public sealed record JsObjectExpression(IReadOnlyDictionary<string, JsExpression> Properties) : JsExpression;
public sealed record JsNewExpression(string Callee, IReadOnlyList<JsExpression> Arguments) : JsExpression;
public sealed record JsThisExpression : JsExpression;
public sealed record JsTemplateLiteralExpression(IReadOnlyList<string> Quasis, IReadOnlyList<JsExpression> Expressions) : JsExpression;
public sealed record JsAssignmentExpression(JsExpression Target, string Operator, JsExpression Value) : JsExpression;


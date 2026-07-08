using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Jint;
using Acornima;
using Acornima.Ast;
using Expression = System.Linq.Expressions.Expression;
using AstExpression = Acornima.Ast.Expression;

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

public sealed class TypedJintEngine
{
    private readonly TypedJintOptions _options;
    private readonly Engine _jint;
    private readonly ConcurrentDictionary<string, ICompiledFunction> _compiled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _globals = new(StringComparer.Ordinal);

    public TypedJintEngine(TypedJintOptions? options = null)
    {
        _options = options ?? new TypedJintOptions();
        _jint = new Engine(cfg => cfg.AllowClr());
        Document = new DomDocument();
        RegisterDom(Document);
    }

    public Engine Jint => _jint;
    public DomDocument Document { get; }

    public TypedJintEngine SetValue(string name, object? value)
    {
        _globals[name] = value;
        _jint.SetValue(name, value);
        return this;
    }

    public TypedJintEngine RegisterHostObject(string name, object instance) => SetValue(name, instance);

    public TypedJintEngine RegisterDom(DomDocument document)
    {
        SetValue("document", document);
        SetValue("window", new DomWindow(document));
        return this;
    }

    public TypedCompilationResult Execute(string source)
    {
        var result = _options.EnableCompilation
            ? new TypedJsCompiler(_globals, _options).Compile(source)
            : new TypedCompilationResult
            {
                CompiledFunctions = new Dictionary<string, ICompiledFunction>(),
                Fallbacks = new Dictionary<string, FallbackInfo>(),
                Diagnostics = Array.Empty<TypedDiagnostic>()
            };

        foreach (var function in result.CompiledFunctions)
        {
            _compiled[function.Key] = function.Value;
        }

        if (_options.ExecuteOriginalSourceInJint)
        {
            _jint.Execute(source);
        }

        return result;
    }

    public object? Invoke(string functionName, params object?[] arguments)
    {
        if (_compiled.TryGetValue(functionName, out var compiled))
        {
            return compiled.Invoke(arguments);
        }

        return _jint.Invoke(functionName, arguments).ToObject();
    }
}

public sealed class DomWindow
{
    public DomWindow(DomDocument document) => this.document = document;
    public DomDocument document { get; }
}

public class DomEvent
{
    public DomEvent(string type) => this.type = type;
    public string type { get; }
    public DomEventTarget? target { get; internal set; }
    public bool defaultPrevented { get; private set; }
    public void preventDefault() => defaultPrevented = true;
}

public interface IDomEventListener { void HandleEvent(DomEvent ev); }

public class DomEventTarget
{
    private readonly Dictionary<string, List<object>> _listeners = new(StringComparer.Ordinal);

    public void addEventListener(string type, object listener)
    {
        if (!_listeners.TryGetValue(type, out var listeners))
        {
            listeners = new List<object>();
            _listeners[type] = listeners;
        }

        listeners.Add(listener);
    }

    public void removeEventListener(string type, object listener)
    {
        if (_listeners.TryGetValue(type, out var listeners))
        {
            listeners.Remove(listener);
        }
    }

    public bool dispatchEvent(DomEvent ev)
    {
        ev.target = this;
        if (!_listeners.TryGetValue(ev.type, out var listeners))
        {
            return !ev.defaultPrevented;
        }

        foreach (var listener in listeners.ToArray())
        {
            if (listener is IDomEventListener typed) typed.HandleEvent(ev);
            if (listener is Action<DomEvent> action) action(ev);
        }

        return !ev.defaultPrevented;
    }
}

public class DomNode : DomEventTarget
{
    private readonly List<DomNode> _children = new();
    private string? _textContent;

    public DomNode(string nodeName) => this.nodeName = nodeName;
    public string nodeName { get; }
    public DomNode? parentNode { get; private set; }
    public IReadOnlyList<DomNode> childNodes => _children;

    public virtual string? textContent
    {
        get => _children.Count == 0 ? _textContent : string.Concat(_children.Select(x => x.textContent));
        set { _children.Clear(); _textContent = value; }
    }

    public DomNode appendChild(DomNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        child.parentNode = this;
        _children.Add(child);
        return child;
    }

    public DomNode removeChild(DomNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (_children.Remove(child)) child.parentNode = null;
        return child;
    }
}

public sealed class DomTextNode : DomNode
{
    public DomTextNode(string text) : base("#text") => textContent = text;
}

public sealed class DomTokenList
{
    private readonly SortedSet<string> _tokens = new(StringComparer.Ordinal);
    public int length => _tokens.Count;
    public void add(string token) => _tokens.Add(token);
    public void remove(string token) => _tokens.Remove(token);
    public bool contains(string token) => _tokens.Contains(token);
    public void toggle(string token) { if (!_tokens.Remove(token)) _tokens.Add(token); }
    public override string ToString() => string.Join(" ", _tokens);
}

public sealed class CssStyleDeclaration
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    public string? width { get => getPropertyValue("width"); set => SetOrRemove("width", value); }
    public string? height { get => getPropertyValue("height"); set => SetOrRemove("height", value); }
    public string? color { get => getPropertyValue("color"); set => SetOrRemove("color", value); }
    public string? backgroundColor { get => getPropertyValue("background-color"); set => SetOrRemove("background-color", value); }
    public string? display { get => getPropertyValue("display"); set => SetOrRemove("display", value); }
    public string? getPropertyValue(string name) => _values.TryGetValue(name, out var value) ? value : null;
    public void setProperty(string name, string value) => _values[name] = value;
    public void removeProperty(string name) => _values.Remove(name);
    private void SetOrRemove(string name, string? value) { if (value is null) _values.Remove(name); else _values[name] = value; }
}

public class DomElement : DomNode
{
    private readonly Dictionary<string, string> _attributes = new(StringComparer.OrdinalIgnoreCase);
    public DomElement(string tagName) : base(tagName.ToUpperInvariant()) => this.tagName = tagName.ToUpperInvariant();
    public string tagName { get; }
    public string id { get => getAttribute("id") ?? string.Empty; set => setAttribute("id", value); }
    public DomTokenList classList { get; } = new();
    public CssStyleDeclaration style { get; } = new();
    public string? getAttribute(string name) => _attributes.TryGetValue(name, out var value) ? value : null;
    public bool hasAttribute(string name) => _attributes.ContainsKey(name);
    public void setAttribute(string name, string value) => _attributes[name] = value;
    public void removeAttribute(string name) => _attributes.Remove(name);
    public DomElement? querySelector(string selector) => QuerySelectorAllCore(this, selector).FirstOrDefault();
    public IReadOnlyList<DomElement> querySelectorAll(string selector) => QuerySelectorAllCore(this, selector).ToArray();

    internal static IEnumerable<DomElement> QuerySelectorAllCore(DomNode root, string selector)
    {
        foreach (var child in root.childNodes)
        {
            if (child is not DomElement element) continue;
            if (Matches(element, selector)) yield return element;
            foreach (var descendant in QuerySelectorAllCore(element, selector)) yield return descendant;
        }
    }

    private static bool Matches(DomElement element, string selector)
    {
        selector = selector.Trim();
        if (selector.StartsWith('#')) return string.Equals(element.id, selector[1..], StringComparison.Ordinal);
        if (selector.StartsWith('.')) return element.classList.contains(selector[1..]);
        return string.Equals(element.tagName, selector.ToUpperInvariant(), StringComparison.Ordinal);
    }
}

public sealed class DomDocument : DomNode
{
    public DomDocument() : base("#document")
    {
        documentElement = new DomElement("html");
        body = new DomElement("body");
        appendChild(documentElement);
        documentElement.appendChild(body);
    }

    public DomElement documentElement { get; }
    public DomElement body { get; }
    public DomElement createElement(string tagName)
    {
        if (string.Equals(tagName, "canvas", StringComparison.OrdinalIgnoreCase))
        {
            return new HTMLCanvasElement();
        }
        return new DomElement(tagName);
    }
    public DomTextNode createTextNode(string text) => new(text);
    public DomElement? getElementById(string id) => DomElement.QuerySelectorAllCore(this, "#" + id).FirstOrDefault();
    public DomElement? querySelector(string selector) => DomElement.QuerySelectorAllCore(this, selector).FirstOrDefault();
    public IReadOnlyList<DomElement> querySelectorAll(string selector) => DomElement.QuerySelectorAllCore(this, selector).ToArray();
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

public sealed class TypedJsCompiler
{
    private readonly IReadOnlyDictionary<string, object?> _globals;
    private readonly TypedJintOptions _options;
    private readonly List<TypedDiagnostic> _diagnostics = new();
    private readonly Dictionary<string, ICompiledFunction> _compiled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FallbackInfo> _fallbacks = new(StringComparer.Ordinal);

    public TypedJsCompiler(IReadOnlyDictionary<string, object?> globals, TypedJintOptions options)
    {
        _globals = globals;
        _options = options;
    }

    public TypedCompilationResult Compile(string source)
    {
        if (_options.Backend == TypedBackendKind.CSharp)
        {
            CompileCSharpBackend(source);
        }
        else
        {
            CompileExpressionTreesBackend(source);
        }

        return new TypedCompilationResult { CompiledFunctions = _compiled, Fallbacks = _fallbacks, Diagnostics = _diagnostics };
    }

    private void CompileExpressionTreesBackend(string source)
    {
        foreach (var fn in SimpleJsParser.ParseFunctions(source))
        {
            if (_options.CompilationMode == TypedCompilationMode.CompileAnnotatedFunctionsOnly && (fn.Annotation is null || fn.Annotation.IsInferred))
            {
                AddFallback(fn.Name, "Function has no JSDoc type annotation.", fn.Span);
                continue;
            }

            if (fn.Annotation is null)
            {
                AddFallback(fn.Name, "Only annotated functions are supported in phase one.", fn.Span);
                continue;
            }

            try
            {
                var del = new ExpressionTreeBackend(_globals).Compile(fn);
                _compiled[fn.Name] = new CompiledFunction(fn.Name, del);
                AddDiagnostic("TJ0400", TypedDiagnosticSeverity.Info, $"Compiled function '{fn.Name}' to Expression Tree (IL).", fn.Span);
            }
            catch (Exception ex) when (!_options.ThrowOnCompilationFailure)
            {
                AddFallback(fn.Name, ex.Message, fn.Span);
            }
        }
    }

    private void CompileCSharpBackend(string source)
    {
        OptimizedJavaScriptCSharpGenerationResult genResult;
        try
        {
            genResult = OptimizedJavaScriptCSharpGenerator.Generate(source, new OptimizedJavaScriptCSharpGenerationOptions
            {
                ClassName = "ScriptModule",
                EmitNativeMethods = true,
                EmitRuntimeFallback = true,
                EmitAggressiveInlining = true
            });
        }
        catch (Exception ex)
        {
            foreach (var fn in SimpleJsParser.ParseFunctions(source))
            {
                AddFallback(fn.Name, $"C# code generation failed: {ex.Message}", fn.Span);
            }
            return;
        }

        foreach (var diag in genResult.Diagnostics)
        {
            _diagnostics.Add(diag);
        }

        var buildResult = GeneratedCSharpCompiler.CreateScriptInstance(genResult.Source, "ScriptModule");
        if (!buildResult.Success || buildResult.Instance is null)
        {
            foreach (var diag in buildResult.Build.Diagnostics)
            {
                AddDiagnostic("TJ0600", TypedDiagnosticSeverity.Error, $"Roslyn Compilation: {diag.Message} at line {diag.Line}, col {diag.Column}", null);
            }

            foreach (var fn in SimpleJsParser.ParseFunctions(source))
            {
                AddFallback(fn.Name, $"Roslyn compilation failed: {buildResult.Build.DiagnosticsText}", fn.Span);
            }
            return;
        }

        var scriptInstance = (GeneratedCSharpScriptInstance)buildResult.Instance;

        foreach (var fn in SimpleJsParser.ParseFunctions(source))
        {
            if (_options.CompilationMode == TypedCompilationMode.CompileAnnotatedFunctionsOnly && (fn.Annotation is null || fn.Annotation.IsInferred))
            {
                AddFallback(fn.Name, "Function has no JSDoc type annotation.", fn.Span);
                continue;
            }

            if (fn.Annotation is null)
            {
                AddFallback(fn.Name, "Only annotated functions are supported in phase one.", fn.Span);
                continue;
            }

            var isNative = genResult.NativeFunctions.Contains(fn.Name, StringComparer.Ordinal);
            if (isNative)
            {
                try
                {
                    var methodInfo = scriptInstance.ScriptType.GetMethod(fn.Name);
                    if (methodInfo == null)
                    {
                        throw new MissingMethodException(scriptInstance.ScriptType.FullName, fn.Name);
                    }

                    var paramTypes = fn.Parameters.Select(parameter =>
                    {
                        return fn.Annotation.Parameters.TryGetValue(parameter, out var staticType) ? staticType.ClrType : typeof(object);
                    }).ToArray();

                    var delegateType = fn.Annotation.ReturnType.ClrType == typeof(void)
                        ? Expression.GetActionType(paramTypes)
                        : Expression.GetFuncType(paramTypes.Append(fn.Annotation.ReturnType.ClrType).ToArray());

                    var del = Delegate.CreateDelegate(delegateType, methodInfo.IsStatic ? null : scriptInstance.Instance, methodInfo);
                    _compiled[fn.Name] = new GeneratedScriptCompiledFunction(fn.Name, scriptInstance, methodInfo, isNative: true, del);
                    AddDiagnostic("TJ0400", TypedDiagnosticSeverity.Info, $"Compiled function '{fn.Name}' to C# native method.", fn.Span);
                }
                catch (Exception ex) when (!_options.ThrowOnCompilationFailure)
                {
                    AddFallback(fn.Name, $"Roslyn native delegate creation failed: {ex.Message}", fn.Span);
                }
            }
            else
            {
                var runtimeInvokeMethod = scriptInstance.ScriptType.GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public);
                if (runtimeInvokeMethod != null)
                {
                    var paramTypes = fn.Parameters.Select(_ => typeof(object)).ToArray();
                    var delegateType = Expression.GetFuncType(paramTypes.Append(typeof(object)).ToArray());

                    var del = new Func<object?[], object?>(args => scriptInstance.InvokeRuntime(fn.Name, args));
                    _compiled[fn.Name] = new GeneratedScriptCompiledFunction(fn.Name, scriptInstance, runtimeInvokeMethod, isNative: false, del);
                    AddFallback(fn.Name, "Function uses Jint runtime fallback facade on generated C# class.", fn.Span);
                }
                else
                {
                    AddFallback(fn.Name, "Generated Invoke method not found on class.", fn.Span);
                }
            }
        }
    }

    private void AddFallback(string functionName, string reason, SourceSpan? span)
    {
        _fallbacks[functionName] = new FallbackInfo(functionName, reason, span);
        AddDiagnostic("TJ0500", TypedDiagnosticSeverity.Warning, $"Function '{functionName}' uses Jint fallback: {reason}", span);
    }

    private void AddDiagnostic(string code, TypedDiagnosticSeverity severity, string message, SourceSpan? span)
    {
        var diagnostic = new TypedDiagnostic(code, severity, message, span);
        _diagnostics.Add(diagnostic);
        _options.DiagnosticSink?.Invoke(diagnostic);
    }
}

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

public sealed class ExpressionTreeBackend
{
    private readonly IReadOnlyDictionary<string, object?> _globals;
    private readonly Dictionary<string, Expression> _symbols = new(StringComparer.Ordinal);
    private readonly List<ParameterExpression> _locals = new();
    private readonly Stack<LabelTarget> _breakTargets = new();
    private readonly Stack<LabelTarget> _continueTargets = new();
    public ExpressionTreeBackend(IReadOnlyDictionary<string, object?> globals) => _globals = globals;

    public Delegate Compile(JsFunctionDeclaration fn)
    {
        if (fn.Annotation is null) throw new InvalidOperationException("Function must be annotated.");
        var parameters = new List<ParameterExpression>();
        foreach (var name in fn.Parameters)
        {
            if (!fn.Annotation.Parameters.TryGetValue(name, out var type)) throw new NotSupportedException($"Parameter '{name}' has no JSDoc type.");
            var parameter = Expression.Parameter(type.ClrType, name);
            parameters.Add(parameter);
            _symbols[name] = parameter;
        }
        foreach (var global in _globals.Where(x => x.Value is not null)) _symbols[global.Key] = Expression.Constant(global.Value!, global.Value!.GetType());

        var expressions = new List<Expression>();
        var returnTarget = Expression.Label(fn.Annotation.ReturnType.ClrType, "return");
        foreach (var statement in fn.Body) expressions.Add(CompileStatement(statement, returnTarget));
        expressions.Add(Expression.Label(returnTarget, Default(fn.Annotation.ReturnType.ClrType)));
        var body = Expression.Block(_locals, expressions);
        var delegateType = fn.Annotation.ReturnType.ClrType == typeof(void) ? Expression.GetActionType(parameters.Select(x => x.Type).ToArray()) : Expression.GetFuncType(parameters.Select(x => x.Type).Append(fn.Annotation.ReturnType.ClrType).ToArray());
        return Expression.Lambda(delegateType, body, fn.Name, parameters).Compile();
    }

    private Expression CompileStatement(JsStatement statement, LabelTarget returnTarget) => statement switch
    {
        JsBlockStatement block => Expression.Block(block.Statements.Select(x => CompileStatement(x, returnTarget))),
        JsVariableStatement variable => AsVoid(CompileVariable(variable)),
        JsReturnStatement ret => Expression.Return(returnTarget, ret.Value is null ? Default(returnTarget.Type) : ConvertTo(CompileExpression(ret.Value), returnTarget.Type)),
        JsExpressionStatement expr => AsVoid(CompileExpression(expr.Expression)),

        JsIfStatement ifStatement => CompileIf(ifStatement, returnTarget),
        JsWhileStatement whileStatement => CompileWhile(whileStatement, returnTarget),
        JsForStatement forStatement => CompileFor(forStatement, returnTarget),
        JsBreakStatement => _breakTargets.Count == 0 ? throw new NotSupportedException("break can only be used inside a loop.") : Expression.Break(_breakTargets.Peek()),
        JsContinueStatement => _continueTargets.Count == 0 ? throw new NotSupportedException("continue can only be used inside a loop.") : Expression.Continue(_continueTargets.Peek()),
        JsThrowStatement throwStmt => Expression.Throw(Expression.New(typeof(Exception).GetConstructor(new[] { typeof(string) })!, Expression.Call(typeof(Convert).GetMethod("ToString", new[] { typeof(object) })!, Expression.Convert(CompileExpression(throwStmt.Value), typeof(object))))),
        JsTryStatement tryStmt => CompileTryCatchFinally(tryStmt, returnTarget),
        JsSwitchStatement switchStmt => CompileSwitch(switchStmt, returnTarget),
        _ => throw new NotSupportedException($"Unsupported statement '{statement.GetType().Name}'.")
    };

    private Expression CompileTryCatchFinally(JsTryStatement tryStmt, LabelTarget returnTarget)
    {
        var tryBody = CompileStatement(tryStmt.Block, returnTarget);
        CatchBlock? catchBlock = null;
        if (tryStmt.HandlerBlock is not null)
        {
            var exParam = Expression.Parameter(typeof(Exception), "__ex");
            var expressions = new List<Expression>();
            if (!string.IsNullOrEmpty(tryStmt.HandlerParam))
            {
                var msgLocal = Expression.Variable(typeof(string), tryStmt.HandlerParam);
                _locals.Add(msgLocal);
                _symbols[tryStmt.HandlerParam] = msgLocal;
                expressions.Add(Expression.Assign(msgLocal, Expression.Property(exParam, "Message")));
            }
            expressions.Add(CompileStatement(tryStmt.HandlerBlock, returnTarget));
            catchBlock = Expression.Catch(exParam, Expression.Block(expressions));
        }

        var finallyBody = tryStmt.Finalizer is not null ? CompileStatement(tryStmt.Finalizer, returnTarget) : null;
        if (catchBlock is not null && finallyBody is not null)
        {
            return Expression.TryCatchFinally(tryBody, finallyBody, catchBlock);
        }
        if (catchBlock is not null)
        {
            return Expression.TryCatch(tryBody, catchBlock);
        }
        if (finallyBody is not null)
        {
            return Expression.TryFinally(tryBody, finallyBody);
        }
        return tryBody;
    }

    private Expression CompileSwitch(JsSwitchStatement switchStmt, LabelTarget returnTarget)
    {
        var discVal = CompileExpression(switchStmt.Discriminant);
        var switchBreakTarget = Expression.Label("switch_break");
        _breakTargets.Push(switchBreakTarget);

        try
        {
            var cases = switchStmt.Cases;
            var useExpressionSwitch = discVal.Type != typeof(object);
            if (useExpressionSwitch)
            {
                foreach (var c in cases)
                {
                    if (c.Test != null && CompileExpression(c.Test).Type != discVal.Type)
                    {
                        useExpressionSwitch = false;
                        break;
                    }
                }
            }

            if (useExpressionSwitch)
            {
                var switchCases = new List<System.Linq.Expressions.SwitchCase>();
                Expression? defaultBody = null;
                foreach (var c in cases)
                {
                    var bodyExprs = c.Consequent.Select(stmt => CompileStatement(stmt, returnTarget)).ToList();
                    if (bodyExprs.Count == 0) bodyExprs.Add(Expression.Empty());
                    var body = Expression.Block(bodyExprs);

                    if (c.Test is not null)
                    {
                        var testVal = CompileExpression(c.Test);
                        switchCases.Add(Expression.SwitchCase(body, testVal));
                    }
                    else
                    {
                        defaultBody = body;
                    }
                }
                return Expression.Block(
                    Expression.Switch(discVal, defaultBody ?? Expression.Empty(), switchCases.ToArray()),
                    Expression.Label(switchBreakTarget)
                );
            }
            else
            {
                var tempDisc = Expression.Variable(discVal.Type, "__disc");
                _locals.Add(tempDisc);

                Expression resultExpr = Expression.Empty();
                Expression? defaultBody = null;
                
                for (int i = cases.Count - 1; i >= 0; i--)
                {
                    var c = cases[i];
                    var bodyExprs = c.Consequent.Select(stmt => CompileStatement(stmt, returnTarget)).ToList();
                    if (bodyExprs.Count == 0) bodyExprs.Add(Expression.Empty());
                    var body = Expression.Block(bodyExprs);

                    if (c.Test is null)
                    {
                        defaultBody = body;
                    }
                    else
                    {
                        var testVal = CompileExpression(c.Test);
                        var testExpr = CompileBinaryExpression("===", tempDisc, testVal);
                        if (resultExpr == Expression.Empty() && defaultBody != null)
                        {
                            resultExpr = Expression.IfThenElse(testExpr, body, defaultBody);
                        }
                        else
                        {
                            resultExpr = Expression.IfThenElse(testExpr, body, resultExpr);
                        }
                    }
                }

                if (resultExpr == Expression.Empty() && defaultBody != null)
                {
                    resultExpr = defaultBody;
                }

                return Expression.Block(
                    Expression.Assign(tempDisc, discVal),
                    resultExpr,
                    Expression.Label(switchBreakTarget)
                );
            }
        }
        finally
        {
            _breakTargets.Pop();
        }
    }

    private Expression CompileVariable(JsVariableStatement variable)
    {
        var value = CompileExpression(variable.Initializer);
        var local = Expression.Variable(value.Type, variable.Name);
        _locals.Add(local);
        _symbols[variable.Name] = local;
        return Expression.Assign(local, value);
    }

    private Expression CompileAssignmentExpression(JsAssignmentExpression assign)
    {
        if (assign.Target is JsMemberExpression member)
        {
            var inst = CompileExpression(member.Target);
            if (inst.Type == typeof(object))
            {
                var valExpr = CompileExpression(assign.Value);
                if (assign.Operator == "=")
                {
                    var setMethod = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.SetProperty))!;
                    return Expression.Call(setMethod, inst, Expression.Constant(member.Member), Expression.Convert(valExpr, typeof(object)));
                }
                else
                {
                    var getMethod = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.GetProperty))!;
                    var curVal = Expression.Call(getMethod, inst, Expression.Constant(member.Member));
                    var mOp = assign.Operator[..^1];
                    var mComputed = CompileBinaryExpression(mOp, curVal, valExpr);
                    var setMethod = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.SetProperty))!;
                    return Expression.Call(setMethod, inst, Expression.Constant(member.Member), Expression.Convert(mComputed, typeof(object)));
                }
            }
        }
        else if (assign.Target is JsIndexExpression index)
        {
            var inst = CompileExpression(index.Target);
            if (inst.Type == typeof(object))
            {
                var idxExpr = CompileExpression(index.Index);
                var valExpr = CompileExpression(assign.Value);
                if (assign.Operator == "=")
                {
                    var setMethod = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.SetIndex))!;
                    return Expression.Call(setMethod, inst, Expression.Convert(idxExpr, typeof(object)), Expression.Convert(valExpr, typeof(object)));
                }
                else
                {
                    var getMethod = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.GetIndex))!;
                    var curVal = Expression.Call(getMethod, inst, Expression.Convert(idxExpr, typeof(object)));
                    var iOp = assign.Operator[..^1];
                    var iComputed = CompileBinaryExpression(iOp, curVal, valExpr);
                    var setMethod = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.SetIndex))!;
                    return Expression.Call(setMethod, inst, Expression.Convert(idxExpr, typeof(object)), Expression.Convert(iComputed, typeof(object)));
                }
            }
        }

        var target = CompileAssignable(assign.Target);
        var val = CompileExpression(assign.Value);
        if (assign.Operator == "=")
        {
            return Expression.Assign(target, ConvertTo(val, target.Type));
        }

        var op = assign.Operator[..^1];
        var computed = CompileBinaryExpression(op, target, val);
        return Expression.Assign(target, ConvertTo(computed, target.Type));
    }

    private Expression CompileIf(JsIfStatement statement, LabelTarget returnTarget)
    {
        var test = ConvertTo(CompileExpression(statement.Test), typeof(bool));
        var consequent = CompileStatement(statement.Consequent, returnTarget);
        var alternate = statement.Alternate is null ? Expression.Empty() : CompileStatement(statement.Alternate, returnTarget);
        return Expression.IfThenElse(test, consequent, alternate);
    }

    private Expression CompileWhile(JsWhileStatement statement, LabelTarget returnTarget)
    {
        var breakTarget = Expression.Label("while_break");
        var continueTarget = Expression.Label("while_continue");
        _breakTargets.Push(breakTarget);
        _continueTargets.Push(continueTarget);
        try
        {
            return Expression.Loop(
                Expression.Block(
                    Expression.IfThen(Expression.Not(ConvertTo(CompileExpression(statement.Test), typeof(bool))), Expression.Break(breakTarget)),
                    CompileStatement(statement.Body, returnTarget),
                    Expression.Label(continueTarget)),
                breakTarget);
        }
        finally
        {
            _continueTargets.Pop();
            _breakTargets.Pop();
        }
    }

    private Expression CompileFor(JsForStatement statement, LabelTarget returnTarget)
    {
        var breakTarget = Expression.Label("for_break");
        var continueTarget = Expression.Label("for_continue");
        var expressions = new List<Expression>();
        if (statement.Init is not null) expressions.Add(CompileStatement(statement.Init, returnTarget));

        _breakTargets.Push(breakTarget);
        _continueTargets.Push(continueTarget);
        try
        {
            var loopExpressions = new List<Expression>();
            if (statement.Test is not null)
            {
                loopExpressions.Add(Expression.IfThen(Expression.Not(ConvertTo(CompileExpression(statement.Test), typeof(bool))), Expression.Break(breakTarget)));
            }

            loopExpressions.Add(CompileStatement(statement.Body, returnTarget));
            loopExpressions.Add(Expression.Label(continueTarget));
            if (statement.Update is not null) loopExpressions.Add(CompileStatement(statement.Update, returnTarget));
            expressions.Add(Expression.Loop(Expression.Block(loopExpressions), breakTarget));
            return Expression.Block(expressions);
        }
        finally
        {
            _continueTargets.Pop();
            _breakTargets.Pop();
        }
    }

    private Expression CompileExpression(JsExpression expression) => expression switch
    {
        JsLiteralExpression literal => CompileLiteral(literal.Value),
        JsIdentifierExpression identifier => _symbols.TryGetValue(identifier.Name, out var symbol) ? symbol : throw new NotSupportedException($"Unknown identifier '{identifier.Name}'."),
        JsMemberExpression member => CompileMember(member),
        JsIndexExpression index => CompileIndex(index),
        JsCallExpression call => CompileCall(call),
        JsBinaryExpression binary => CompileBinary(binary),
        JsUnaryExpression unary => CompileUnary(unary),
        JsUpdateExpression update => CompileUpdate(update),
        JsArrayExpression array => CompileArray(array),
        JsConditionalExpression conditional => CompileConditional(conditional),
        JsFunctionExpression func => CompileFunctionExpression(func),
        JsArrowFunctionExpression arrow => CompileArrowFunctionExpression(arrow),
        JsObjectExpression obj => CompileObjectExpression(obj),
        JsNewExpression newExpr => CompileNewExpression(newExpr),
        JsThisExpression => Expression.Constant(new object()),
        JsTemplateLiteralExpression temp => CompileTemplateLiteral(temp),
        JsAssignmentExpression assign => CompileAssignmentExpression(assign),
        _ => throw new NotSupportedException($"Unsupported expression '{expression.GetType().Name}'.")
    };

    private Expression CompileObjectExpression(JsObjectExpression obj)
    {
        var dictType = typeof(Dictionary<string, object?>);
        var ctor = dictType.GetConstructor(new[] { typeof(IEqualityComparer<string>) })!;
        var comparer = Expression.Constant(StringComparer.Ordinal);
        var dictVar = Expression.Variable(dictType, "dict");
        _locals.Add(dictVar);

        var expressions = new List<Expression>
        {
            Expression.Assign(dictVar, Expression.New(ctor, comparer))
        };

        var addMethod = dictType.GetMethod("Add")!;
        foreach (var p in obj.Properties)
        {
            var keyConst = Expression.Constant(p.Key);
            var valExpr = Expression.Convert(CompileExpression(p.Value), typeof(object));
            expressions.Add(Expression.Call(dictVar, addMethod, keyConst, valExpr));
        }

        expressions.Add(dictVar);
        return Expression.Block(expressions);
    }

    private Expression CompileNewExpression(JsNewExpression newExpr)
    {
        Type? type = null;
        if (_globals.TryGetValue(newExpr.Callee, out var val) && val is Type t)
        {
            type = t;
        }
        else if (newExpr.Callee == "Error")
        {
            type = typeof(Exception);
        }
        else if (newExpr.Callee == "Event")
        {
            type = typeof(DomEvent);
        }

        if (type is null)
        {
            return Expression.Constant(new object());
        }

        var argExprs = newExpr.Arguments.Select(arg => CompileExpression(arg)).ToList();
        var argTypes = argExprs.Select(x => x.Type).ToArray();
        var ctor = type.GetConstructor(argTypes);
        if (ctor is null)
        {
            ctor = type.GetConstructors().FirstOrDefault();
        }

        if (ctor is null) return Expression.Constant(new object());

        var ctorParams = ctor.GetParameters();
        var alignedArgs = new List<Expression>();
        for (int i = 0; i < ctorParams.Length; i++)
        {
            if (i < argExprs.Count)
            {
                alignedArgs.Add(ConvertTo(argExprs[i], ctorParams[i].ParameterType));
            }
            else
            {
                alignedArgs.Add(Default(ctorParams[i].ParameterType));
            }
        }

        return Expression.New(ctor, alignedArgs);
    }

    private Expression CompileTemplateLiteral(JsTemplateLiteralExpression temp)
    {
        var parts = new List<Expression>();
        var toStringMethod = typeof(Convert).GetMethod("ToString", new[] { typeof(object) })!;
        for (int i = 0; i < temp.Quasis.Count; i++)
        {
            var raw = temp.Quasis[i];
            if (!string.IsNullOrEmpty(raw))
            {
                parts.Add(Expression.Constant(raw));
            }
            if (i < temp.Expressions.Count)
            {
                var expr = CompileExpression(temp.Expressions[i]);
                parts.Add(Expression.Call(toStringMethod, Expression.Convert(expr, typeof(object))));
            }
        }
        if (parts.Count == 0) return Expression.Constant(string.Empty);
        var concatMethod = typeof(string).GetMethod("Concat", new[] { typeof(object), typeof(object) })!;
        Expression current = parts[0];
        for (int i = 1; i < parts.Count; i++)
        {
            current = Expression.Call(concatMethod, Expression.Convert(current, typeof(object)), Expression.Convert(parts[i], typeof(object)));
        }
        return current;
    }

    private Expression CompileFunctionExpression(JsFunctionExpression func)
    {
        var parameters = func.Parameters.Select(p => Expression.Parameter(typeof(object), p)).ToList();
        var nested = new ExpressionTreeBackend(_globals);
        foreach (var p in _symbols) nested._symbols[p.Key] = p.Value;
        foreach (var p in parameters) nested._symbols[p.Name!] = p;

        var bodyExprs = new List<Expression>();
        // Stub return label target for inner return statements
        var innerReturnTarget = Expression.Label(typeof(object), "inner_return");
        foreach (var stmt in func.Body)
        {
            bodyExprs.Add(nested.CompileStatement(stmt, innerReturnTarget));
        }
        bodyExprs.Add(Expression.Label(innerReturnTarget, Expression.Constant(null)));
        var block = Expression.Block(nested._locals, bodyExprs);
        return Expression.Lambda(block, parameters);
    }

    private Expression CompileArrowFunctionExpression(JsArrowFunctionExpression arrow)
    {
        var parameters = arrow.Parameters.Select(p => Expression.Parameter(typeof(object), p)).ToList();
        var nested = new ExpressionTreeBackend(_globals);
        foreach (var p in _symbols) nested._symbols[p.Key] = p.Value;
        foreach (var p in parameters) nested._symbols[p.Name!] = p;

        var bodyExprs = new List<Expression>();
        var innerReturnTarget = Expression.Label(typeof(object), "inner_return");
        foreach (var stmt in arrow.Body)
        {
            bodyExprs.Add(nested.CompileStatement(stmt, innerReturnTarget));
        }
        bodyExprs.Add(Expression.Label(innerReturnTarget, Expression.Constant(null)));
        var block = Expression.Block(nested._locals, bodyExprs);
        return Expression.Lambda(block, parameters);
    }

    private static Expression CompileLiteral(object? value) => value switch { null => Expression.Constant(null), double d => Expression.Constant(d), string s => Expression.Constant(s), bool b => Expression.Constant(b), _ => Expression.Constant(value) };
    private Expression CompileMember(JsMemberExpression member) => BindMember(CompileExpression(member.Target), member.Member);

    private Expression CompileAssignable(JsExpression expression) => expression switch
    {
        JsIdentifierExpression id when _symbols.TryGetValue(id.Name, out var symbol) => symbol,
        JsMemberExpression member => CompileMember(member),
        JsIndexExpression index => CompileIndex(index),
        _ => throw new NotSupportedException("Unsupported assignment target.")
    };

    private Expression CompileCall(JsCallExpression call)
    {
        if (call.Target is not JsMemberExpression member) throw new NotSupportedException("Only method calls are supported in phase one.");
        var instance = CompileExpression(member.Target);
        var args = call.Arguments.Select(CompileExpression).ToArray();
        
        if (instance.Type == typeof(object))
        {
            var invokeMethod = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.InvokeMethod))!;
            var argsArrayExpr = Expression.NewArrayInit(typeof(object), args.Select(x => ConvertTo(x, typeof(object))));
            return Expression.Call(invokeMethod, instance, Expression.Constant(member.Member), argsArrayExpr);
        }

        var method = ResolveMethod(instance.Type, member.Member, args.Select(x => x.Type).ToArray());
        var converted = method.GetParameters().Select((p, i) => ConvertTo(args[i], p.ParameterType));
        return Expression.Call(instance, method, converted);
    }

    private Expression CompileArray(JsArrayExpression array)
    {
        var elements = array.Elements.Select(CompileExpression).ToArray();
        var elementType = InferArrayElementType(elements);
        return Expression.NewArrayInit(elementType, elements.Select(x => ConvertTo(x, elementType)));
    }

    private Expression CompileIndex(JsIndexExpression index)
    {
        var target = CompileExpression(index.Target);
        var indexExpression = CompileExpression(index.Index);
        if (target.Type == typeof(object))
        {
            var getMethod = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.GetIndex))!;
            return Expression.Call(getMethod, target, Expression.Convert(indexExpression, typeof(object)));
        }
        if (target.Type.IsArray)
        {
            return Expression.ArrayAccess(target, ConvertTo(indexExpression, typeof(int)));
        }

        var indexer = target.Type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(x => x.GetIndexParameters().Length == 1 && CanConvert(indexExpression.Type, x.GetIndexParameters()[0].ParameterType));
        if (indexer is not null)
        {
            var parameter = indexer.GetIndexParameters()[0];
            return Expression.MakeIndex(target, indexer, new[] { ConvertTo(indexExpression, parameter.ParameterType) });
        }

        throw new NotSupportedException($"Index access is not supported for '{target.Type.Name}'.");
    }

    private Expression CompileConditional(JsConditionalExpression conditional)
    {
        var test = ConvertTo(CompileExpression(conditional.Test), typeof(bool));
        var consequent = CompileExpression(conditional.Consequent);
        var alternate = CompileExpression(conditional.Alternate);
        var type = CommonType(consequent.Type, alternate.Type);
        return Expression.Condition(test, ConvertTo(consequent, type), ConvertTo(alternate, type));
    }

    private static Expression BindMember(Expression target, string member)
    {
        if (target.Type == typeof(object))
        {
            var method = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.GetProperty))!;
            return Expression.Call(method, target, Expression.Constant(member));
        }
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        var property = target.Type.GetProperty(member, flags);
        if (property is not null) return Expression.Property(target, property);
        var field = target.Type.GetField(member, flags);
        if (field is not null) return Expression.Field(target, field);
        throw new NotSupportedException($"Member '{member}' not found on '{target.Type.Name}'.");
    }

    private static MethodInfo ResolveMethod(Type type, string name, IReadOnlyList<Type> argumentTypes)
    {
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase).Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && x.GetParameters().Length == argumentTypes.Count))
        {
            var parameters = method.GetParameters();
            var ok = true;
            for (var i = 0; i < parameters.Length; i++) if (!CanConvert(argumentTypes[i], parameters[i].ParameterType)) { ok = false; break; }
            if (ok) return method;
        }
        throw new NotSupportedException($"Method '{name}' with {argumentTypes.Count} argument(s) not found on '{type.Name}'.");
    }

    private Expression CompileUnary(JsUnaryExpression unary)
    {
        var operand = CompileExpression(unary.Operand);
        return unary.Operator switch
        {
            "+" => ConvertTo(operand, typeof(double)),
            "-" => Expression.Negate(ConvertTo(operand, typeof(double))),
            "!" => Expression.Not(ConvertTo(operand, typeof(bool))),
            _ => throw new NotSupportedException($"Unary operator '{unary.Operator}' is not supported.")
        };
    }

    private Expression CompileUpdate(JsUpdateExpression update)
    {
        var target = CompileAssignable(update.Target);
        return update.Operator switch
        {
            "++" => update.Prefix ? Expression.PreIncrementAssign(target) : Expression.PostIncrementAssign(target),
            "--" => update.Prefix ? Expression.PreDecrementAssign(target) : Expression.PostDecrementAssign(target),
            _ => throw new NotSupportedException($"Update operator '{update.Operator}' is not supported.")
        };
    }

    private Expression CompileBinary(JsBinaryExpression binary)
    {
        var left = CompileExpression(binary.Left);
        var right = CompileExpression(binary.Right);
        if (binary.Operator == "||")
        {
            var method = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.LogicalOr))!;
            return Expression.Call(method, Expression.Convert(left, typeof(object)), Expression.Convert(right, typeof(object)));
        }
        if (binary.Operator == "&&")
        {
            var method = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.LogicalAnd))!;
            return Expression.Call(method, Expression.Convert(left, typeof(object)), Expression.Convert(right, typeof(object)));
        }
        if (binary.Operator == "??")
        {
            var method = typeof(JavaScriptRuntimeEngine).GetMethod(nameof(JavaScriptRuntimeEngine.Coalesce))!;
            return Expression.Call(method, Expression.Convert(left, typeof(object)), Expression.Convert(right, typeof(object)));
        }
        return CompileBinaryExpression(binary.Operator, left, right);
    }

    private static Expression CompileBinaryExpression(string op, Expression left, Expression right)
    {
        return op switch
        {
            "+" when left.Type == typeof(string) || right.Type == typeof(string) => Expression.Call(typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(object), typeof(object) })!, Expression.Convert(left, typeof(object)), Expression.Convert(right, typeof(object))),
            "+" => Expression.Add(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "-" => Expression.Subtract(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "*" => Expression.Multiply(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "/" => Expression.Divide(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "%" => Expression.Modulo(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "<" => Expression.LessThan(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "<=" => Expression.LessThanOrEqual(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            ">" => Expression.GreaterThan(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            ">=" => Expression.GreaterThanOrEqual(ConvertTo(left, typeof(double)), ConvertTo(right, typeof(double))),
            "==" or "===" => Expression.Equal(ConvertComparable(left, right), ConvertComparable(right, left)),
            "!=" or "!==" => Expression.NotEqual(ConvertComparable(left, right), ConvertComparable(right, left)),
            "&&" => Expression.AndAlso(ConvertTo(left, typeof(bool)), ConvertTo(right, typeof(bool))),
            "||" => Expression.OrElse(ConvertTo(left, typeof(bool)), ConvertTo(right, typeof(bool))),
            _ => throw new NotSupportedException($"Operator '{op}' is not supported.")
        };
    }

    private static Expression ConvertComparable(Expression expression, Expression other)
    {
        if (expression.Type == other.Type) return expression;
        if (expression.Type == typeof(object)) return expression;
        if (other.Type == typeof(object)) return ConvertTo(expression, typeof(object));
        return ConvertTo(expression, other.Type);
    }

    private static Type InferArrayElementType(IReadOnlyList<Expression> elements)
    {
        if (elements.Count == 0) return typeof(object);
        var first = elements[0].Type;
        return elements.All(x => x.Type == first) ? first : typeof(object);
    }

    private static Type CommonType(Type left, Type right)
    {
        if (left == right) return left;
        if (left == typeof(double) && right == typeof(int) || left == typeof(int) && right == typeof(double)) return typeof(double);
        return typeof(object);
    }

    private static bool CanConvert(Type source, Type target) => target.IsAssignableFrom(source) || source == typeof(double) && target == typeof(int) || source == typeof(int) && target == typeof(double) || target == typeof(object);
    private static Expression ConvertTo(Expression expression, Type targetType) => expression.Type == targetType ? expression : Expression.Convert(expression, targetType);
    private static Expression Default(Type type) => type == typeof(void) ? Expression.Empty() : Expression.Default(type);
    private static Expression AsVoid(Expression expression) => expression.Type == typeof(void) ? expression : Expression.Block(expression, Expression.Empty());
}

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

public static class JavaScriptTypeInferenceEngine
{
    private sealed class TypeTerm
    {
        public string? Name { get; set; }
        public JsStaticType? ConcreteType { get; set; }
        public TypeTerm? Parent { get; set; }

        public TypeTerm Find()
        {
            var current = this;
            while (current.Parent != null)
            {
                current = current.Parent;
            }
            return current;
        }

        public void Unify(TypeTerm other)
        {
            var root1 = this.Find();
            var root2 = other.Find();
            if (root1 == root2) return;

            if (root1.ConcreteType != null && root2.ConcreteType != null)
            {
                if (root1.ConcreteType != root2.ConcreteType)
                {
                    root1.ConcreteType = JsStaticType.Object;
                }
                root2.Parent = root1;
            }
            else if (root1.ConcreteType != null)
            {
                root2.Parent = root1;
            }
            else
            {
                root1.Parent = root2;
            }
        }
    }

    public static FunctionAnnotation Infer(FunctionDeclaration function)
    {
        var terms = new Dictionary<string, TypeTerm>(StringComparer.Ordinal);
        var returnTerm = new TypeTerm { ConcreteType = null };

        var paramNames = new List<string>();
        foreach (var p in function.Params)
        {
            if (p is Acornima.Ast.Identifier id)
            {
                paramNames.Add(id.Name);
                terms[id.Name] = new TypeTerm { Name = id.Name, ConcreteType = null };
            }
            else
            {
                var name = p.ToString() ?? "";
                paramNames.Add(name);
                terms[name] = new TypeTerm { Name = name, ConcreteType = null };
            }
        }

        var visitor = new InferenceVisitor(terms, returnTerm);
        visitor.Visit(function.Body);

        var inferredParams = new Dictionary<string, JsStaticType>(StringComparer.Ordinal);
        foreach (var name in paramNames)
        {
            var term = terms[name].Find();
            inferredParams[name] = term.ConcreteType ?? JsStaticType.Number;
        }

        var inferredReturn = returnTerm.Find().ConcreteType ?? JsStaticType.Void;

        return new FunctionAnnotation(inferredParams, inferredReturn);
    }

    private sealed class InferenceVisitor : AstVisitor
    {
        private readonly Dictionary<string, TypeTerm> _terms;
        private readonly TypeTerm _returnTerm;

        public InferenceVisitor(Dictionary<string, TypeTerm> terms, TypeTerm returnTerm)
        {
            _terms = terms;
            _returnTerm = returnTerm;
        }

        private TypeTerm GetOrRegisterTerm(string name)
        {
            if (!_terms.TryGetValue(name, out var term))
            {
                term = new TypeTerm { Name = name };
                _terms[name] = term;
            }
            return term;
        }

        private TypeTerm GetExpressionTerm(Acornima.Ast.Expression? expr)
        {
            if (expr is null) return new TypeTerm { ConcreteType = JsStaticType.Object };

            switch (expr)
            {
                case Acornima.Ast.Literal lit:
                    if (lit is NumericLiteral) return new TypeTerm { ConcreteType = JsStaticType.Number };
                    if (lit is StringLiteral) return new TypeTerm { ConcreteType = JsStaticType.String };
                    if (lit is BooleanLiteral) return new TypeTerm { ConcreteType = JsStaticType.Boolean };
                    return new TypeTerm { ConcreteType = JsStaticType.Object };

                case Acornima.Ast.Identifier id:
                    return GetOrRegisterTerm(id.Name);

                case Acornima.Ast.LogicalExpression log:
                    var lLogTerm = GetExpressionTerm(log.Left);
                    _ = GetExpressionTerm(log.Right);
                    if (log.Operator == Operator.NullishCoalescing)
                    {
                        lLogTerm.Unify(new TypeTerm { ConcreteType = JsStaticType.Object });
                    }
                    return new TypeTerm();

                case Acornima.Ast.BinaryExpression bin:
                    var leftTerm = GetExpressionTerm(bin.Left);
                    var rightTerm = GetExpressionTerm(bin.Right);
                    var resultTerm = new TypeTerm();

                    var op = bin.Operator;
                    if (op == Operator.Addition || op == Operator.Subtraction || op == Operator.Multiplication ||
                        op == Operator.Division || op == Operator.Remainder || op == Operator.BitwiseAnd ||
                        op == Operator.BitwiseOr || op == Operator.BitwiseXor || op == Operator.LeftShift ||
                        op == Operator.RightShift || op == Operator.UnsignedRightShift)
                    {
                        leftTerm.Unify(new TypeTerm { ConcreteType = JsStaticType.Number });
                        rightTerm.Unify(new TypeTerm { ConcreteType = JsStaticType.Number });
                        resultTerm.ConcreteType = JsStaticType.Number;
                    }
                    else if (op == Operator.LessThan || op == Operator.GreaterThan || op == Operator.LessThanOrEqual ||
                             op == Operator.GreaterThanOrEqual || op == Operator.Equality || op == Operator.Inequality ||
                             op == Operator.StrictEquality || op == Operator.StrictInequality)
                    {
                        var lType = leftTerm.Find().ConcreteType;
                        var rType = rightTerm.Find().ConcreteType;
                        if (lType != null && rType == null) rightTerm.Unify(leftTerm);
                        else if (rType != null && lType == null) leftTerm.Unify(rightTerm);

                        resultTerm.ConcreteType = JsStaticType.Boolean;
                    }
                    else
                    {
                        resultTerm.ConcreteType = JsStaticType.Object;
                    }
                    return resultTerm;

                case Acornima.Ast.UpdateExpression upd:
                    var updArg = GetExpressionTerm(upd.Argument);
                    updArg.Unify(new TypeTerm { ConcreteType = JsStaticType.Number });
                    return new TypeTerm { ConcreteType = JsStaticType.Number };

                case Acornima.Ast.UnaryExpression un:
                    var argTerm = GetExpressionTerm(un.Argument);
                    if (un.Operator == Operator.LogicalNot)
                    {
                        return new TypeTerm { ConcreteType = JsStaticType.Boolean };
                    }
                    if (un.Operator == Operator.UnaryPlus || un.Operator == Operator.UnaryNegation || un.Operator == Operator.BitwiseNot)
                    {
                        argTerm.Unify(new TypeTerm { ConcreteType = JsStaticType.Number });
                        return new TypeTerm { ConcreteType = JsStaticType.Number };
                    }
                    if (un.Operator == Operator.TypeOf)
                    {
                        return new TypeTerm { ConcreteType = JsStaticType.String };
                    }
                    return new TypeTerm { ConcreteType = JsStaticType.Object };

                case Acornima.Ast.AssignmentExpression assign:
                    var target = GetExpressionTerm(assign.Left as Acornima.Ast.Expression);
                    var val = GetExpressionTerm(assign.Right);
                    target.Unify(val);
                    return target;

                case Acornima.Ast.CallExpression call:
                    if (call.Callee is Acornima.Ast.MemberExpression mem && mem.Object is Acornima.Ast.Identifier idObj && idObj.Name == "Math")
                    {
                        return new TypeTerm { ConcreteType = JsStaticType.Number };
                    }
                    if (call.Callee is Acornima.Ast.Identifier idCall)
                    {
                        if (idCall.Name == "fetch") return new TypeTerm { ConcreteType = JsStaticType.Object };
                        if (idCall.Name == "setTimeout" || idCall.Name == "setInterval") return new TypeTerm { ConcreteType = JsStaticType.Number };
                    }
                    return new TypeTerm { ConcreteType = JsStaticType.Object };

                case Acornima.Ast.ConditionalExpression cond:
                    var test = GetExpressionTerm(cond.Test);
                    test.Unify(new TypeTerm { ConcreteType = JsStaticType.Boolean });
                    var cons = GetExpressionTerm(cond.Consequent);
                    var alt = GetExpressionTerm(cond.Alternate);
                    cons.Unify(alt);
                    return cons;

                default:
                    return new TypeTerm { ConcreteType = JsStaticType.Object };
            }
        }

        protected override object? VisitVariableDeclarator(VariableDeclarator node)
        {
            if (node.Id is Acornima.Ast.Identifier id)
            {
                var varTerm = GetOrRegisterTerm(id.Name);
                if (node.Init != null)
                {
                    var initTerm = GetExpressionTerm(node.Init);
                    varTerm.Unify(initTerm);
                }
            }
            return base.VisitVariableDeclarator(node);
        }

        protected override object? VisitReturnStatement(ReturnStatement node)
        {
            if (node.Argument != null)
            {
                var retTerm = GetExpressionTerm(node.Argument);
                _returnTerm.Unify(retTerm);
            }
            else
            {
                _returnTerm.Unify(new TypeTerm { ConcreteType = JsStaticType.Void });
            }
            return base.VisitReturnStatement(node);
        }

        protected override object? VisitExpressionStatement(ExpressionStatement node)
        {
            GetExpressionTerm(node.Expression);
            return base.VisitExpressionStatement(node);
        }

        protected override object? VisitIfStatement(IfStatement node)
        {
            var testTerm = GetExpressionTerm(node.Test);
            testTerm.Unify(new TypeTerm { ConcreteType = JsStaticType.Boolean });
            return base.VisitIfStatement(node);
        }

        protected override object? VisitWhileStatement(WhileStatement node)
        {
            var testTerm = GetExpressionTerm(node.Test);
            testTerm.Unify(new TypeTerm { ConcreteType = JsStaticType.Boolean });
            return base.VisitWhileStatement(node);
        }

        protected override object? VisitForStatement(ForStatement node)
        {
            if (node.Test != null)
            {
                var testTerm = GetExpressionTerm(node.Test);
                testTerm.Unify(new TypeTerm { ConcreteType = JsStaticType.Boolean });
            }
            return base.VisitForStatement(node);
        }
    }
}

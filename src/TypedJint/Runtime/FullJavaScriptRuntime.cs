using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using Acornima;
using Acornima.Ast;
using TypedJint.Runtime;

namespace TypedJint;

public sealed class JavaScriptRuntimeOptions
{
    public bool AllowClr { get; init; } = true;
    public bool DrainPromiseJobsAfterInvoke { get; init; } = true;
    public Action<TypedDiagnostic>? DiagnosticSink { get; init; }
    public DomDocument? Document { get; init; }
}

public sealed class JavaScriptRuntimeResult
{
    public required IReadOnlyDictionary<string, ICompiledFunction> RuntimeFunctions { get; init; }
    public required IReadOnlyList<string> ClassDeclarations { get; init; }
    public required IReadOnlyList<TypedDiagnostic> Diagnostics { get; init; }

    public bool Verified => Diagnostics.All(x => x.Severity != TypedDiagnosticSeverity.Error);
}

public sealed class JavaScriptRuntimeEngine
{
    private static readonly System.Threading.AsyncLocal<JavaScriptRuntimeEngine?> _currentEngine = new();
    private static JavaScriptRuntimeEngine? _globalEngine;
    public static JavaScriptRuntimeEngine? CurrentEngine
    {
        get => _currentEngine.Value ?? _globalEngine;
        set
        {
            _currentEngine.Value = value;
            _globalEngine = value;
            if (value != null)
            {
                if (_fallbackDocument == null && value.Document != null) _fallbackDocument = value.Document;
                if (_fallbackWindow == null && value.Window != null) _fallbackWindow = value.Window;
            }
        }
    }

    private static readonly System.Threading.AsyncLocal<DomDocument?> _currentDocument = new();
    private static readonly System.Threading.AsyncLocal<DomWindow?> _currentWindow = new();

    public static DomDocument CurrentDocument
    {
        get => _currentDocument.Value ?? CurrentEngine?.Document ?? (_fallbackDocument ??= new DomDocument());
        set
        {
            _currentDocument.Value = value;
            if (value != null) _fallbackDocument = value;
        }
    }

    public static DomWindow CurrentWindow
    {
        get => _currentWindow.Value ?? CurrentEngine?.Window ?? (_fallbackWindow ??= new DomWindow(CurrentDocument));
        set
        {
            _currentWindow.Value = value;
            if (value != null) _fallbackWindow = value;
        }
    }

    private static DomDocument? _fallbackDocument;
    private static DomWindow? _fallbackWindow;

    private readonly JavaScriptRuntimeOptions _options;
    private readonly Engine _engine;
    private readonly Dictionary<string, ICompiledFunction> _runtimeFunctions = new(StringComparer.Ordinal);

    public JavaScriptRuntimeEngine(JavaScriptRuntimeOptions? options = null)
    {
        _options = options ?? new JavaScriptRuntimeOptions();
        _engine = _options.AllowClr ? new Engine(cfg => cfg.AllowClr()) : new Engine();
        Document = _options.Document ?? new DomDocument();
        Window = new DomWindow(Document);
        SetValue("document", Document);
        SetValue("window", Window);
    }

    public Engine Jint => _engine;
    public DomDocument Document { get; }
    public DomWindow Window { get; }
    public IReadOnlyDictionary<string, ICompiledFunction> RuntimeFunctions => new ReadOnlyDictionary<string, ICompiledFunction>(_runtimeFunctions);

    public JavaScriptRuntimeEngine SetValue(string name, object? value)
    {
        _engine.SetValue(name, value);
        return this;
    }

    public JavaScriptRuntimeEngine RegisterHostObject(string name, object instance) => SetValue(name, instance);

    public JavaScriptRuntimeResult Execute(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var oldEngine = CurrentEngine;
        CurrentEngine = this;
        try
        {
            var diagnostics = new List<TypedDiagnostic>();
            _engine.Execute(source);
            DrainPromiseJobs();

            var declarations = JavaScriptDeclarationScanner.Scan(source);
            _runtimeFunctions.Clear();

            foreach (var functionName in declarations.Functions)
            {
                var function = new JavaScriptRuntimeFunction(functionName, _engine, _options.DrainPromiseJobsAfterInvoke);
                _runtimeFunctions[functionName] = function;
                diagnostics.Add(new TypedDiagnostic(
                    "TJ1000",
                    TypedDiagnosticSeverity.Info,
                    $"Function '{functionName}' is available through the JavaScript runtime backend."));
            }

            foreach (var className in declarations.Classes)
            {
                diagnostics.Add(new TypedDiagnostic(
                    "TJ1001",
                    TypedDiagnosticSeverity.Info,
                    $"Class '{className}' is available through the JavaScript runtime backend."));
            }

            foreach (var diagnostic in diagnostics)
            {
                _options.DiagnosticSink?.Invoke(diagnostic);
            }

            return new JavaScriptRuntimeResult
            {
                RuntimeFunctions = new ReadOnlyDictionary<string, ICompiledFunction>(_runtimeFunctions),
                ClassDeclarations = declarations.Classes,
                Diagnostics = diagnostics
            };
        }
        finally
        {
            CurrentEngine = oldEngine;
        }
    }

    public object? Invoke(string functionName, params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);

        var oldEngine = CurrentEngine;
        CurrentEngine = this;
        try
        {
            if (_runtimeFunctions.TryGetValue(functionName, out var function))
            {
                return function.Invoke(arguments);
            }

            var result = _engine.Invoke(functionName, arguments).ToObject();
            DrainPromiseJobs();
            return result;
        }
        finally
        {
            CurrentEngine = oldEngine;
        }
    }

    public object? Evaluate(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var oldEngine = CurrentEngine;
        CurrentEngine = this;
        try
        {
            var result = _engine.Evaluate(source).ToObject();
            DrainPromiseJobs();
            return result;
        }
        finally
        {
            CurrentEngine = oldEngine;
        }
    }

    public void DrainPromiseJobs()
    {
        if (!_options.DrainPromiseJobsAfterInvoke)
        {
            return;
        }

        var advanced = _engine.GetType().GetProperty("Advanced", BindingFlags.Instance | BindingFlags.Public)?.GetValue(_engine);
        TryInvokeNoArgs(advanced, "ProcessTasks");
        TryInvokeNoArgs(advanced, "RunAvailableContinuations");
        TryInvokeNoArgs(advanced, "RunJobs");
    }

    private static void TryInvokeNoArgs(object? target, string methodName)
    {
        if (target is null)
        {
            return;
        }

        try
        {
            var method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            method?.Invoke(target, null);
        }
        catch (TargetInvocationException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    public static object? UnwrapJsValue(JsValue value)
    {
        if (value is null || value.IsNull() || value.IsUndefined()) return null;
        if (value.IsObject())
        {
            var obj = value.AsObject();
            if (obj is Jint.Runtime.Interop.ObjectWrapper wrapper)
            {
                return wrapper.Target;
            }
            return value;
        }
        return value.ToObject();
    }

    public static object? InvokeMethod(object? target, string methodName, object?[] args)
    {
        if (target is null) return null;
        
        // 1. Check if target is a raw Jint JsValue (to support prototype and dynamic invocation chains)
        if (target is JsValue jsVal)
        {
            var engine = JavaScriptRuntimeEngine.CurrentEngine?.Jint;
            if (engine is not null)
            {
                var memberVal = jsVal.Get(methodName);
                if (memberVal is not null && !memberVal.IsUndefined() && !memberVal.IsNull())
                {
                    var jsArgs = new JsValue[args.Length];
                    for (int i = 0; i < args.Length; i++)
                    {
                        jsArgs[i] = JsValue.FromObject(engine, args[i]);
                    }
                    var resultVal = engine.Invoke(memberVal, jsVal, jsArgs);
                    return UnwrapJsValue(resultVal);
                }
            }
        }

        // 2. Check if target is a dictionary/ExpandoObject
        if (target is IDictionary<string, object?> dict)
        {
            if (dict.TryGetValue(methodName, out var memberVal) && memberVal is not null)
            {
                if (memberVal is Delegate del)
                {
                    var delType = del.GetType();
                    var invokeM = delType.GetMethod("Invoke");
                    if (invokeM != null)
                    {
                        var parameters = invokeM.GetParameters();
                        if (parameters.Length == 2 && 
                            parameters[0].ParameterType.Name == "JsValue" && 
                            parameters[1].ParameterType.IsArray && 
                            parameters[1].ParameterType.GetElementType() is Type elemType && 
                            elemType.Name == "JsValue")
                        {
                            var engine = JavaScriptRuntimeEngine.CurrentEngine;
                            if (engine is null) return null;
                            var jintEngine = engine.Jint;
                            var thisValue = JsValue.FromObject(jintEngine, target);
                            var jsArgs = new JsValue[args.Length];
                            for (int i = 0; i < args.Length; i++)
                            {
                                jsArgs[i] = JsValue.FromObject(jintEngine, args[i]);
                            }
                            var result = del.DynamicInvoke(thisValue, jsArgs);
                            if (result is JsValue jsRes)
                            {
                                return UnwrapJsValue(jsRes);
                            }
                            return result;
                        }
                        
                        var convertedArgs = new object?[parameters.Length];
                        for (int i = 0; i < parameters.Length; i++)
                        {
                            if (i < args.Length)
                            {
                                var arg = args[i];
                                var pType = parameters[i].ParameterType;
                                if (arg == null)
                                {
                                    convertedArgs[i] = null;
                                }
                                else if (pType == typeof(JsValue))
                                {
                                    var engine = JavaScriptRuntimeEngine.CurrentEngine;
                                    convertedArgs[i] = engine is null ? JsValue.Null : JsValue.FromObject(engine.Jint, arg);
                                }
                                else if (pType == typeof(object) || pType.IsInstanceOfType(arg))
                                {
                                    convertedArgs[i] = arg;
                                }
                                else
                                {
                                    convertedArgs[i] = Convert.ChangeType(arg, pType, CultureInfo.InvariantCulture);
                                }
                            }
                            else
                            {
                                convertedArgs[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
                            }
                        }
                        var res = del.DynamicInvoke(convertedArgs);
                        if (res is JsValue jsValRes)
                        {
                            return UnwrapJsValue(jsValRes);
                        }
                        return res;
                    }
                }
            }
        }

        var type = target.GetType();
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        
        MethodInfo? method = null;
        foreach (var m in type.GetMethods(flags).Where(x => string.Equals(x.Name, methodName, StringComparison.OrdinalIgnoreCase)))
        {
            var parameters = m.GetParameters();
            if (parameters.Length == args.Length && (args.Length == 0 || !parameters[^1].ParameterType.IsArray || !parameters[^1].IsDefined(typeof(ParamArrayAttribute), false)))
            {
                method = m;
                break;
            }
            if (parameters.Length > 0 && parameters[^1].ParameterType.IsArray && parameters[^1].IsDefined(typeof(ParamArrayAttribute), false))
            {
                if (args.Length >= parameters.Length - 1)
                {
                    method = m;
                    break;
                }
            }
        }
            
        if (method != null)
        {
            var parameters = method.GetParameters();
            var convertedArgs = new object?[parameters.Length];
            var hasParams = parameters.Length > 0 && parameters[^1].ParameterType.IsArray && parameters[^1].IsDefined(typeof(ParamArrayAttribute), false);
            
            int normalParamCount = hasParams ? parameters.Length - 1 : parameters.Length;
            for (int i = 0; i < normalParamCount; i++)
            {
                var pType = parameters[i].ParameterType;
                var arg = i < args.Length ? args[i] : null;
                if (arg == null)
                {
                    convertedArgs[i] = null;
                }
                else if (pType == typeof(object) || pType.IsInstanceOfType(arg))
                {
                    convertedArgs[i] = arg;
                }
                else
                {
                    convertedArgs[i] = Convert.ChangeType(arg, pType, CultureInfo.InvariantCulture);
                }
            }
            
            if (hasParams)
            {
                var paramsParam = parameters[^1];
                var elementType = paramsParam.ParameterType.GetElementType()!;
                int paramsCount = args.Length - normalParamCount;
                var paramsArray = Array.CreateInstance(elementType, paramsCount);
                for (int i = 0; i < paramsCount; i++)
                {
                    var arg = args[normalParamCount + i];
                    if (arg == null)
                    {
                        paramsArray.SetValue(null, i);
                    }
                    else if (elementType == typeof(object) || elementType.IsInstanceOfType(arg))
                    {
                        paramsArray.SetValue(arg, i);
                    }
                    else
                    {
                        paramsArray.SetValue(Convert.ChangeType(arg, elementType, CultureInfo.InvariantCulture), i);
                    }
                }
                convertedArgs[^1] = paramsArray;
            }
            
            return method.Invoke(target, convertedArgs);
        }
        
        throw new NotSupportedException($"Method '{methodName}' with {args.Length} argument(s) not found on '{type.Name}'.");
    }

    public static dynamic? GetProperty(object? target, string member)
    {
        if (target is null) return null;
        if (target is JsValue jsVal)
        {
            var engine = JavaScriptRuntimeEngine.CurrentEngine?.Jint;
            if (engine is not null)
            {
                var val = jsVal.Get(member);
                return UnwrapJsValue(val);
            }
        }
        var type = target.GetType();
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        var prop = type.GetProperty(member, flags);
        if (prop != null) return prop.GetValue(target);
        var field = type.GetField(member, flags);
        if (field != null) return field.GetValue(target);
        if (target is IDictionary<string, object?> dict)
        {
            return dict.TryGetValue(member, out var val) ? val : null;
        }
        return null;
    }

    public static object? SetProperty(object? target, string member, object? value)
    {
        if (target is null) return null;
        if (target is JsValue jsVal)
        {
            var engine = JavaScriptRuntimeEngine.CurrentEngine?.Jint;
            if (engine is not null)
            {
                var jsValVal = JsValue.FromObject(engine, value);
                jsVal.Set(member, jsValVal, jsVal);
                return value;
            }
        }
        var type = target.GetType();
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        var prop = type.GetProperty(member, flags);
        if (prop != null && prop.CanWrite)
        {
            var converted = value == null ? null : Convert.ChangeType(value, prop.PropertyType, CultureInfo.InvariantCulture);
            prop.SetValue(target, converted);
            return value;
        }
        var field = type.GetField(member, flags);
        if (field != null)
        {
            var converted = value == null ? null : Convert.ChangeType(value, field.FieldType, CultureInfo.InvariantCulture);
            field.SetValue(target, converted);
            return value;
        }
        if (target is IDictionary<string, object?> dict)
        {
            dict[member] = value;
        }
        return value;
    }

    public static object? GetIndex(object? target, object? index)
    {
        if (target is null || index is null) return null;
        if (target is JsValue jsVal)
        {
            var engine = JavaScriptRuntimeEngine.CurrentEngine?.Jint;
            if (engine is not null)
            {
                var idxVal = JsValue.FromObject(engine, index);
                var val = jsVal.Get(idxVal);
                return UnwrapJsValue(val);
            }
        }
        if (target is IDictionary<string, object?> dict)
        {
            var key = Convert.ToString(index, CultureInfo.InvariantCulture) ?? string.Empty;
            return dict.TryGetValue(key, out var val) ? val : null;
        }
        if (target is System.Collections.IList list)
        {
            var idx = Convert.ToInt32(index, CultureInfo.InvariantCulture);
            if (idx >= 0 && idx < list.Count) return list[idx];
            return null;
        }
        var targetType = target.GetType();
        var indexer = targetType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(x => x.GetIndexParameters().Length == 1);
        if (indexer != null)
        {
            var paramType = indexer.GetIndexParameters()[0].ParameterType;
            var convertedIndex = Convert.ChangeType(index, paramType, CultureInfo.InvariantCulture);
            return indexer.GetValue(target, new[] { convertedIndex });
        }
        var propName = Convert.ToString(index, CultureInfo.InvariantCulture) ?? string.Empty;
        return GetProperty(target, propName);
    }

    public static object? SetIndex(object? target, object? index, object? value)
    {
        if (target is null || index is null) return null;
        if (target is JsValue jsVal)
        {
            var engine = JavaScriptRuntimeEngine.CurrentEngine?.Jint;
            if (engine is not null)
            {
                var idxVal = JsValue.FromObject(engine, index);
                var valVal = JsValue.FromObject(engine, value);
                jsVal.Set(idxVal, valVal, jsVal);
                return value;
            }
        }
        if (target is IDictionary<string, object?> dict)
        {
            var key = Convert.ToString(index, CultureInfo.InvariantCulture) ?? string.Empty;
            dict[key] = value;
            return value;
        }
        if (target is System.Collections.IList list)
        {
            var idx = Convert.ToInt32(index, CultureInfo.InvariantCulture);
            if (idx >= 0 && idx < list.Count)
            {
                list[idx] = value;
            }
            return value;
        }
        var targetType = target.GetType();
        var indexer = targetType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(x => x.GetIndexParameters().Length == 1 && x.CanWrite);
        if (indexer != null)
        {
            var paramType = indexer.GetIndexParameters()[0].ParameterType;
            var convertedIndex = Convert.ChangeType(index, paramType, CultureInfo.InvariantCulture);
            var convertedValue = value == null ? null : Convert.ChangeType(value, indexer.PropertyType, CultureInfo.InvariantCulture);
            indexer.SetValue(target, convertedValue, new[] { convertedIndex });
            return value;
        }
        var propName = Convert.ToString(index, CultureInfo.InvariantCulture) ?? string.Empty;
        SetProperty(target, propName, value);
        return value;
    }

    public static bool ToBoolean(object? val)
    {
        if (val is null) return false;
        if (val is bool b) return b;
        if (val is double d) return d != 0 && !double.IsNaN(d);
        if (val is int i) return i != 0;
        if (val is string s) return s.Length > 0;
        return true;
    }

    public static object? LogicalOr(object? left, object? right)
    {
        return ToBoolean(left) ? left : right;
    }

    public static object? LogicalAnd(object? left, object? right)
    {
        return ToBoolean(left) ? right : left;
    }

    public static object? Coalesce(object? left, object? right)
    {
        return left is not null ? left : right;
    }

    public static object? Add(object? left, object? right)
    {
        if (left is string || right is string)
        {
            return string.Concat(left, right);
        }
        return Convert.ToDouble(left, CultureInfo.InvariantCulture) + Convert.ToDouble(right, CultureInfo.InvariantCulture);
    }

    public static double Subtract(object? left, object? right) => Convert.ToDouble(left, CultureInfo.InvariantCulture) - Convert.ToDouble(right, CultureInfo.InvariantCulture);
    public static double Multiply(object? left, object? right) => Convert.ToDouble(left, CultureInfo.InvariantCulture) * Convert.ToDouble(right, CultureInfo.InvariantCulture);
    public static double Divide(object? left, object? right) => Convert.ToDouble(left, CultureInfo.InvariantCulture) / Convert.ToDouble(right, CultureInfo.InvariantCulture);
    public static double Modulo(object? left, object? right) => Convert.ToDouble(left, CultureInfo.InvariantCulture) % Convert.ToDouble(right, CultureInfo.InvariantCulture);
}

public sealed class JavaScriptRuntimeFunction : ICompiledFunction
{
    private readonly Engine _engine;
    private readonly bool _drainPromiseJobs;
    private readonly Func<object?[], object?> _delegate;

    public JavaScriptRuntimeFunction(string name, Engine engine, bool drainPromiseJobs)
    {
        Name = name;
        _engine = engine;
        _drainPromiseJobs = drainPromiseJobs;
        _delegate = InvokeArray;
    }

    public string Name { get; }
    public Delegate Delegate => _delegate;

    public object? Invoke(params object?[] arguments) => InvokeArray(arguments);

    private object? InvokeArray(object?[] arguments)
    {
        var result = _engine.Invoke(Name, arguments).ToObject();
        if (_drainPromiseJobs)
        {
            DrainPromiseJobs(_engine);
        }

        return result;
    }

    private static void DrainPromiseJobs(Engine engine)
    {
        var advanced = engine.GetType().GetProperty("Advanced", BindingFlags.Instance | BindingFlags.Public)?.GetValue(engine);
        TryInvokeNoArgs(advanced, "ProcessTasks");
        TryInvokeNoArgs(advanced, "RunAvailableContinuations");
        TryInvokeNoArgs(advanced, "RunJobs");
    }

    private static void TryInvokeNoArgs(object? target, string methodName)
    {
        if (target is null)
        {
            return;
        }

        try
        {
            var method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            method?.Invoke(target, null);
        }
        catch (TargetInvocationException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}

public sealed record JavaScriptDeclarationScanResult(
    IReadOnlyList<string> Functions,
    IReadOnlyList<string> Classes);

public static class JavaScriptDeclarationScanner
{
    public static JavaScriptDeclarationScanResult Scan(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var parser = new Parser(new ParserOptions { Tolerant = true });
        Program program;
        try
        {
            program = parser.ParseScript(source);
        }
        catch
        {
            try
            {
                program = parser.ParseModule(source);
            }
            catch
            {
                return new JavaScriptDeclarationScanResult(Array.Empty<string>(), Array.Empty<string>());
            }
        }

        var visitor = new DeclarationVisitor();
        visitor.Visit(program);

        return new JavaScriptDeclarationScanResult(
            visitor.Functions.ToList(),
            visitor.Classes.ToList());
    }

    private sealed class DeclarationVisitor : AstVisitor
    {
        public HashSet<string> Functions { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Classes { get; } = new(StringComparer.Ordinal);

        protected override object? VisitFunctionDeclaration(FunctionDeclaration node)
        {
            if (node.Id != null && !string.IsNullOrEmpty(node.Id.Name))
            {
                Functions.Add(node.Id.Name);
            }
            return base.VisitFunctionDeclaration(node);
        }

        protected override object? VisitClassDeclaration(ClassDeclaration node)
        {
            if (node.Id != null && !string.IsNullOrEmpty(node.Id.Name))
            {
                Classes.Add(node.Id.Name);
            }
            return base.VisitClassDeclaration(node);
        }
    }
}

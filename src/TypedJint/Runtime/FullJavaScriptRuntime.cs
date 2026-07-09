using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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

    private static readonly System.Threading.AsyncLocal<object?> _currentThis = new();
    public static object? CurrentThis
    {
        get => _currentThis.Value;
        set => _currentThis.Value = value;
    }

    private static DomDocument? _fallbackDocument;
    private static DomWindow? _fallbackWindow;

    private readonly JavaScriptRuntimeOptions _options;
    private readonly Dictionary<string, ICompiledFunction> _runtimeFunctions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _globals = new(StringComparer.Ordinal);

    public JavaScriptRuntimeEngine(JavaScriptRuntimeOptions? options = null)
    {
        _options = options ?? new JavaScriptRuntimeOptions();
        Document = _options.Document ?? new DomDocument();
        Window = new DomWindow(Document);
        SetValue("document", Document);
        SetValue("window", Window);
    }

    public DomDocument Document { get; }
    public DomWindow Window { get; }
    public IReadOnlyDictionary<string, ICompiledFunction> RuntimeFunctions => new ReadOnlyDictionary<string, ICompiledFunction>(_runtimeFunctions);

    public JavaScriptRuntimeEngine SetValue(string name, object? value)
    {
        _globals[name] = value;
        return this;
    }

    public object? GetValue(string name)
    {
        return _globals.TryGetValue(name, out var val) ? val : null;
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

            var className = "DynamicModule_" + Guid.NewGuid().ToString("N");
            // Generate C#
            var genResult = OptimizedJavaScriptCSharpGenerator.Generate(source, new OptimizedJavaScriptCSharpGenerationOptions
            {
                ClassName = className,
                EmitNativeMethods = true,
                EmitRuntimeFallback = false,
                EmitAggressiveInlining = true,
                Globals = _globals
            });

            // Compile C#
            var buildResult = GeneratedCSharpCompiler.CreateScriptInstance(genResult.Source, className);
            if (buildResult.Success && buildResult.Instance != null)
            {
                var scriptInstance = (GeneratedCSharpScriptInstance)buildResult.Instance;
                var declarations = JavaScriptDeclarationScanner.Scan(source);
                _runtimeFunctions.Clear();

                // Call init if present
                var initMethod = scriptInstance.ScriptType.GetMethod("init", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (initMethod != null)
                {
                    if (initMethod.IsStatic) initMethod.Invoke(null, null);
                    else initMethod.Invoke(scriptInstance.Instance, null);
                }

                foreach (var functionName in declarations.Functions)
                {
                    var methodInfo = scriptInstance.ScriptType.GetMethod(functionName);
                    if (methodInfo != null)
                    {
                        var del = new Func<object?[], object?>(args => scriptInstance.InvokeRuntime(functionName, args));
                        _runtimeFunctions[functionName] = new GeneratedScriptCompiledFunction(functionName, scriptInstance, methodInfo, isNative: false, del);
                    }
                }

                return new JavaScriptRuntimeResult
                {
                    RuntimeFunctions = new ReadOnlyDictionary<string, ICompiledFunction>(_runtimeFunctions),
                    ClassDeclarations = declarations.Classes,
                    Diagnostics = diagnostics
                };
            }
            else
            {
                throw new InvalidOperationException($"Roslyn compilation of dynamic script failed: {buildResult.Build?.DiagnosticsText}. Exception: {buildResult.Exception}");
            }
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

            throw new InvalidOperationException($"Function '{functionName}' not found.");
        }
        finally
        {
            CurrentEngine = oldEngine;
        }
    }

    public object? Evaluate(string source)
    {
        throw new NotSupportedException("Evaluate is not supported in pure C# backend.");
    }

    public void DrainPromiseJobs()
    {
    }

    public static dynamic? Invoke(object? callee, object?[] args)
    {
        if (callee is null) return null;

        if (callee is Func<object?[], object?> fastFn)
        {
            return fastFn(args);
        }

        if (callee is Delegate del)
        {
            var methodInfo = del.GetType().GetMethod("Invoke");
            if (methodInfo != null)
            {
                var parameters = methodInfo.GetParameters();
                var hasParams = parameters.Length > 0 && parameters[^1].ParameterType.IsArray && parameters[^1].IsDefined(typeof(ParamArrayAttribute), false);
                int normalParamCount = hasParams ? parameters.Length - 1 : parameters.Length;

                var convertedArgs = new object?[parameters.Length];
                for (int i = 0; i < normalParamCount; i++)
                {
                    if (i < args.Length)
                    {
                        var arg = args[i];
                        var pType = parameters[i].ParameterType;
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
                    else
                    {
                        convertedArgs[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
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

                return del.DynamicInvoke(convertedArgs);
            }
        }

        return null;
    }

    public static dynamic? InvokeMethod(object? target, string methodName, object?[] args)
    {
        if (target is null) return null;
        if (target is TypedJint.JsTypedArrayWrapper wrapper)
        {
            if (methodName.Equals("set", StringComparison.OrdinalIgnoreCase))
            {
                var offset = args.Length > 1 ? Convert.ToInt32(args[1]) : 0;
                wrapper.set(args.Length > 0 ? args[0] : null, offset);
                return null;
            }
        }
        var prevThis = CurrentThis;
        CurrentThis = target;
        try
        {
            if (target is string str)
            {
                switch (methodName.ToLowerInvariant())
                {
                    case "touppercase":
                        return str.ToUpperInvariant();
                    case "tolowercase":
                        return str.ToLowerInvariant();
                    case "indexof":
                        {
                            var search = args.Length > 0 ? Convert.ToString(args[0]) : "";
                            var start = args.Length > 1 ? Convert.ToInt32(args[1]) : 0;
                            return (double)str.IndexOf(search ?? "", start);
                        }
                    case "lastindexof":
                        {
                            var search = args.Length > 0 ? Convert.ToString(args[0]) : "";
                            return (double)str.LastIndexOf(search ?? "");
                        }
                    case "substring":
                    case "substr":
                        {
                            int start = args.Length > 0 ? Math.Max(0, Convert.ToInt32(args[0])) : 0;
                            int length = args.Length > 1 ? Convert.ToInt32(args[1]) : str.Length - start;
                            if (methodName.ToLowerInvariant() == "substring")
                            {
                                int end = args.Length > 1 ? Convert.ToInt32(args[1]) : str.Length;
                                length = Math.Max(0, end - start);
                            }
                            if (start >= str.Length) return "";
                            return str.Substring(start, Math.Min(length, str.Length - start));
                        }
                    case "slice":
                        {
                            int start = args.Length > 0 ? Convert.ToInt32(args[0]) : 0;
                            int end = args.Length > 1 ? Convert.ToInt32(args[1]) : str.Length;
                            if (start < 0) start = Math.Max(0, str.Length + start);
                            if (end < 0) end = Math.Max(0, str.Length + end);
                            start = Math.Min(start, str.Length);
                            end = Math.Min(end, str.Length);
                            if (start >= end) return "";
                            return str.Substring(start, end - start);
                        }
                    case "trim":
                        return str.Trim();
                    case "charat":
                        {
                            int idx = args.Length > 0 ? Convert.ToInt32(args[0]) : 0;
                            if (idx < 0 || idx >= str.Length) return "";
                            return new string(str[idx], 1);
                        }
                    case "charcodeat":
                        {
                            int idx = args.Length > 0 ? Convert.ToInt32(args[0]) : 0;
                            if (idx < 0 || idx >= str.Length) return double.NaN;
                            return (double)str[idx];
                        }
                    case "includes":
                        return str.Contains(args.Length > 0 ? Convert.ToString(args[0]) ?? "" : "");
                    case "startswith":
                        return str.StartsWith(args.Length > 0 ? Convert.ToString(args[0]) ?? "" : "");
                    case "endswith":
                        return str.EndsWith(args.Length > 0 ? Convert.ToString(args[0]) ?? "" : "");
                    case "split":
                        {
                            var separator = args.Length > 0 ? Convert.ToString(args[0]) : null;
                            var limit = args.Length > 1 ? Convert.ToInt32(args[1]) : int.MaxValue;
                            string[] parts;
                            if (string.IsNullOrEmpty(separator))
                            {
                                parts = str.Select(c => new string(c, 1)).ToArray();
                            }
                            else
                            {
                                parts = str.Split(new[] { separator }, StringSplitOptions.None);
                            }
                            var result = new TypedJint.Runtime.JsArray();
                            for (int i = 0; i < Math.Min(parts.Length, limit); i++) result.Add(parts[i]);
                            return result;
                        }
                    case "replace":
                        {
                            var search = args.Length > 0 ? Convert.ToString(args[0]) ?? "" : "";
                            var replace = args.Length > 1 ? Convert.ToString(args[1]) ?? "" : "";
                            return str.Replace(search, replace);
                        }
                }
            }

            if (target is System.Text.RegularExpressions.Regex regex)
            {
                switch (methodName.ToLowerInvariant())
                {
                    case "exec":
                        {
                            var input = args.Length > 0 ? Convert.ToString(args[0]) ?? "" : "";
                            return JavaScriptRuntimeEngine.ExecRegex(regex, input);
                        }
                    case "test":
                        {
                            var input = args.Length > 0 ? Convert.ToString(args[0]) ?? "" : "";
                            return regex.IsMatch(input);
                        }
                }
            }

            if (target is double dNum || target is int || target is float || target is long)
            {
                double num = Convert.ToDouble(target);
                switch (methodName.ToLowerInvariant())
                {
                    case "tofixed":
                        {
                            int digits = args.Length > 0 ? Convert.ToInt32(args[0]) : 0;
                            return num.ToString("F" + digits, CultureInfo.InvariantCulture);
                        }
                    case "tostring":
                        {
                            int radix = args.Length > 0 ? Convert.ToInt32(args[0]) : 10;
                            if (radix == 16) return ((long)num).ToString("x");
                            return num.ToString(CultureInfo.InvariantCulture);
                        }
                }
            }

            // Check if target is a dictionary/ExpandoObject
            if (target is IDictionary<string, object?> dict)
            {
                if (dict.TryGetValue(methodName, out var memberVal) && memberVal is not null)
                {
                    return Invoke(memberVal, args);
                }
            }

            var targetIsType = target is Type;
            var type = targetIsType ? (Type)target : target.GetType();
            var flags = targetIsType 
                ? (BindingFlags.Static | BindingFlags.Public | BindingFlags.IgnoreCase) 
                : (BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            
            MethodInfo? method = null;
            foreach (var m in type.GetMethods(flags).Where(x => string.Equals(x.Name, methodName, StringComparison.OrdinalIgnoreCase)))
            {
                var parameters = m.GetParameters();
                if (args.Length <= parameters.Length && (args.Length == 0 || !parameters[^1].ParameterType.IsArray || !parameters[^1].IsDefined(typeof(ParamArrayAttribute), false)))
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
                
                return method.Invoke(targetIsType ? null : target, convertedArgs);
            }

            var memberValProto = JavaScriptRuntime.FindInPrototypeChain(target, methodName, out var owner);
            if (memberValProto != null)
            {
                return Invoke(memberValProto, args);
            }
            
            throw new NotSupportedException($"Method '{methodName}' with {args.Length} argument(s) not found on '{type.Name}'.");
        }
        finally
        {
            CurrentThis = prevThis;
        }
    }

    public static dynamic? GetProperty(object? target, string member)
    {
        if (target is null) return null;
        if (target is TypedJint.JsTypedArrayWrapper wrapper)
        {
            if (member.Equals("length", StringComparison.OrdinalIgnoreCase)) return wrapper.Length;
            if (int.TryParse(member, out var idx)) return wrapper[idx];
        }
        if (target is Type typeVal)
        {
            var nested = typeVal.GetNestedType(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase | BindingFlags.Static);
            if (nested != null) return nested;

            var staticProp = typeVal.GetProperty(member, BindingFlags.Static | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (staticProp != null)
            {
                var val = staticProp.GetValue(null);
                if (val != null) return val;
            }
            var staticField = typeVal.GetField(member, BindingFlags.Static | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (staticField != null)
            {
                var val = staticField.GetValue(null);
                if (val != null) return val;
            }
        }
        var type = target.GetType();
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        var prop = type.GetProperty(member, flags);
        if (prop != null)
        {
            var val = prop.GetValue(target);
            if (val != null) return val;
        }
        var field = type.GetField(member, flags);
        if (field != null)
        {
            var val = field.GetValue(target);
            if (val != null) return val;
        }
        if (target is IDictionary<string, object?> dict)
        {
            if (dict.TryGetValue(member, out var val)) return val;
        }

        var extraVal = JavaScriptRuntime.GetExtraProperty(target, member, out var found);
        if (found) return extraVal;

        var memberVal = JavaScriptRuntime.FindInPrototypeChain(target, member, out var owner);
        if (memberVal != null)
        {
            return memberVal;
        }
        return null;
    }

    public static object? SetProperty(object? target, string member, object? value)
    {
        if (target is null) return null;
        if (target is TypedJint.JsTypedArrayWrapper wrapper)
        {
            if (int.TryParse(member, out var idx))
            {
                wrapper[idx] = value;
                return value;
            }
        }
        if (target is Type typeVal)
        {
            var staticProp = typeVal.GetProperty(member, BindingFlags.Static | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (staticProp != null && staticProp.CanWrite)
            {
                var converted = ChangeType(value, staticProp.PropertyType);
                staticProp.SetValue(null, converted);
                return value;
            }
            var staticField = typeVal.GetField(member, BindingFlags.Static | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (staticField != null)
            {
                var converted = ChangeType(value, staticField.FieldType);
                staticField.SetValue(null, converted);
                return value;
            }
        }
        var type = target.GetType();
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        var prop = type.GetProperty(member, flags);
        if (prop != null && prop.CanWrite)
        {
            var converted = ChangeType(value, prop.PropertyType);
            prop.SetValue(target, converted);
            return value;
        }
        var field = type.GetField(member, flags);
        if (field != null)
        {
            var converted = ChangeType(value, field.FieldType);
            field.SetValue(target, converted);
            return value;
        }
        if (target is IDictionary<string, object?> dict)
        {
            dict[member] = value;
            return value;
        }

        JavaScriptRuntime.SetExtraProperty(target, member, value);
        return value;
    }

    public static object? GetIndex(object? target, object? index)
    {
        if (target is null || index is null) return null;
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
            var convertedIndex = ChangeType(index, paramType);
            return indexer.GetValue(target, new object?[] { convertedIndex });
        }
        var propName = Convert.ToString(index, CultureInfo.InvariantCulture) ?? string.Empty;
        return GetProperty(target, propName);
    }

    public static object? SetIndex(object? target, object? index, object? value)
    {
        if (target is null || index is null) return null;
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
            var convertedIndex = ChangeType(index, paramType);
            var convertedValue = ChangeType(value, indexer.PropertyType);
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

    public static object? LogicalOr(object? left, Func<object?> rightFunc)
    {
        return ToBoolean(left) ? left : rightFunc();
    }

    public static object? LogicalAnd(object? left, Func<object?> rightFunc)
    {
        return ToBoolean(left) ? rightFunc() : left;
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

    public static object? ExecRegex(System.Text.RegularExpressions.Regex regex, string input)
    {
        if (regex == null) return null;
        var match = regex.Match(input);
        if (!match.Success) return null;

        var result = new JsRegexMatch();
        result.index = match.Index;
        result.input = input;

        for (int i = 0; i < match.Groups.Count; i++)
        {
            var g = match.Groups[i];
            result.Add(g.Success ? g.Value : null);
        }
        
        TypedJint.Runtime.RegExp._dollar_1 = match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : "";
        TypedJint.Runtime.RegExp._dollar_2 = match.Groups.Count > 2 && match.Groups[2].Success ? match.Groups[2].Value : "";

        return result;
    }

    public static object? InvokeSuperMethod(object? instance, string superClassName, string methodName, object?[] args)
    {
        var proto = TypedJint.Runtime.JavaScriptRuntime.GetPrototype(superClassName);
        if (proto == null) return null;
        var method = JavaScriptRuntimeEngine.GetProperty(proto, methodName);
        if (method == null) return null;
        
        var prevThis = JavaScriptRuntimeEngine.CurrentThis;
        JavaScriptRuntimeEngine.CurrentThis = instance;
        try
        {
            return JavaScriptRuntimeEngine.Invoke((object?)method, args);
        }
        finally
        {
            JavaScriptRuntimeEngine.CurrentThis = prevThis;
        }
    }

    public static object? ChangeType(object? value, Type targetType)
    {
        if (value == null) return null;
        var valueType = value.GetType();
        if (targetType.IsAssignableFrom(valueType)) return value;
        try
        {
            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }
        catch
        {
            return value;
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

public class JsRegexMatch : List<object?>
{
    public int index { get; set; }
    public string input { get; set; } = "";
}

public class JsTypedArrayWrapper : System.Dynamic.DynamicObject, System.Collections.IList, System.Collections.Generic.IReadOnlyList<object?>
{
    private readonly Array _array;

    public JsTypedArrayWrapper(Array array)
    {
        _array = array;
    }

    public Array InnerArray => _array;

    public int Length => _array.Length;
    public int Count => _array.Length;
    public bool IsReadOnly => false;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    protected static T[] CreateArray<T>(object? arg)
    {
        if (arg == null) return Array.Empty<T>();
        if (arg is int len) return new T[len];
        if (arg is double d) return new T[(int)d];
        if (arg is float f) return new T[(int)f];
        if (arg is long l) return new T[l];
        
        if (arg is JsTypedArrayWrapper wrapper)
        {
            var src = wrapper.InnerArray;
            var dst = new T[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                dst[i] = (T)Convert.ChangeType(src.GetValue(i), typeof(T), CultureInfo.InvariantCulture);
            }
            return dst;
        }
        
        if (arg is IList list)
        {
            var dst = new T[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                dst[i] = (T)Convert.ChangeType(list[i], typeof(T), CultureInfo.InvariantCulture);
            }
            return dst;
        }

        if (arg is Array arr)
        {
            var dst = new T[arr.Length];
            for (int i = 0; i < arr.Length; i++)
            {
                dst[i] = (T)Convert.ChangeType(arr.GetValue(i), typeof(T), CultureInfo.InvariantCulture);
            }
            return dst;
        }

        return Array.Empty<T>();
    }

    public object? this[int index]
    {
        get => _array.GetValue(index);
        set
        {
            var elementType = _array.GetType().GetElementType()!;
            _array.SetValue(Convert.ChangeType(value, elementType, CultureInfo.InvariantCulture), index);
        }
    }

    object? IList.this[int index]
    {
        get => this[index];
        set => this[index] = value;
    }

    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();

    public bool Contains(object? value)
    {
        for (int i = 0; i < _array.Length; i++)
        {
            if (Equals(this[i], value)) return true;
        }
        return false;
    }

    public int IndexOf(object? value)
    {
        for (int i = 0; i < _array.Length; i++)
        {
            if (Equals(this[i], value)) return i;
        }
        return -1;
    }

    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();

    public void CopyTo(Array array, int index)
    {
        Array.Copy(_array, 0, array, index, _array.Length);
    }

    public void set(object? arrayObj, int offset = 0)
    {
        if (arrayObj is IList list)
        {
            var elementType = _array.GetType().GetElementType()!;
            for (int i = 0; i < list.Count; i++)
            {
                if (offset + i < _array.Length)
                {
                    _array.SetValue(Convert.ChangeType(list[i], elementType, CultureInfo.InvariantCulture), offset + i);
                }
            }
        }
        else if (arrayObj is Array arr)
        {
            var elementType = _array.GetType().GetElementType()!;
            for (int i = 0; i < arr.Length; i++)
            {
                if (offset + i < _array.Length)
                {
                    _array.SetValue(Convert.ChangeType(arr.GetValue(i), elementType, CultureInfo.InvariantCulture), offset + i);
                }
            }
        }
    }

    public System.Collections.Generic.IEnumerator<object?> GetEnumerator()
    {
        for (int i = 0; i < _array.Length; i++)
        {
            yield return _array.GetValue(i);
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public override bool TryGetMember(System.Dynamic.GetMemberBinder binder, out object? result)
    {
        if (binder.Name.Equals("length", StringComparison.OrdinalIgnoreCase))
        {
            result = this.Length;
            return true;
        }
        if (binder.Name.Equals("byteLength", StringComparison.OrdinalIgnoreCase))
        {
            var elementType = _array.GetType().GetElementType()!;
            result = this.Length * System.Runtime.InteropServices.Marshal.SizeOf(elementType);
            return true;
        }
        result = null;
        return false;
    }

    public override bool TrySetMember(System.Dynamic.SetMemberBinder binder, object? value)
    {
        return false;
    }

    public override bool TryGetIndex(System.Dynamic.GetIndexBinder binder, object[] indexes, out object? result)
    {
        if (indexes.Length == 1 && int.TryParse(Convert.ToString(indexes[0]), out var idx))
        {
            result = this[idx];
            return true;
        }
        result = null;
        return false;
    }

    public override bool TrySetIndex(System.Dynamic.SetIndexBinder binder, object[] indexes, object? value)
    {
        if (indexes.Length == 1 && int.TryParse(Convert.ToString(indexes[0]), out var idx))
        {
            this[idx] = value;
            return true;
        }
        return false;
    }
}

public class Float32Array : JsTypedArrayWrapper
{
    public Float32Array(object? arg) : base(CreateArray<float>(arg)) { }
}

public class Uint16Array : JsTypedArrayWrapper
{
    public Uint16Array(object? arg) : base(CreateArray<ushort>(arg)) { }
}

public class Uint32Array : JsTypedArrayWrapper
{
    public Uint32Array(object? arg) : base(CreateArray<uint>(arg)) { }
}

public class Int32Array : JsTypedArrayWrapper
{
    public Int32Array(object? arg) : base(CreateArray<int>(arg)) { }
}

public class Float64Array : JsTypedArrayWrapper
{
    public Float64Array(object? arg) : base(CreateArray<double>(arg)) { }
}

public class Uint8Array : JsTypedArrayWrapper
{
    public Uint8Array(object? arg) : base(CreateArray<byte>(arg)) { }
}

public class Int16Array : JsTypedArrayWrapper
{
    public Int16Array(object? arg) : base(CreateArray<short>(arg)) { }
}

public class Int8Array : JsTypedArrayWrapper
{
    public Int8Array(object? arg) : base(CreateArray<sbyte>(arg)) { }
}

public class CustomEvent : DomEvent
{
    public object? detail { get; set; }

    public CustomEvent(string type, object? options = null) : base(type)
    {
        if (options is IDictionary<string, object?> dict)
        {
            if (dict.TryGetValue("detail", out var d)) detail = d;
        }
        else if (options is System.Dynamic.ExpandoObject exp)
        {
            var expDict = (IDictionary<string, object?>)exp;
            if (expDict.TryGetValue("detail", out var d)) detail = d;
        }
    }
}

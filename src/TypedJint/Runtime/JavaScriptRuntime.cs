using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

namespace TypedJint.Runtime;

public static class JavaScriptRuntime
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> ObjectPrototypes = new();
    private static readonly Dictionary<Type, object> ClassPrototypes = new();

    public static object? GetPrototype(object? obj)
    {
        if (obj is null) return null;
        if (obj is Type t)
        {
            if (!ClassPrototypes.TryGetValue(t, out var p))
            {
                p = new System.Dynamic.ExpandoObject();
                ClassPrototypes[t] = p;
            }
            return p;
        }

        if (obj is Delegate del)
        {
            var extraVal = GetExtraProperty(del, "prototype", out var found);
            if (found && extraVal != null) return extraVal;

            if (!ObjectPrototypes.TryGetValue(del, out var p))
            {
                p = new System.Dynamic.ExpandoObject();
                ObjectPrototypes.AddOrUpdate(del, p);
            }
            return p;
        }

        if (obj is IDictionary<string, object?> dict)
        {
            if (dict.TryGetValue("__proto__", out var p)) return p;
        }
        if (ObjectPrototypes.TryGetValue(obj, out var proto)) return proto;
        return GetPrototype(obj.GetType());
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<string, object?>> ExtraProperties = new();

    public static object? SetExtraProperty(object obj, string member, object? value)
    {
        var dict = ExtraProperties.GetOrCreateValue(obj);
        dict[member] = value;
        return value;
    }

    public static object? GetExtraProperty(object obj, string member, out bool found)
    {
        if (ExtraProperties.TryGetValue(obj, out var dict))
        {
            if (dict.TryGetValue(member, out var val))
            {
                found = true;
                return val;
            }
        }
        found = false;
        return null;
    }

    public static IReadOnlyDictionary<string, object?>? GetExtraProperties(object obj)
    {
        if (ExtraProperties.TryGetValue(obj, out var dict))
        {
            return dict;
        }
        return null;
    }

    public static Func<object?[], object?> CreateDelegate(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName) ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        var parameters = method.GetParameters();
        
        var argsParam = System.Linq.Expressions.Expression.Parameter(typeof(object?[]), "args");
        var argExpressions = new System.Linq.Expressions.Expression[parameters.Length];
        
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
            argExpressions[i] = conditionExpr;
        }

        var callExpr = method.IsStatic 
            ? System.Linq.Expressions.Expression.Call(null, method, argExpressions)
            : System.Linq.Expressions.Expression.Call(System.Linq.Expressions.Expression.Constant(target), method, argExpressions);
        
        System.Linq.Expressions.Expression bodyExpr = method.ReturnType == typeof(void)
            ? System.Linq.Expressions.Expression.Block(callExpr, System.Linq.Expressions.Expression.Constant(null, typeof(object)))
            : System.Linq.Expressions.Expression.Convert(callExpr, typeof(object));

        var lambda = System.Linq.Expressions.Expression.Lambda<Func<object?[], object?>>(bodyExpr, argsParam);
        return lambda.Compile();
    }

    public static bool HasPrototype(object obj)
    {
        if (obj is null) return false;
        if (obj is Type t) return ClassPrototypes.ContainsKey(t);
        if (obj is IDictionary<string, object?> dict) return dict.ContainsKey("__proto__");
        return ObjectPrototypes.TryGetValue(obj, out _);
    }

    public static void SetPrototype(object obj, object proto)
    {
        if (obj is null) return;
        if (obj is Type t)
        {
            ClassPrototypes[t] = proto;
            return;
        }
        if (obj is IDictionary<string, object?> dict)
        {
            dict["__proto__"] = proto;
            return;
        }
        ObjectPrototypes.AddOrUpdate(obj, proto);
    }

    public static object? FindInPrototypeChain(object? target, string name, out object? prototypeOwner)
    {
        prototypeOwner = null;
        if (target is null) return null;
        
        var current = GetPrototype(target);
        Console.WriteLine($"[FindInPrototypeChain Debug] target: '{target.GetType().Name}', name: '{name}', start prototype is null? {current == null}");
        while (current != null)
        {
            if (current is IDictionary<string, object?> dict)
            {
                Console.WriteLine($"  Checking prototype keys: {string.Join(", ", dict.Keys)}");
                if (dict.TryGetValue(name, out var val) && val != null)
                {
                    prototypeOwner = current;
                    Console.WriteLine($"  Found '{name}' on prototype!");
                    return val;
                }
                if (dict.TryGetValue("__proto__", out var next) && next != current)
                {
                    Console.WriteLine($"  Following __proto__ to next prototype");
                    current = next;
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }
        Console.WriteLine($"  '{name}' NOT found in prototype chain!");
        return null;
    }

    public static dynamic GetPrototype(string className)
    {
        Console.WriteLine($"[GetPrototype Debug] className: '{className}'");
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase;
        
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .OrderByDescending(asm => asm.GetName().Name?.StartsWith("TypedJint.GeneratedScript") == true);
            
        foreach (var asm in assemblies)
        {
            var asmName = asm.GetName().Name;
            if (asmName != null && (asmName.StartsWith("System.") || asmName.StartsWith("Microsoft.") || asmName == "mscorlib" || asmName == "netstandard"))
            {
                continue;
            }

            var type = asm.GetType(className) ?? asm.GetType("ThreeLibraryModule+" + className) ?? asm.GetType("RoughLibraryModule+" + className) ?? asm.GetType("PixiLibraryModule+" + className) ?? asm.GetType("D3LibraryModule+" + className) ?? asm.GetType("LightweightChartsLibraryModule+" + className);
            if (type != null)
            {
                Console.WriteLine($"  Found Type in Assembly '{asmName}': '{type.FullName}'");
                return GetPrototype(type);
            }

            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }
            catch
            {
                continue;
            }

            foreach (var t in types)
            {
                var prop = t.GetProperty(className, flags);
                if (prop != null)
                {
                    var val = prop.GetValue(null);
                    Console.WriteLine($"  Found Static Property '{className}' on Type '{t.FullName}' in Assembly '{asmName}', value is null? {val == null}");
                    if (val != null) return GetPrototype(val);
                }
                var field = t.GetField(className, flags);
                if (field != null)
                {
                    var val = field.GetValue(null);
                    Console.WriteLine($"  Found Static Field '{className}' on Type '{t.FullName}' in Assembly '{asmName}', value is null? {val == null}");
                    if (val != null) return GetPrototype(val);
                }
            }
        }

        var globalObj = GetGlobal(className);
        if (globalObj != null)
        {
            return GetPrototype(globalObj);
        }
        Console.WriteLine($"  Prototype for constructor '{className}' NOT found!");
        return null;
    }

    public static object? GetConstructor(object? obj)
    {
        if (obj == null) return null;
        if (obj is Type t) return t;
        return obj.GetType();
    }

    // --- Runtime Emulation Helpers ---
    public static void Discard(object? obj) {}

    public static double Max(params object?[] args)
    {
        if (args.Length == 0) return double.NegativeInfinity;
        return args.Select(Convert.ToDouble).Max();
    }

    public static double Min(params object?[] args)
    {
        if (args.Length == 0) return double.PositiveInfinity;
        return args.Select(Convert.ToDouble).Min();
    }

    public static double ToNumber(object? val)
    {
        if (val == null) return 0.0;
        if (val is double d) return d;
        if (val is float f) return f;
        if (val is int i) return i;
        if (val is long l) return l;
        if (val is decimal dec) return (double)dec;
        if (val is bool b) return b ? 1.0 : 0.0;
        if (val is string s)
        {
            if (double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var res)) return res;
            return double.NaN;
        }
        return double.NaN;
    }

    public static dynamic? GetGlobal(string name)
    {
        var currentEngine = JavaScriptRuntimeEngine.CurrentEngine;
        if (currentEngine != null)
        {
            var val = currentEngine.GetValue(name);
            if (val != null) return val;
        }

        var window = JavaScriptRuntimeEngine.CurrentWindow;
        if (window != null)
        {
            var winVal = JavaScriptRuntimeEngine.GetProperty(window, name);
            if (winVal != null) return winVal;
        }

        return null;
    }

    public static object? SetGlobal(string name, object? value)
    {
        var currentEngine = JavaScriptRuntimeEngine.CurrentEngine;
        if (currentEngine != null)
        {
            currentEngine.SetValue(name, value);
        }
        var window = JavaScriptRuntimeEngine.CurrentWindow;
        if (window != null)
        {
            JavaScriptRuntimeEngine.SetProperty(window, name, value);
        }
        return value;
    }

    public static double Abs(object? val) => Math.Abs(ToNumber(val));
    public static double Acos(object? val) => Math.Acos(ToNumber(val));
    public static double Asin(object? val) => Math.Asin(ToNumber(val));
    public static double Atan(object? val) => Math.Atan(ToNumber(val));
    public static double Cos(object? val) => Math.Cos(ToNumber(val));
    public static double Sin(object? val) => Math.Sin(ToNumber(val));
    public static double Tan(object? val) => Math.Tan(ToNumber(val));
    public static double Exp(object? val) => Math.Exp(ToNumber(val));
    public static double Log(object? val) => Math.Log(ToNumber(val));
    public static double Sqrt(object? val) => Math.Sqrt(ToNumber(val));
    public static double Floor(object? val) => Math.Floor(ToNumber(val));
    public static double Ceiling(object? val) => Math.Ceiling(ToNumber(val));
    public static double Round(object? val) => Math.Round(ToNumber(val));
    public static double Atan2(object? y, object? x) => Math.Atan2(ToNumber(y), ToNumber(x));
    public static double Pow(object? x, object? y) => Math.Pow(ToNumber(x), ToNumber(y));

    public static dynamic? PostIncrementProperty(object? obj, object? prop)
    {
        var propStr = Convert.ToString(prop) ?? string.Empty;
        var val = JavaScriptRuntimeEngine.GetProperty(obj, propStr);
        var num = val == null ? 0 : Convert.ToDouble(val);
        JavaScriptRuntimeEngine.SetProperty(obj, propStr, num + 1);
        return num;
    }

    public static dynamic? PostDecrementProperty(object? obj, object? prop)
    {
        var propStr = Convert.ToString(prop) ?? string.Empty;
        var val = JavaScriptRuntimeEngine.GetProperty(obj, propStr);
        var num = val == null ? 0 : Convert.ToDouble(val);
        JavaScriptRuntimeEngine.SetProperty(obj, propStr, num - 1);
        return num;
    }

    public static dynamic? PreIncrementProperty(object? obj, object? prop)
    {
        var propStr = Convert.ToString(prop) ?? string.Empty;
        var val = JavaScriptRuntimeEngine.GetProperty(obj, propStr);
        var num = (val == null ? 0 : Convert.ToDouble(val)) + 1;
        JavaScriptRuntimeEngine.SetProperty(obj, propStr, num);
        return num;
    }

    public static dynamic? PreDecrementProperty(object? obj, object? prop)
    {
        var propStr = Convert.ToString(prop) ?? string.Empty;
        var val = JavaScriptRuntimeEngine.GetProperty(obj, propStr);
        var num = (val == null ? 0 : Convert.ToDouble(val)) - 1;
        JavaScriptRuntimeEngine.SetProperty(obj, propStr, num);
        return num;
    }

    public static dynamic? PostIncrementGlobal(string name)
    {
        var val = GetGlobal(name);
        var num = val == null ? 0 : Convert.ToDouble(val);
        SetGlobal(name, num + 1);
        return num;
    }

    public static dynamic? PostDecrementGlobal(string name)
    {
        var val = GetGlobal(name);
        var num = val == null ? 0 : Convert.ToDouble(val);
        SetGlobal(name, num - 1);
        return num;
    }

    public static dynamic? PreIncrementGlobal(string name)
    {
        var val = GetGlobal(name);
        var num = (val == null ? 0 : Convert.ToDouble(val)) + 1;
        SetGlobal(name, num);
        return num;
    }

    public static dynamic? PreDecrementGlobal(string name)
    {
        var val = GetGlobal(name);
        var num = (val == null ? 0 : Convert.ToDouble(val)) - 1;
        SetGlobal(name, num);
        return num;
    }

    public static dynamic? apply(dynamic? obj, dynamic? thisArg, dynamic? args)
    {
        if (obj is Delegate del)
        {
            var parameters = del.Method.GetParameters();
            var invokeArgs = new object?[parameters.Length];
            var list = args as System.Collections.IList;
            for (int i = 0; i < invokeArgs.Length; i++)
            {
                if (list != null && i < list.Count)
                {
                    invokeArgs[i] = list[i];
                }
                else
                {
                    invokeArgs[i] = null;
                }
            }
            return del.DynamicInvoke(invokeArgs);
        }
        return null;
    }

    public static dynamic? call(dynamic? obj, dynamic? thisArg, params object?[] args)
    {
        if (obj is Delegate del)
        {
            var parameters = del.Method.GetParameters();
            var invokeArgs = new object?[parameters.Length];
            for (int i = 0; i < invokeArgs.Length; i++)
            {
                if (i < args.Length)
                {
                    invokeArgs[i] = args[i];
                }
                else
                {
                    invokeArgs[i] = null;
                }
            }
            return del.DynamicInvoke(invokeArgs);
        }
        return null;
    }

    public static bool DeleteProperty(dynamic? obj, object? prop)
    {
        if (obj == null || prop == null) return false;
        string key = Convert.ToString(prop);
        if (obj is System.Dynamic.ExpandoObject expando)
        {
            return ((IDictionary<string, object?>)expando).Remove(key);
        }
        if (obj is System.Collections.IDictionary dict)
        {
            if (dict.Contains(prop))
            {
                dict.Remove(prop);
                return true;
            }
            return false;
        }
        return false;
    }

    public static bool In(dynamic? prop, dynamic? obj)
    {
        if (obj == null) return false;
        if (prop == null) return false;
        string key = Convert.ToString(prop);
        if (obj is System.Dynamic.ExpandoObject expando)
        {
            return ((IDictionary<string, object?>)expando).ContainsKey(key);
        }
        if (obj is System.Collections.IDictionary dict)
        {
            return dict.Contains(prop);
        }
        var type = (Type)obj.GetType();
        if (type.GetProperty(key) != null) return true;
        if (type.GetField(key) != null) return true;
        return false;
    }

    public static bool InstanceOf(dynamic? obj, dynamic? constructor)
    {
        if (obj == null || constructor == null) return false;
        if (constructor is ArrayClass || constructor == typeof(System.Array)) return obj is System.Collections.IList;
        if (constructor is Type t)
        {
            if (t == typeof(string)) return obj is string;
            if (t == typeof(double)) return obj is double || obj is int || obj is float || obj is decimal || obj is long || obj is short || obj is byte;
            if (t == typeof(bool)) return obj is bool;
            return t.IsInstanceOfType(obj);
        }
        return false;
    }

    public static dynamic CreateNewInstance(dynamic? constructor, params object?[] args)
    {
        args = args ?? System.Array.Empty<object?>();
        Console.WriteLine($"[CreateNewInstance Debug] constructor type: {constructor?.GetType().FullName}, value: {constructor}");
        if (constructor == null)
        {
            Console.WriteLine($"[CreateNewInstance Debug] constructor is null! args count: {args.Length}");
            Console.WriteLine(Environment.StackTrace);
            throw new ArgumentException("constructor is null");
        }
        
        if (constructor != null && constructor.GetType().FullName.Contains("DelegateWrapper"))
        {
            var field = constructor.GetType().GetField("_d", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field != null)
            {
                var delVal = field.GetValue(constructor);
                if (delVal != null)
                {
                    constructor = delVal;
                }
            }
        }

        if (constructor is Type t)
        {
            var constructors = t.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            System.Reflection.ConstructorInfo? bestCtor = null;
            object?[]? paddedArgs = null;
            
            foreach (var ctor in constructors)
            {
                var parameters = ctor.GetParameters();
                if (args.Length <= parameters.Length)
                {
                    var tempArgs = new object?[parameters.Length];
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        if (i < args.Length)
                        {
                            tempArgs[i] = args[i];
                        }
                        else
                        {
                            if (parameters[i].HasDefaultValue)
                            {
                                tempArgs[i] = parameters[i].DefaultValue;
                            }
                            else
                            {
                                tempArgs[i] = null;
                            }
                        }
                    }
                    bestCtor = ctor;
                    paddedArgs = tempArgs;
                    break;
                }
            }
            if (bestCtor != null)
            {
                return bestCtor.Invoke(paddedArgs);
            }
            return Activator.CreateInstance(t, args);
        }
        if (constructor is Delegate del)
        {
            var newObj = new System.Dynamic.ExpandoObject();
            var proto = GetPrototype(del);
            if (proto != null)
            {
                SetPrototype(newObj, proto);
            }
            var prevThis = JavaScriptRuntimeEngine.CurrentThis;
            JavaScriptRuntimeEngine.CurrentThis = newObj;
            try
            {
                var parameters = del.Method.GetParameters();
                var invokeArgs = new object?[parameters.Length];
                for (int i = 0; i < invokeArgs.Length; i++)
                {
                    if (i < args.Length) invokeArgs[i] = args[i];
                    else invokeArgs[i] = null;
                }
                var result = del.DynamicInvoke(invokeArgs);
                if (result != null && result.GetType().IsClass && result is not string)
                {
                    return result;
                }
                return result ?? newObj;
            }
            finally
            {
                JavaScriptRuntimeEngine.CurrentThis = prevThis;
            }
        }
        return new System.Dynamic.ExpandoObject();
    }

    public static dynamic CreateObject(params (string Key, object? Value)[] properties)
    {
        var expando = new System.Dynamic.ExpandoObject();
        var dict = (IDictionary<string, object?>)expando;
        foreach (var prop in properties)
        {
            dict[prop.Key] = prop.Value;
        }
        return expando;
    }

    public static dynamic? GetComputedProperty(dynamic? obj, dynamic? prop)
    {
        if (obj == null) return null;
        if (obj is System.Dynamic.ExpandoObject expando)
        {
            var dict = (IDictionary<string, object?>)expando;
            var key = prop == null ? "undefined" : Convert.ToString(prop) ?? "undefined";
            object? val;
            return dict.TryGetValue(key, out val) ? val : null;
        }
        if (obj is System.Collections.IList list && (prop is double || prop is int))
        {
            int idx = (int)Convert.ChangeType(prop, typeof(int));
            return idx >= 0 && idx < list.Count ? list[idx] : null;
        }
        try { return obj[prop]; } catch { return null; }
    }

    public static dynamic? SetComputedProperty(dynamic? obj, dynamic? prop, dynamic? value)
    {
        if (obj == null) return value;
        if (obj is System.Dynamic.ExpandoObject expando)
        {
            var dict = (IDictionary<string, object?>)expando;
            var key = prop == null ? "undefined" : Convert.ToString(prop) ?? "undefined";
            dict[key] = value;
            return value;
        }
        if (obj is System.Collections.IList list && (prop is double || prop is int))
        {
            int idx = (int)Convert.ChangeType(prop, typeof(int));
            while (list.Count <= idx) list.Add(null);
            list[idx] = value;
            return value;
        }
        try { obj[prop] = value; } catch { }
        return value;
    }

    public static dynamic? InvokeSequence(Func<dynamic?> func) => func();

    public static bool ToBool(dynamic? val)
    {
        if (val == null) return false;
        if (val is bool b) return b;
        if (val is double d) return d != 0.0 && !double.IsNaN(d);
        if (val is string s) return s.Length > 0;
        return true;
    }

    public static string GetTypeString(dynamic? val)
    {
        if (val == null) return "undefined";
        if (val is string) return "string";
        if (val is bool) return "boolean";
        if (val is double || val is float || val is int || val is long) return "number";
        if (val is Delegate || val is MulticastDelegate) return "function";
        return "object";
    }

    public static dynamic SliceList(dynamic? obj, int startIndex)
    {
        if (obj is System.Collections.IList list)
        {
            var newList = new List<dynamic?>();
            for (int i = startIndex; i < list.Count; i++)
            {
                newList.Add(list[i]);
            }
            return newList;
        }
        return new List<dynamic?>();
    }

    public static double GetListLength(dynamic? obj)
    {
        if (obj == null) return 0;
        if (obj is System.Collections.ICollection col) return col.Count;
        if (obj is string s) return s.Length;
        try { return obj.length; } catch { return 0; }
    }

    public static dynamic SetListLength(dynamic? obj, object? newLength)
    {
        if (obj is System.Collections.IList list)
        {
            int len = newLength == null ? 0 : (int)Convert.ChangeType(newLength, typeof(int));
            if (len < 0) len = 0;
            while (list.Count > len) list.RemoveAt(list.Count - 1);
            while (list.Count < len) list.Add(null);
            return len;
        }
        return 0;
    }

    public static double parseFloat(object? val) => double.TryParse(Convert.ToString(val), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0.0;

    public static bool isNaN(object? val)
    {
        if (val == null) return true;
        if (val is double d) return double.IsNaN(d);
        if (val is float f) return float.IsNaN(f);
        return !double.TryParse(Convert.ToString(val), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    public static bool isFinite(object? val) => JsNumber.isFinite(val);

    public static double parseInt(object? val, object? radix = null)
    {
        string s = Convert.ToString(val) ?? "";
        int r = radix == null ? 10 : (int)Convert.ChangeType(radix, typeof(int));
        if (r == 16)
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var hexVal) ? hexVal : 0.0;
        }
        if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)) return (int)d;
        return 0.0;
    }

    public static dynamic _Array { get; } = new ArrayClass();
    public static dynamic _Boolean { get; } = typeof(bool);
    public static dynamic _String { get; } = typeof(string);
    public static dynamic _Object { get; } = CreateObject(
        ("assign", (AssignDelegate)JsObject.assign),
        ("freeze", (Func<dynamic, dynamic>)JsObject.freeze),
        ("defineProperty", (Func<dynamic, object?, dynamic, dynamic>)JsObject.defineProperty),
        ("defineProperties", (Func<dynamic, dynamic, dynamic>)JsObject.defineProperties),
        ("getOwnPropertySymbols", (Func<dynamic, dynamic>)JsObject.getOwnPropertySymbols),
        ("setPrototypeOf", (Func<dynamic, dynamic, dynamic>)JsObject.setPrototypeOf),
        ("create", (Func<dynamic, dynamic>)JsObject.create),
        ("keys", (Func<dynamic?, dynamic>)JsObject.keys)
    );
    public static dynamic _Number { get; } = new NumberClass();
    public static dynamic _Function { get; } = typeof(Delegate);
    public static dynamic _math { get; } = new JsMath();
    public static dynamic UnsignedRightShift(object? left, object? right)
    {
        uint l = left == null ? 0 : (uint)Convert.ChangeType(left, typeof(uint));
        int r = right == null ? 0 : (int)Convert.ChangeType(right, typeof(int));
        return (dynamic)(l >> r);
    }

    public static bool AreStrictEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        
        if (IsNumeric(a) && IsNumeric(b))
        {
            return Convert.ToDouble(a) == Convert.ToDouble(b);
        }
        
        if (a.GetType() != b.GetType()) return false;
        
        if (a is string strA && b is string strB)
        {
            return strA == strB;
        }
        
        if (a is bool boolA && b is bool boolB)
        {
            return boolA == boolB;
        }
        
        return false;
    }

    public static bool AreEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null)
        {
            return a == null && b == null;
        }
        
        if (IsNumeric(a) && IsNumeric(b))
        {
            return Convert.ToDouble(a) == Convert.ToDouble(b);
        }
        
        if (a is string || IsNumeric(a) || a is bool)
        {
            if (b is string || IsNumeric(b) || b is bool)
            {
                if (a is bool boolA) return AreEqual(boolA ? 1.0 : 0.0, b);
                if (b is bool boolB) return AreEqual(a, boolB ? 1.0 : 0.0);
                if (a is string strA && b is string strB) return strA == strB;
                try
                {
                    return Convert.ToDouble(a) == Convert.ToDouble(b);
                }
                catch
                {
                    return Convert.ToString(a) == Convert.ToString(b);
                }
            }
        }
        
        return a.Equals(b);
    }
    
    private static bool IsNumeric(object? value)
    {
        return value is sbyte || value is byte || value is short || value is ushort ||
               value is int || value is uint || value is long || value is ulong ||
               value is float || value is double || value is decimal;
    }

    public static System.Collections.IEnumerable GetEnumerable(object? obj)
    {
        if (obj == null) yield break;
        if (obj is System.Collections.IEnumerable enumerable && !(obj is string))
        {
            foreach (var item in enumerable)
            {
                yield return item;
            }
        }
        else if (obj is string str)
        {
            foreach (var c in str)
            {
                yield return c.ToString();
            }
        }
    }

    public static System.Collections.IEnumerable GetKeys(object? obj)
    {
        if (obj == null) yield break;
        if (obj is System.Dynamic.ExpandoObject expando)
        {
            foreach (var key in ((System.Collections.Generic.IDictionary<string, object?>)expando).Keys)
            {
                yield return key;
            }
        }
        else if (obj is System.Collections.Generic.IDictionary<string, object?> dict)
        {
            foreach (var key in dict.Keys)
            {
                yield return key;
            }
        }
        else
        {
            var properties = obj.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            foreach (var prop in properties)
            {
                yield return prop.Name;
            }
        }
    }
}

public class Error
{
    public string message { get; }
    public Error(string message = "") { this.message = message; }
    public override string ToString() => message;
}

public class RangeError : Error
{
    public RangeError(string message = "") : base(message) {}
}

public class TypeError : Error
{
    public TypeError(string message = "") : base(message) {}
}

public class ReferenceError : Error
{
    public ReferenceError(string message = "") : base(message) {}
}

public delegate dynamic AssignDelegate(dynamic target, params dynamic[] sources);

public static class JsObject
{
    public static dynamic? prototype { get; set; } = new System.Dynamic.ExpandoObject();
    public static dynamic freeze { get; } = new Func<dynamic, dynamic>((obj) => obj);
    public static dynamic defineProperty { get; } = new Func<dynamic, object?, dynamic, dynamic>((obj, prop, desc) => obj);
    public static dynamic defineProperties { get; } = new Func<dynamic, dynamic, dynamic>((obj, props) => obj);
    public static dynamic getOwnPropertySymbols { get; } = new Func<dynamic, dynamic>((obj) => new List<dynamic?>());
    public static dynamic setPrototypeOf { get; } = new Func<dynamic, dynamic, dynamic>((obj, proto) => {
        JavaScriptRuntime.SetPrototype(obj, proto);
        return obj;
    });
    public static dynamic create { get; } = new Func<dynamic, dynamic>((proto) => {
        var obj = new System.Dynamic.ExpandoObject();
        if (proto != null)
        {
            JavaScriptRuntime.SetPrototype(obj, proto);
        }
        return obj;
    });
    public static AssignDelegate assign { get; } = (target, sources) =>
    {
        var targetDict = (IDictionary<string, object?>)target;
        foreach (var source in sources)
        {
            if (source is System.Dynamic.ExpandoObject expando)
            {
                var sourceDict = (IDictionary<string, object?>)expando;
                foreach (var kvp in sourceDict)
                {
                    targetDict[kvp.Key] = kvp.Value;
                }
            }
        }
        return target;
    };
    public static dynamic keys { get; } = new Func<dynamic?, dynamic>((obj) => {
        if (obj == null) return new JsArray();
        var list = new JsArray();
        if (obj is IDictionary<string, object?> dict)
        {
            foreach (var key in dict.Keys)
            {
                if (key != "__proto__")
                {
                    list.Add(key);
                }
            }
            return list;
        }
        
        var type = obj.GetType();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;
        foreach (var prop in type.GetProperties(flags))
        {
            list.Add(prop.Name);
        }
        foreach (var field in type.GetFields(flags))
        {
            list.Add(field.Name);
        }
        return list;
    });
}

public class ArrayClass
{
    public dynamic from(dynamic? obj) => JsArray.from(obj);
    public bool isArray(dynamic? obj)
    {
        if (obj is null) return false;
        if (obj is TypedJint.JsTypedArrayWrapper)
        {
            return false;
        }
        var typeName = obj.GetType().FullName ?? "";
        if (typeName.Contains("TypedArray"))
        {
            return false;
        }
        return obj is System.Collections.IList;
    }
}

public class NumberClass
{
    public bool isNaN(dynamic? obj) => JavaScriptRuntime.isNaN(obj);
    public bool isFinite(dynamic? obj) => JavaScriptRuntime.isFinite(obj);
    public double MIN_VALUE => double.MinValue;
    public double MAX_VALUE => double.MaxValue;
}

public sealed class JsMath
{
    public double E => System.Math.E;
    public double LN10 => System.Math.Log(10);
    public double LN2 => System.Math.Log(2);
    public double LOG10E => System.Math.Log10(System.Math.E);
    public double LOG2E => System.Math.Log2(System.Math.E);
    public double PI => System.Math.PI;
    public double SQRT1_2 => System.Math.Sqrt(0.5);
    public double SQRT2 => System.Math.Sqrt(2);

    public double abs(double x) => System.Math.Abs(x);
    public double acos(double x) => System.Math.Acos(x);
    public double acosh(double x) => System.Math.Acosh(x);
    public double asin(double x) => System.Math.Asin(x);
    public double asinh(double x) => System.Math.Asinh(x);
    public double atan(double x) => System.Math.Atan(x);
    public double atan2(double y, double x) => System.Math.Atan2(y, x);
    public double atanh(double x) => System.Math.Atanh(x);
    public double cbrt(double x) => System.Math.Cbrt(x);
    public double ceil(double x) => System.Math.Ceiling(x);
    public double clz32(double x) => 0;
    public double cos(double x) => System.Math.Cos(x);
    public double cosh(double x) => System.Math.Cosh(x);
    public double exp(double x) => System.Math.Exp(x);
    public double expm1(double x) => System.Math.Exp(x) - 1.0;
    public double floor(double x) => System.Math.Floor(x);
    public double fround(double x) => (float)x;
    public double hypot(params double[] args) => System.Math.Sqrt(args.Sum(v => v * v));
    public double imul(double x, double y) => (int)x * (int)y;
    public double log(double x) => System.Math.Log(x);
    public double log10(double x) => System.Math.Log10(x);
    public double log1p(double x) => System.Math.Log(1.0 + x);
    public double log2(double x) => System.Math.Log2(x);
    public double max(params double[] args) => args.Length == 0 ? double.NegativeInfinity : args.Max();
    public double min(params double[] args) => args.Length == 0 ? double.PositiveInfinity : args.Min();
    public double pow(double x, double y) => System.Math.Pow(x, y);
    public double random() => Random.Shared.NextDouble();
    public double round(double x) => System.Math.Round(x);
    public double sign(double x) => System.Math.Sign(x);
    public double sin(double x) => System.Math.Sin(x);
    public double sinh(double x) => System.Math.Sinh(x);
    public double sqrt(double x) => System.Math.Sqrt(x);
    public double tan(double x) => System.Math.Tan(x);
    public double tanh(double x) => System.Math.Tanh(x);
    public double trunc(double x) => System.Math.Truncate(x);
}

public class JsDate
{
    private readonly DateTime _dt;
    public JsDate() { _dt = DateTime.UtcNow; }
    public JsDate(object? val)
    {
        if (val == null) _dt = DateTime.UtcNow;
        else if (val is double d) _dt = DateTime.UnixEpoch.AddMilliseconds(d);
        else if (val is int i) _dt = DateTime.UnixEpoch.AddMilliseconds(i);
        else if (DateTime.TryParse(Convert.ToString(val), out var parsed)) _dt = parsed;
        else _dt = DateTime.UtcNow;
    }
    public double getTime() => (_dt.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;
    public int getUTCDate() => _dt.Day;
    public int getUTCMonth() => _dt.Month - 1;
    public int getUTCFullYear() => _dt.Year;
}

public class JsMap
{
    private readonly Dictionary<object, object?> _dict = new();
    public int size => _dict.Count;
    public dynamic? get(object? key) => (key != null && _dict.TryGetValue(key, out var v)) ? v : null;
    public void set(object? key, object? value) { if (key != null) _dict[key] = value; }
    public bool has(object? key) => key != null && _dict.ContainsKey(key);
    public bool delete(object? key) => key != null && _dict.Remove(key);
    public void clear() => _dict.Clear();
}

public class JsSet
{
    private readonly HashSet<object> _set = new();
    public int size => _set.Count;
    public void add(object? value) { if (value != null) _set.Add(value); }
    public bool has(object? value) => value != null && _set.Contains(value);
    public bool delete(object? value) => value != null && _set.Remove(value);
    public void clear() => _set.Clear();
}

public class JsNavigator
{
    public string userAgent => "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
}

public class JsPerformance
{
    private static readonly DateTime StartTime = DateTime.UtcNow;
    public double now() => (DateTime.UtcNow - StartTime).TotalMilliseconds;
}

public class JsJson
{
    public static string stringify(dynamic obj) => System.Text.Json.JsonSerializer.Serialize<object>(obj);
    public static dynamic? parse(string json) => System.Text.Json.JsonSerializer.Deserialize<System.Dynamic.ExpandoObject>(json);
}

public class JsNumber
{
    public const double MAX_SAFE_INTEGER = 9007199254740991.0;
    public const double MIN_SAFE_INTEGER = -9007199254740991.0;
    public const double NaN = double.NaN;
    public const double POSITIVE_INFINITY = double.PositiveInfinity;
    public const double NEGATIVE_INFINITY = double.NegativeInfinity;
    public const double EPSILON = 2.220446049250313e-16;
    public static bool isNaN(object? val) => JavaScriptRuntime.isNaN(val);
    public static bool isFinite(object? val)
    {
        if (val == null) return false;
        if (double.TryParse(Convert.ToString(val), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            return !double.IsNaN(d) && !double.IsInfinity(d);
        }
        return false;
    }
}

public class JsArray : List<dynamic?>
{
    public JsArray() {}
    public JsArray(int capacity) : base(capacity) {}
    public JsArray(System.Collections.IEnumerable collection)
    {
        if (collection != null)
        {
            foreach (var item in collection) this.Add(item);
        }
    }
    public dynamic Length
    {
        get => this.Count;
        set
        {
            int newLen = (int)Convert.ChangeType(value, typeof(int));
            if (newLen < this.Count)
            {
                this.RemoveRange(newLen, this.Count - newLen);
            }
            else if (newLen > this.Count)
            {
                while (this.Count < newLen) this.Add(null);
            }
        }
    }
    public static dynamic from(dynamic? obj)
    {
        if (obj == null) return new JsArray();
        if (obj is System.Collections.IEnumerable enumerable)
        {
            return new JsArray(enumerable);
        }
        return new JsArray { obj };
    }
}

public static class RegExp
{
    public static string _dollar_1 { get; set; } = "";
    public static string _dollar_2 { get; set; } = "";
}

public static class JsRegExpExtensions
{
    public static bool test(this System.Text.RegularExpressions.Regex regex, object? val)
    {
        if (regex == null) return false;
        return regex.IsMatch(Convert.ToString(val) ?? "");
    }
}

public static class JsArrayExtensions
{
    public static double push(this System.Collections.IList list, params object?[] items)
    {
        foreach (var item in items) list.Add(item);
        return list.Count;
    }
    public static double push(this System.Collections.IList list, object? item)
    {
        list.Add(item);
        return list.Count;
    }
    public static dynamic concat(this object? list, params object?[] args)
    {
        if (list == null) return new List<dynamic?>();
        if (list is string str)
        {
            return str + string.Concat(args);
        }
        if (list is System.Collections.IList ilist)
        {
            var newList = new List<dynamic?>();
            foreach (var item in ilist) newList.Add(item);
            foreach (var arg in args)
            {
                if (arg is System.Collections.IList subList)
                {
                    foreach (var item in subList) newList.Add(item);
                }
                else
                {
                    newList.Add(arg);
                }
            }
            return newList;
        }
        return new List<dynamic?>();
    }
    public static dynamic concat(this object? list, object? arg)
    {
        if (list == null) return new List<dynamic?>();
        if (list is string str)
        {
            return str + Convert.ToString(arg);
        }
        if (list is System.Collections.IList ilist)
        {
            var newList = new List<dynamic?>();
            foreach (var item in ilist) newList.Add(item);
            if (arg is System.Collections.IList subList)
            {
                foreach (var item in subList) newList.Add(item);
            }
            else
            {
                newList.Add(arg);
            }
            return newList;
        }
        return new List<dynamic?>();
    }
    public static string join(this object? list, object? separator = null, params object?[] extra)
    {
        var sepStr = separator == null ? "," : Convert.ToString(separator);
        if (list == null) return "";
        if (list is System.Collections.IList ilist)
        {
            var parts = new List<string>();
            foreach (var item in ilist) parts.Add(Convert.ToString(item) ?? "");
            return string.Join(sepStr, parts);
        }
        return Convert.ToString(list) ?? "";
    }
    public static double indexOf(this object? list, object? searchElement, int fromIndex = 0)
    {
        if (list == null) return -1;
        if (list is string s)
        {
            return s.IndexOf(Convert.ToString(searchElement) ?? "", fromIndex);
        }
        if (list is System.Collections.IList ilist)
        {
            for (int i = fromIndex; i < ilist.Count; i++)
            {
                if (Equals(ilist[i], searchElement)) return i;
            }
        }
        return -1;
    }
    private static bool ToBool(object? obj)
    {
        if (obj == null) return false;
        if (obj is bool b) return b;
        if (obj is int i) return i != 0;
        if (obj is double d) return d != 0 && !double.IsNaN(d);
        if (obj is string s) return s.Length > 0;
        return true;
    }
    public static object? forEach(this object? list, object? callback, object? thisArg = null)
    {
        if (list == null || callback == null) return null;
        if (list is System.Collections.IList ilist)
        {
            if (callback is Func<dynamic?, dynamic?> f1)
            {
                for (int i = 0; i < ilist.Count; i++) f1(ilist[i]);
            }
            else if (callback is Func<dynamic?, dynamic?, dynamic?> f2)
            {
                for (int i = 0; i < ilist.Count; i++) f2(ilist[i], i);
            }
            else if (callback is Func<dynamic?, dynamic?, dynamic?, dynamic?> f3)
            {
                for (int i = 0; i < ilist.Count; i++) f3(ilist[i], i, ilist);
            }
            else if (callback is Action<dynamic?> a1)
            {
                for (int i = 0; i < ilist.Count; i++) a1(ilist[i]);
            }
            else if (callback is Action<dynamic?, double> a2)
            {
                for (int i = 0; i < ilist.Count; i++) a2(ilist[i], i);
            }
            else if (callback is Action<dynamic?, double, dynamic> a3)
            {
                for (int i = 0; i < ilist.Count; i++) a3(ilist[i], i, ilist);
            }
            else if (callback is Delegate del)
            {
                for (int i = 0; i < ilist.Count; i++)
                {
                    var parameters = del.Method.GetParameters();
                    var args = new object?[parameters.Length];
                    if (parameters.Length > 0) args[0] = ilist[i];
                    if (parameters.Length > 1) args[1] = (double)i;
                    if (parameters.Length > 2) args[2] = ilist;
                    del.DynamicInvoke(args);
                }
            }
        }
        return null;
    }
    public static dynamic map(this object? list, object? callback, object? thisArg = null)
    {
        var newList = new List<dynamic?>();
        if (list == null || callback == null) return newList;
        if (list is System.Collections.IList ilist)
        {
            if (callback is Func<dynamic?, dynamic?> f1)
            {
                for (int i = 0; i < ilist.Count; i++) newList.Add(f1(ilist[i]));
            }
            else if (callback is Func<dynamic?, dynamic?, dynamic?> f2)
            {
                for (int i = 0; i < ilist.Count; i++) newList.Add(f2(ilist[i], i));
            }
            else if (callback is Func<dynamic?, dynamic?, dynamic?, dynamic?> f3)
            {
                for (int i = 0; i < ilist.Count; i++) newList.Add(f3(ilist[i], i, ilist));
            }
            else if (callback is Delegate del)
            {
                for (int i = 0; i < ilist.Count; i++)
                {
                    var parameters = del.Method.GetParameters();
                    var args = new object?[parameters.Length];
                    if (parameters.Length > 0) args[0] = ilist[i];
                    if (parameters.Length > 1) args[1] = (double)i;
                    if (parameters.Length > 2) args[2] = ilist;
                    newList.Add(del.DynamicInvoke(args));
                }
            }
        }
        return newList;
    }
    public static dynamic filter(this object? list, object? callback, object? thisArg = null)
    {
        var newList = new List<dynamic?>();
        if (list == null || callback == null) return newList;
        if (list is System.Collections.IList ilist)
        {
            if (callback is Func<dynamic?, dynamic?> f1)
            {
                for (int i = 0; i < ilist.Count; i++)
                {
                    if (ToBool(f1(ilist[i]))) newList.Add(ilist[i]);
                }
            }
            else if (callback is Func<dynamic?, dynamic?, dynamic?> f2)
            {
                for (int i = 0; i < ilist.Count; i++)
                {
                    if (ToBool(f2(ilist[i], i))) newList.Add(ilist[i]);
                }
            }
            else if (callback is Func<dynamic?, dynamic?, dynamic?, dynamic?> f3)
            {
                for (int i = 0; i < ilist.Count; i++)
                {
                    if (ToBool(f3(ilist[i], i, ilist))) newList.Add(ilist[i]);
                }
            }
            else if (callback is Delegate del)
            {
                for (int i = 0; i < ilist.Count; i++)
                {
                    var parameters = del.Method.GetParameters();
                    var args = new object?[parameters.Length];
                    if (parameters.Length > 0) args[0] = ilist[i];
                    if (parameters.Length > 1) args[1] = (double)i;
                    if (parameters.Length > 2) args[2] = ilist;
                    if (ToBool(del.DynamicInvoke(args))) newList.Add(ilist[i]);
                }
            }
        }
        return newList;
    }
    public static dynamic slice(this object? list, object? start = null, object? end = null)
    {
        if (list == null) return new List<dynamic?>();
        if (list is string str)
        {
            int s = start == null ? 0 : (int)Convert.ChangeType(start, typeof(int));
            int e = end == null ? str.Length : (int)Convert.ChangeType(end, typeof(int));
            if (s < 0) s = str.Length + s;
            if (e < 0) e = str.Length + e;
            s = Math.Max(0, Math.Min(str.Length, s));
            e = Math.Max(0, Math.Min(str.Length, e));
            if (e <= s) return "";
            return str.Substring(s, e - s);
        }
        if (list is System.Collections.IList ilist)
        {
            int s = start == null ? 0 : (int)Convert.ChangeType(start, typeof(int));
            int e = end == null ? ilist.Count : (int)Convert.ChangeType(end, typeof(int));
            if (s < 0) s = ilist.Count + s;
            if (e < 0) e = ilist.Count + e;
            s = Math.Max(0, Math.Min(ilist.Count, s));
            e = Math.Max(0, Math.Min(ilist.Count, e));
            var newList = new List<dynamic?>();
            for (int i = s; i < e; i++)
            {
                newList.Add(ilist[i]);
            }
            return newList;
        }
        return new List<dynamic?>();
    }
    public static dynamic splice(this System.Collections.IList list, object? start)
    {
        int s = start == null ? 0 : (int)Convert.ChangeType(start, typeof(int));
        if (s < 0) s = list.Count + s;
        s = Math.Max(0, Math.Min(list.Count, s));
        int dc = list.Count - s;
        var removed = new List<dynamic?>();
        for (int i = 0; i < dc; i++)
        {
            removed.Add(list[s]);
            list.RemoveAt(s);
        }
        return removed;
    }
    public static dynamic splice(this System.Collections.IList list, object? start, object? deleteCount, params object?[] items)
    {
        int s = start == null ? 0 : (int)Convert.ChangeType(start, typeof(int));
        int dc = deleteCount == null ? 0 : (int)Convert.ChangeType(deleteCount, typeof(int));
        if (s < 0) s = list.Count + s;
        s = Math.Max(0, Math.Min(list.Count, s));
        dc = Math.Max(0, Math.Min(list.Count - s, dc));
        var removed = new List<dynamic?>();
        for (int i = 0; i < dc; i++)
        {
            removed.Add(list[s]);
            list.RemoveAt(s);
        }
        for (int i = 0; i < items.Length; i++)
        {
            list.Insert(s + i, items[i]);
        }
        return removed;
    }
    public static dynamic? match(this string? str, System.Text.RegularExpressions.Regex regex)
    {
        if (str == null) return null;
        var match = regex.Match(str);
        if (match.Success)
        {
            RegExp._dollar_1 = match.Groups.Count > 1 ? match.Groups[1].Value : "";
            RegExp._dollar_2 = match.Groups.Count > 2 ? match.Groups[2].Value : "";
            var list = new List<dynamic?>();
            foreach (System.Text.RegularExpressions.Group g in match.Groups) list.Add(g.Value);
            return list;
        }
        return null;
    }
}


using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using TypedJint.Runtime;

namespace TypedJint;

public sealed class TypedJintEngine
{
    private readonly TypedJintOptions _options;
    private readonly ConcurrentDictionary<string, ICompiledFunction> _compiled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _globals = new(StringComparer.Ordinal);

    public TypedJintEngine(TypedJintOptions? options = null)
    {
        _options = options ?? new TypedJintOptions();
        Document = new DomDocument();
        RegisterDom(Document);
        if (_options.TypeScriptRegistry != null)
        {
            TypeScriptRegistry.Merge(_options.TypeScriptRegistry);
        }
    }

    public DomDocument Document { get; }
    public DomWindow Window { get; private set; } = null!;
    public TypeScriptTypeRegistry TypeScriptRegistry { get; } = new();
    public IReadOnlyDictionary<string, object?> Globals => _globals;

    public TypedJintEngine SetValue(string name, object? value)
    {
        _globals[name] = value;
        return this;
    }

    public TypedJintEngine RegisterHostObject(string name, object instance) => SetValue(name, instance);

    public TypedJintEngine RegisterDom(DomDocument document)
    {
        Window = new DomWindow(document);
        SetValue("document", document);
        SetValue("window", Window);
        return this;
    }

    public TypedCompilationResult Execute(string source)
    {
        var result = _options.EnableCompilation
            ? new TypedJsCompiler(_globals, _options, TypeScriptRegistry).Compile(source)
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

        // Compile dynamic/fallback functions using JavaScriptRuntimeEngine
        try
        {
            var runtimeEngine = new JavaScriptRuntimeEngine();
            foreach (var global in _globals)
            {
                runtimeEngine.SetValue(global.Key, global.Value);
            }
            var runtimeResult = runtimeEngine.Execute(source);
            foreach (var fn in runtimeResult.RuntimeFunctions)
            {
                if (!_compiled.ContainsKey(fn.Key))
                {
                    _compiled[fn.Key] = fn.Value;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TypedJintEngine] Note: Dynamic fallback compilation: {ex.Message}");
        }

        return result;
    }

    public object? GetValue(string name)
    {
        return _globals.TryGetValue(name, out var val) ? val : null;
    }

    public object? Invoke(string functionName, params object?[] arguments)
    {
        if (_compiled.TryGetValue(functionName, out var compiled))
        {
            return compiled.Invoke(arguments);
        }

        throw new InvalidOperationException($"Function '{functionName}' is not compiled and Jint fallback is not available.");
    }
}

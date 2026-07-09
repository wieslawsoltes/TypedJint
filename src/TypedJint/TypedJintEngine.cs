using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Jint;
using TypedJint.Runtime;

namespace TypedJint;

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
        if (_options.TypeScriptRegistry != null)
        {
            TypeScriptRegistry.Merge(_options.TypeScriptRegistry);
        }
    }

    public Engine Jint => _jint;
    public DomDocument Document { get; }
    public DomWindow Window { get; private set; } = null!;
    public TypeScriptTypeRegistry TypeScriptRegistry { get; } = new();

    public TypedJintEngine SetValue(string name, object? value)
    {
        _globals[name] = value;
        _jint.SetValue(name, value);
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

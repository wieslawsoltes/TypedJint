using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.RegularExpressions;
using Jint;

namespace TypedJint;

public sealed class FullJavaScriptRuntimeOptions
{
    public bool AllowClr { get; init; } = true;
    public bool DrainPromiseJobsAfterInvoke { get; init; } = true;
    public Action<TypedDiagnostic>? DiagnosticSink { get; init; }
}

public sealed class FullJavaScriptRuntimeResult
{
    public required IReadOnlyDictionary<string, ICompiledFunction> RuntimeFunctions { get; init; }
    public required IReadOnlyList<string> ClassDeclarations { get; init; }
    public required IReadOnlyList<TypedDiagnostic> Diagnostics { get; init; }

    public bool Verified => Diagnostics.All(x => x.Severity != TypedDiagnosticSeverity.Error);
}

public sealed class FullJavaScriptRuntimeEngine
{
    private readonly FullJavaScriptRuntimeOptions _options;
    private readonly Engine _engine;
    private readonly Dictionary<string, ICompiledFunction> _runtimeFunctions = new(StringComparer.Ordinal);

    public FullJavaScriptRuntimeEngine(FullJavaScriptRuntimeOptions? options = null)
    {
        _options = options ?? new FullJavaScriptRuntimeOptions();
        _engine = _options.AllowClr ? new Engine(cfg => cfg.AllowClr()) : new Engine();
        Document = new DomDocument();
        SetValue("document", Document);
        SetValue("window", new DomWindow(Document));
    }

    public Engine Jint => _engine;
    public DomDocument Document { get; }
    public IReadOnlyDictionary<string, ICompiledFunction> RuntimeFunctions => new ReadOnlyDictionary<string, ICompiledFunction>(_runtimeFunctions);

    public FullJavaScriptRuntimeEngine SetValue(string name, object? value)
    {
        _engine.SetValue(name, value);
        return this;
    }

    public FullJavaScriptRuntimeEngine RegisterHostObject(string name, object instance) => SetValue(name, instance);

    public FullJavaScriptRuntimeResult Execute(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var diagnostics = new List<TypedDiagnostic>();
        _engine.Execute(source);
        DrainPromiseJobs();

        var declarations = FullJavaScriptDeclarationScanner.Scan(source);
        _runtimeFunctions.Clear();

        foreach (var functionName in declarations.Functions)
        {
            var function = new JintRuntimeFunction(functionName, _engine, _options.DrainPromiseJobsAfterInvoke);
            _runtimeFunctions[functionName] = function;
            diagnostics.Add(new TypedDiagnostic(
                "TJ1000",
                TypedDiagnosticSeverity.Info,
                $"Function '{functionName}' is available through the full JavaScript runtime backend."));
        }

        foreach (var className in declarations.Classes)
        {
            diagnostics.Add(new TypedDiagnostic(
                "TJ1001",
                TypedDiagnosticSeverity.Info,
                $"Class '{className}' is available to JavaScript through the full runtime backend."));
        }

        foreach (var diagnostic in diagnostics)
        {
            _options.DiagnosticSink?.Invoke(diagnostic);
        }

        return new FullJavaScriptRuntimeResult
        {
            RuntimeFunctions = new ReadOnlyDictionary<string, ICompiledFunction>(_runtimeFunctions),
            ClassDeclarations = declarations.Classes,
            Diagnostics = diagnostics
        };
    }

    public object? Invoke(string functionName, params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);

        if (_runtimeFunctions.TryGetValue(functionName, out var function))
        {
            return function.Invoke(arguments);
        }

        var result = _engine.Invoke(functionName, arguments).ToObject();
        DrainPromiseJobs();
        return result;
    }

    public object? Evaluate(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = _engine.Evaluate(source).ToObject();
        DrainPromiseJobs();
        return result;
    }

    public void DrainPromiseJobs()
    {
        if (!_options.DrainPromiseJobsAfterInvoke)
        {
            return;
        }

        TryInvokeNoArgs(_engine.GetType().GetProperty("Advanced", BindingFlags.Instance | BindingFlags.Public)?.GetValue(_engine), "ProcessTasks");
        TryInvokeNoArgs(_engine.GetType().GetProperty("Advanced", BindingFlags.Instance | BindingFlags.Public)?.GetValue(_engine), "RunAvailableContinuations");
        TryInvokeNoArgs(_engine.GetType().GetProperty("Advanced", BindingFlags.Instance | BindingFlags.Public)?.GetValue(_engine), "RunJobs");
    }

    private static void TryInvokeNoArgs(object? target, string methodName)
    {
        if (target is null)
        {
            return;
        }

        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes);
        method?.Invoke(target, null);
    }
}

public sealed class JintRuntimeFunction : ICompiledFunction
{
    private readonly Engine _engine;
    private readonly bool _drainPromiseJobs;
    private readonly Func<object?[], object?> _delegate;

    public JintRuntimeFunction(string name, Engine engine, bool drainPromiseJobs)
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

        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes);
        method?.Invoke(target, null);
    }
}

public sealed record FullJavaScriptDeclarationScanResult(
    IReadOnlyList<string> Functions,
    IReadOnlyList<string> Classes);

public static class FullJavaScriptDeclarationScanner
{
    private static readonly Regex FunctionRegex = new(@"(?:^|[^A-Za-z0-9_$])(?:async\s+)?function\s*\*?\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex ClassRegex = new(@"(?:^|[^A-Za-z0-9_$])class\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\b", RegexOptions.Compiled);

    public static FullJavaScriptDeclarationScanResult Scan(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var functions = FunctionRegex
            .Matches(source)
            .Select(x => x.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var classes = ClassRegex
            .Matches(source)
            .Select(x => x.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new FullJavaScriptDeclarationScanResult(functions, classes);
    }
}

using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TypedJint;

public sealed record GeneratedCSharpCompilerOptions(
    string AssemblyName = "TypedJint.GeneratedScript",
    bool Optimize = true,
    bool AllowUnsafe = false)
{
    public Microsoft.CodeAnalysis.CSharp.LanguageVersion CSharpLanguageVersion { get; init; } = Microsoft.CodeAnalysis.CSharp.LanguageVersion.Preview;
}

public sealed record GeneratedCSharpDiagnostic(
    string Id,
    string Severity,
    string Message,
    int? Line,
    int? Column)
{
    public override string ToString()
    {
        var location = Line is null || Column is null ? string.Empty : $"({Line},{Column}): ";
        return $"{location}{Severity} {Id}: {Message}";
    }
}

public sealed class GeneratedCSharpBuildResult
{
    public required string Source { get; init; }
    public required bool Success { get; init; }
    public required IReadOnlyList<GeneratedCSharpDiagnostic> Diagnostics { get; init; }
    public Assembly? Assembly { get; init; }
    public AssemblyLoadContext? LoadContext { get; init; }

    public string DiagnosticsText => Diagnostics.Count == 0
        ? "No diagnostics."
        : string.Join(Environment.NewLine, Diagnostics.Select(x => x.ToString()));

    public void Unload()
    {
        LoadContext?.Unload();
    }
}

public sealed class GeneratedCSharpExecutionResult
{
    public required GeneratedCSharpBuildResult Build { get; init; }
    public object? ReturnValue { get; init; }
    public object? Instance { get; init; }
    public Exception? Exception { get; init; }

    public bool Success => Build.Success && Exception is null;

    public void Unload()
    {
        Build.Unload();
    }
}

public sealed class GeneratedCSharpScriptInstance
{
    public GeneratedCSharpScriptInstance(Assembly assembly, Type scriptType, object instance)
    {
        Assembly = assembly;
        ScriptType = scriptType;
        Instance = instance;
    }

    public Assembly Assembly { get; }
    public Type ScriptType { get; }
    public object Instance { get; }

    public object? InvokeRuntime(string functionName, params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        var method = ScriptType.GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(ScriptType.FullName ?? ScriptType.Name, "Invoke");
        return method.Invoke(Instance, new object?[] { functionName, arguments });
    }

    public object? InvokeMethod(string methodName, params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        var method = ScriptType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(ScriptType.FullName ?? ScriptType.Name, methodName);
        return method.Invoke(Instance, arguments);
    }

    public IReadOnlyList<MethodInfo> PublicScriptMethods => ScriptType
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        .Where(x => !x.IsSpecialName)
        .ToArray();
}

public static class GeneratedCSharpCompiler
{
    public static GeneratedCSharpBuildResult BuildLibrary(
        string source,
        GeneratedCSharpCompilerOptions? options = null)
    {
        return BuildCore(source, OutputKind.DynamicallyLinkedLibrary, options);
    }

    public static GeneratedCSharpExecutionResult RunTopLevelProgram(
        string source,
        GeneratedCSharpCompilerOptions? options = null)
    {
        var build = BuildCore(source, OutputKind.ConsoleApplication, options);
        if (!build.Success || build.Assembly is null)
        {
            return new GeneratedCSharpExecutionResult { Build = build };
        }

        try
        {
            var entryPoint = build.Assembly.EntryPoint
                ?? throw new MissingMethodException("Generated program entry point was not found.");
            var parameters = entryPoint.GetParameters().Length == 0
                ? null
                : new object?[] { Array.Empty<string>() };
            var value = entryPoint.Invoke(null, parameters);
            value = UnwrapTask(value);
            return new GeneratedCSharpExecutionResult
            {
                Build = build,
                ReturnValue = value
            };
        }
        catch (TargetInvocationException ex)
        {
            return new GeneratedCSharpExecutionResult
            {
                Build = build,
                Exception = ex.InnerException ?? ex
            };
        }
        catch (Exception ex)
        {
            return new GeneratedCSharpExecutionResult
            {
                Build = build,
                Exception = ex
            };
        }
    }

    public static GeneratedCSharpExecutionResult CreateScriptInstance(
        string source,
        string typeName = "ScriptModule",
        GeneratedCSharpCompilerOptions? options = null)
    {
        var build = BuildLibrary(source, options);
        if (!build.Success || build.Assembly is null)
        {
            return new GeneratedCSharpExecutionResult { Build = build };
        }

        try
        {
            var type = build.Assembly.GetType(typeName)
                ?? build.Assembly.GetExportedTypes().FirstOrDefault(x => string.Equals(x.Name, typeName, StringComparison.Ordinal))
                ?? throw new TypeLoadException($"Generated type '{typeName}' was not found.");
            var instance = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException($"Generated type '{type.FullName ?? type.Name}' could not be instantiated.");
            return new GeneratedCSharpExecutionResult
            {
                Build = build,
                Instance = new GeneratedCSharpScriptInstance(build.Assembly, type, instance)
            };
        }
        catch (TargetInvocationException ex)
        {
            return new GeneratedCSharpExecutionResult
            {
                Build = build,
                Exception = ex.InnerException ?? ex
            };
        }
        catch (Exception ex)
        {
            return new GeneratedCSharpExecutionResult
            {
                Build = build,
                Exception = ex
            };
        }
    }

    private static GeneratedCSharpBuildResult BuildCore(
        string source,
        OutputKind outputKind,
        GeneratedCSharpCompilerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new GeneratedCSharpCompilerOptions();

        var parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(options.CSharpLanguageVersion)
            .WithDocumentationMode(DocumentationMode.None);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);

        var compilationOptions = new CSharpCompilationOptions(outputKind)
            .WithOptimizationLevel(options.Optimize ? OptimizationLevel.Release : OptimizationLevel.Debug)
            .WithNullableContextOptions(NullableContextOptions.Enable)
            .WithAllowUnsafe(options.AllowUnsafe);

        var assemblyName = options.AssemblyName + "." + Guid.NewGuid().ToString("N");
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { syntaxTree },
            CreateReferences(),
            compilationOptions);

        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        var diagnostics = emit.Diagnostics
            .Where(x => x.Severity == DiagnosticSeverity.Error || x.Severity == DiagnosticSeverity.Warning)
            .Select(ToDiagnostic)
            .ToArray();

        if (!emit.Success)
        {
            return new GeneratedCSharpBuildResult
            {
                Source = source,
                Success = false,
                Diagnostics = diagnostics
            };
        }

        pe.Position = 0;
        var loadContext = new GeneratedCSharpAssemblyLoadContext(assemblyName);
        var assembly = loadContext.LoadFromStream(pe);
        return new GeneratedCSharpBuildResult
        {
            Source = source,
            Success = true,
            Diagnostics = diagnostics,
            Assembly = assembly,
            LoadContext = loadContext
        };
    }

    private static IReadOnlyList<MetadataReference> CreateReferences()
    {
        var references = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrWhiteSpace(trustedPlatformAssemblies))
        {
            foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator))
            {
                AddReference(references, path);
            }
        }

        AddReference(references, typeof(object).Assembly.Location);
        AddReference(references, typeof(Console).Assembly.Location);
        AddReference(references, typeof(Enumerable).Assembly.Location);
        AddReference(references, typeof(TypedJintEngine).Assembly.Location);
        AddReference(references, typeof(Jint.Engine).Assembly.Location);

        return references.Values.ToArray();
    }

    private static void AddReference(IDictionary<string, MetadataReference> references, string? location)
    {
        if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
        {
            return;
        }

        references[location] = MetadataReference.CreateFromFile(location);
    }

    private static GeneratedCSharpDiagnostic ToDiagnostic(Diagnostic diagnostic)
    {
        var lineSpan = diagnostic.Location.IsInSource
            ? diagnostic.Location.GetLineSpan()
            : default;
        return new GeneratedCSharpDiagnostic(
            diagnostic.Id,
            diagnostic.Severity.ToString(),
            diagnostic.GetMessage(),
            diagnostic.Location.IsInSource ? lineSpan.StartLinePosition.Line + 1 : null,
            diagnostic.Location.IsInSource ? lineSpan.StartLinePosition.Character + 1 : null);
    }

    private static object? UnwrapTask(object? value)
    {
        if (value is not Task task)
        {
            return value;
        }

        task.GetAwaiter().GetResult();
        var type = task.GetType();
        return type.IsGenericType ? type.GetProperty("Result")?.GetValue(task) : null;
    }

    private sealed class GeneratedCSharpAssemblyLoadContext : AssemblyLoadContext
    {
        public GeneratedCSharpAssemblyLoadContext(string name)
            : base(name, isCollectible: true)
        {
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            return AssemblyLoadContext.Default.Assemblies.FirstOrDefault(
                assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName));
        }
    }
}

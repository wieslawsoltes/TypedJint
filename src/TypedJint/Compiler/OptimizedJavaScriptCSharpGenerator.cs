using System.Text;
using TypedJint.Runtime;

namespace TypedJint;

public sealed record OptimizedJavaScriptCSharpGenerationOptions(
    string ClassName = "ScriptModule",
    bool EmitNativeMethods = true,
    bool EmitRuntimeFallback = false,
    bool EmitAggressiveInlining = true,
    bool IncludeNullable = true,
    TypeScriptTypeRegistry? TypeScriptRegistry = null,
    IReadOnlyDictionary<string, object?>? Globals = null);

public sealed record OptimizedJavaScriptCSharpGenerationResult(
    string Source,
    string PreviewSource,
    IReadOnlyList<string> NativeFunctions,
    IReadOnlyList<string> RuntimeFunctions,
    IReadOnlyList<TypedDiagnostic> Diagnostics);

public static class OptimizedJavaScriptCSharpGenerator
{
    public static OptimizedJavaScriptCSharpGenerationResult Generate(
        string source,
        OptimizedJavaScriptCSharpGenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new OptimizedJavaScriptCSharpGenerationOptions();

        var diagnostics = new List<TypedDiagnostic>();
        var nativeFunctions = options.EmitNativeMethods
            ? CollectNativeFunctions(source, diagnostics, options)
            : Array.Empty<JsFunctionDeclaration>();

        var safeFunctionsDict = new Dictionary<string, JsFunctionDeclaration>(StringComparer.Ordinal);
        foreach (var fn in nativeFunctions)
        {
            if (fn.Name != null)
            {
                safeFunctionsDict.TryAdd(fn.Name, fn);
            }
        }
        var executableSource = FullJsToCSharpTranspiler.Transpile(source, options.ClassName, safeFunctionsDict, emitRuntimeFallback: false);
        var nativeFunctionNames = JavaScriptDeclarationScanner.Scan(source).Functions.ToArray();
        var runtimeFunctions = Array.Empty<string>();
        return new OptimizedJavaScriptCSharpGenerationResult(
            executableSource,
            executableSource,
            nativeFunctionNames,
            runtimeFunctions,
            diagnostics);
    }



    private static IReadOnlyList<JsFunctionDeclaration> CollectNativeFunctions(
        string source,
        List<TypedDiagnostic> diagnostics,
        OptimizedJavaScriptCSharpGenerationOptions options)
    {
        var functions = new List<JsFunctionDeclaration>();
        foreach (var candidate in JavaScriptFunctionSourceScanner.Scan(source))
        {
            if (TryParseAndCompile(candidate, diagnostics, options, out var parsed))
            {
                functions.Add(parsed);
            }
        }

        return functions;
    }

    private static bool TryParseAndCompile(
        JavaScriptFunctionSource candidate,
        List<TypedDiagnostic> diagnostics,
        OptimizedJavaScriptCSharpGenerationOptions options,
        out JsFunctionDeclaration function)
    {
        function = null!;
        try
        {
            var parsed = SimpleJsParser.ParseFunctions(candidate.Source).SingleOrDefault();
            if (parsed is null)
            {
                diagnostics.Add(new TypedDiagnostic(
                    "TJ2003",
                    TypedDiagnosticSeverity.Info,
                    $"Function '{candidate.Name}' was not emitted as native C# because it could not be parsed as a typed function."));
                return false;
            }

            if (!IsNativelyCompilable(candidate.Source, parsed.Name, diagnostics, options))
            {
                return false;
            }

            function = parsed;
            return true;
        }
        catch (Exception ex)
        {
            diagnostics.Add(new TypedDiagnostic(
                "TJ2004",
                TypedDiagnosticSeverity.Info,
                $"Function '{candidate.Name}' uses the runtime path because it is outside the native typed subset: {ex.Message}"));
            return false;
        }
    }

    private static bool IsNativelyCompilable(
        string functionSource,
        string functionName,
        List<TypedDiagnostic> diagnostics,
        OptimizedJavaScriptCSharpGenerationOptions options)
    {
        try
        {
            var globals = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Math"] = JavaScriptMath.Instance,
                ["console"] = JavaScriptConsole.Instance,
                ["net"] = JavaScriptNetwork.Instance,
                ["encoding"] = JavaScriptEncoding.Instance,
                ["json"] = JavaScriptJson.Instance,
                ["time"] = JavaScriptTime.Instance
            };

            if (options.Globals != null)
            {
                foreach (var pair in options.Globals)
                {
                    globals[pair.Key] = pair.Value;
                }
            }

            var compileResult = new TypedJsCompiler(
                globals,
                new TypedJintOptions
                {
                    CompilationMode = TypedCompilationMode.CompileSafeFunctionsOnly,
                    ThrowOnCompilationFailure = false,
                    TypeScriptRegistry = options.TypeScriptRegistry
                }).Compile(functionSource);

            diagnostics.AddRange(compileResult.Diagnostics.Where(x => x.Severity != TypedDiagnosticSeverity.Warning));
            return compileResult.CompiledFunctions.ContainsKey(functionName);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new TypedDiagnostic(
                "TJ2002",
                TypedDiagnosticSeverity.Info,
                $"Function '{functionName}' uses the runtime path because it is outside the native typed subset: {ex.Message}"));
            return false;
        }
    }


}

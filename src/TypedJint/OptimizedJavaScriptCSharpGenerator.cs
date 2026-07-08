using System.Text;

namespace TypedJint;

public sealed record OptimizedJavaScriptCSharpGenerationOptions(
    string ClassName = "ScriptModule",
    bool EmitNativeMethods = true,
    bool EmitRuntimeFallback = true,
    bool EmitAggressiveInlining = true,
    bool IncludeNullable = true);

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
            ? CollectNativeFunctions(source, diagnostics)
            : Array.Empty<JsFunctionDeclaration>();

        var classSources = JavaScriptClassSourceScanner.Scan(source);
        var executableSource = BuildExecutableSource(source, nativeFunctions, options);
        var nativeFunctionNames = nativeFunctions.Select(x => x.Name).ToArray();
        var runtimeFunctions = JavaScriptDeclarationScanner.Scan(source).Functions
            .Where(x => !nativeFunctionNames.Contains(x, StringComparer.Ordinal))
            .ToArray();
        var previewSource = BuildPreviewSource(nativeFunctions, runtimeFunctions, classSources, options);

        return new OptimizedJavaScriptCSharpGenerationResult(
            executableSource,
            previewSource,
            nativeFunctionNames,
            runtimeFunctions,
            diagnostics);
    }

    private static string BuildExecutableSource(
        string source,
        IReadOnlyList<JsFunctionDeclaration> nativeFunctions,
        OptimizedJavaScriptCSharpGenerationOptions options)
    {
        var classSources = JavaScriptClassSourceScanner.Scan(source);
        var classPreview = JavaScriptClassCSharpPreviewGenerator.Generate(classSources).TrimEnd();

        var builder = CreateHeader(options, emitClassDeclaration: true);
        builder.AppendLine("{");

        if (options.EmitRuntimeFallback)
        {
            builder.AppendLine("    private readonly JavaScriptRuntimeEngine _runtime;");
            builder.AppendLine();
            builder.Append("    private const string Source = ").Append(ToRawStringLiteral(source, indent: 4)).AppendLine(";");
            builder.AppendLine();
            builder.Append("    public ").Append(SanitizeIdentifier(options.ClassName)).AppendLine("()");
            builder.AppendLine("    {");
            builder.AppendLine("        _runtime = new JavaScriptRuntimeEngine().RegisterStandardLibrary();");
            builder.AppendLine("        _runtime.Execute(Source);");
            builder.AppendLine("    }");
            builder.AppendLine();
            AppendRuntimeMembers(builder);
        }
        else
        {
            builder.Append("    public ").Append(SanitizeIdentifier(options.ClassName)).AppendLine("()");
            builder.AppendLine("    {");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        AppendNativeMethods(builder, nativeFunctions, options);
        builder.AppendLine("}");

        if (!string.IsNullOrWhiteSpace(classPreview))
        {
            builder.AppendLine();
            builder.AppendLine(classPreview);
        }

        return builder.ToString();
    }

    private static string BuildPreviewSource(
        IReadOnlyList<JsFunctionDeclaration> nativeFunctions,
        IReadOnlyList<string> runtimeFunctions,
        IReadOnlyList<JavaScriptClassSource> classSources,
        OptimizedJavaScriptCSharpGenerationOptions options)
    {
        var builder = CreateHeader(options, emitClassDeclaration: false);
        var classPreview = JavaScriptClassCSharpPreviewGenerator.Generate(classSources).TrimEnd();
        if (!string.IsNullOrWhiteSpace(classPreview))
        {
            builder.AppendLine(classPreview);
            builder.AppendLine();
        }

        builder.Append("public sealed class ").Append(SanitizeIdentifier(options.ClassName)).AppendLine();
        builder.AppendLine("{");

        if (options.EmitRuntimeFallback)
        {
            builder.AppendLine("    private readonly JavaScriptRuntimeEngine _runtime;");
            builder.AppendLine();
            builder.Append("    public ").Append(SanitizeIdentifier(options.ClassName)).AppendLine("()");
            builder.AppendLine("    {");
            builder.AppendLine("        _runtime = new JavaScriptRuntimeEngine().RegisterStandardLibrary();");
            builder.AppendLine("    }");
            builder.AppendLine();
            AppendRuntimeMembers(builder);
            AppendRuntimeFunctionFacades(builder, runtimeFunctions);
        }
        else
        {
            builder.Append("    public ").Append(SanitizeIdentifier(options.ClassName)).AppendLine("()");
            builder.AppendLine("    {");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        AppendNativeMethods(builder, nativeFunctions, options);
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static StringBuilder CreateHeader(OptimizedJavaScriptCSharpGenerationOptions options, bool emitClassDeclaration)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        if (options.IncludeNullable)
        {
            builder.AppendLine("#nullable enable");
            builder.AppendLine("#nullable disable warnings");
        }

        builder.AppendLine("using System;");
        builder.AppendLine("using System.Collections.Generic;");
        builder.AppendLine("using System.Runtime.CompilerServices;");
        builder.AppendLine("using TypedJint;");
        builder.AppendLine();
        if (emitClassDeclaration)
        {
            builder.Append("public sealed class ").Append(SanitizeIdentifier(options.ClassName)).AppendLine();
        }

        return builder;
    }

    private static void AppendRuntimeMembers(StringBuilder builder)
    {
        builder.AppendLine("    public JavaScriptRuntimeEngine Runtime => _runtime;");
        builder.AppendLine();
        builder.AppendLine("    public object? Invoke(string functionName, params object?[] arguments) => _runtime.Invoke(functionName, arguments);");
        builder.AppendLine();
        builder.AppendLine("    public object? Evaluate(string expression) => _runtime.Evaluate(expression);");
        builder.AppendLine();
    }

    private static void AppendRuntimeFunctionFacades(StringBuilder builder, IReadOnlyList<string> runtimeFunctions)
    {
        foreach (var function in runtimeFunctions)
        {
            builder.Append("    public object? ")
                .Append(SanitizeIdentifier(function))
                .Append("(params object?[] arguments) => Invoke(nameof(")
                .Append(SanitizeIdentifier(function))
                .AppendLine("), arguments);");
            builder.AppendLine();
        }
    }

    private static void AppendNativeMethods(
        StringBuilder builder,
        IReadOnlyList<JsFunctionDeclaration> nativeFunctions,
        OptimizedJavaScriptCSharpGenerationOptions options)
    {
        foreach (var function in nativeFunctions)
        {
            EmitNativeMethod(builder, function, options);
        }
    }

    private static IReadOnlyList<JsFunctionDeclaration> CollectNativeFunctions(string source, List<TypedDiagnostic> diagnostics)
    {
        var functions = new List<JsFunctionDeclaration>();
        foreach (var candidate in JavaScriptFunctionSourceScanner.Scan(source))
        {
            if (TryParseAndCompile(candidate, diagnostics, out var parsed))
            {
                functions.Add(parsed);
            }
        }

        return functions;
    }

    private static bool TryParseAndCompile(
        JavaScriptFunctionSource candidate,
        List<TypedDiagnostic> diagnostics,
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

            if (!IsNativelyCompilable(candidate.Source, parsed.Name, diagnostics))
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

    private static bool IsNativelyCompilable(string functionSource, string functionName, List<TypedDiagnostic> diagnostics)
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

            var compileResult = new TypedJsCompiler(
                globals,
                new TypedJintOptions
                {
                    CompilationMode = TypedCompilationMode.CompileSafeFunctionsOnly,
                    ThrowOnCompilationFailure = false
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

    private static void EmitNativeMethod(StringBuilder builder, JsFunctionDeclaration function, OptimizedJavaScriptCSharpGenerationOptions options)
    {
        var method = TypedJintTranspiler.TranspileFunctionToCSharp(function)
            .Replace("public static ", "public ", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        if (options.EmitAggressiveInlining)
        {
            builder.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        }

        foreach (var line in method)
        {
            if (line.Length == 0)
            {
                builder.AppendLine();
            }
            else
            {
                builder.Append("    ").AppendLine(line);
            }
        }

        builder.AppendLine();
    }

    private static string ToRawStringLiteral(string value, int indent)
    {
        var quotes = 3;
        while (value.Contains(new string('"', quotes), StringComparison.Ordinal))
        {
            quotes++;
        }

        var fence = new string('"', quotes);
        var pad = new string(' ', indent);
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var builder = new StringBuilder();
        builder.AppendLine(fence);
        foreach (var line in lines)
        {
            builder.Append(pad).AppendLine(line);
        }

        builder.Append(pad).Append(fence);
        return builder.ToString();
    }

    private static string SanitizeIdentifier(string value) => value switch
    {
        "class" or "namespace" or "public" or "private" or "protected" or "internal" or "static" or "void" or "double" or "string" or "bool" or "object" or "return" => "@" + value,
        _ => value
    };
}

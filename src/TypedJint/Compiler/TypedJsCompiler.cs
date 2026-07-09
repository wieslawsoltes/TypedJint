using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Expression = System.Linq.Expressions.Expression;
using TypedJint.Runtime;

namespace TypedJint;

public sealed class TypedJsCompiler
{
    private readonly IReadOnlyDictionary<string, object?> _globals;
    private readonly TypedJintOptions _options;
    private readonly TypeScriptTypeRegistry? _typeRegistry;
    private readonly List<TypedDiagnostic> _diagnostics = new();
    private readonly Dictionary<string, ICompiledFunction> _compiled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FallbackInfo> _fallbacks = new(StringComparer.Ordinal);

    public TypedJsCompiler(IReadOnlyDictionary<string, object?> globals, TypedJintOptions options, TypeScriptTypeRegistry? typeRegistry = null)
    {
        _globals = globals;
        _options = options;
        _typeRegistry = typeRegistry;
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
                EmitRuntimeFallback = false,
                EmitAggressiveInlining = true,
                TypeScriptRegistry = _typeRegistry,
                Globals = _globals
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

                    Delegate del;
                    var methodParams = methodInfo.GetParameters();
                    bool needsAdapter = methodInfo.ReturnType != fn.Annotation.ReturnType.ClrType ||
                                        methodParams.Length != paramTypes.Length ||
                                        !methodParams.Zip(paramTypes, (mp, pt) => mp.ParameterType == pt).All(x => x);

                    if (needsAdapter)
                    {
                        var instanceExpr = methodInfo.IsStatic ? null : Expression.Constant(scriptInstance.Instance);
                        var paramExprs = paramTypes.Select((t, i) => Expression.Parameter(t, $"p{i}")).ToArray();
                        
                        var methodArgs = new Expression[methodParams.Length];
                        for (int i = 0; i < methodParams.Length; i++)
                        {
                            if (i < paramExprs.Length)
                            {
                                methodArgs[i] = Expression.Convert(paramExprs[i], methodParams[i].ParameterType);
                            }
                            else
                            {
                                methodArgs[i] = Expression.Default(methodParams[i].ParameterType);
                            }
                        }
                        
                        var callExpr = Expression.Call(instanceExpr, methodInfo, methodArgs);
                        Expression bodyExpr = callExpr;
                        if (fn.Annotation.ReturnType.ClrType == typeof(void) && methodInfo.ReturnType != typeof(void))
                        {
                            bodyExpr = Expression.Block(callExpr, Expression.Empty());
                        }
                        else if (fn.Annotation.ReturnType.ClrType != typeof(void))
                        {
                            bodyExpr = Expression.Convert(callExpr, fn.Annotation.ReturnType.ClrType);
                        }
                        
                        var lambda = Expression.Lambda(delegateType, bodyExpr, paramExprs);
                        del = lambda.Compile();
                    }
                    else
                    {
                        del = Delegate.CreateDelegate(delegateType, methodInfo.IsStatic ? null : scriptInstance.Instance, methodInfo);
                    }
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

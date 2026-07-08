using Xunit;

namespace TypedJint.Tests;

public sealed class GeneratedCSharpCompilerTests
{
    [Fact]
    public void BuildsAndRunsRuntimeTopLevelGeneratedCode()
    {
        var source = JavaScriptCSharpGenerator.GenerateRuntimeTopLevelStatements(
            """
            function run() {
                console.log("hello generated runtime");
                return 42;
            }
            """);

        var result = GeneratedCSharpCompiler.RunTopLevelProgram(source);

        Assert.True(result.Success, result.Build.DiagnosticsText);
    }

    [Fact]
    public void BuildsOptimizedHybridGeneratedClassAndInvokesRuntimeFunction()
    {
        var generated = OptimizedJavaScriptCSharpGenerator.Generate(
            """
            function answer() {
                return 42;
            }
            """);

        var result = GeneratedCSharpCompiler.CreateScriptInstance(generated.Source);

        Assert.True(result.Success, result.Build.DiagnosticsText + Environment.NewLine + result.Exception?.Message);
        var script = Assert.IsType<GeneratedCSharpScriptInstance>(result.Instance);
        Assert.Equal(42.0, Convert.ToDouble(script.InvokeRuntime("answer")));
    }

    [Fact]
    public void BuildsOptimizedHybridGeneratedClassAndInvokesNativeMethod()
    {
        var generated = OptimizedJavaScriptCSharpGenerator.Generate(
            """
            /**
             * @param {number} a
             * @param {number} b
             * @returns {number}
             */
            function add(a, b) {
                return a + b;
            }
            """);

        var result = GeneratedCSharpCompiler.CreateScriptInstance(generated.Source);

        Assert.True(result.Success, result.Build.DiagnosticsText + Environment.NewLine + result.Exception?.Message);
        var script = Assert.IsType<GeneratedCSharpScriptInstance>(result.Instance);
        Assert.Equal(42.0, Convert.ToDouble(script.InvokeMethod("add", 10.0, 32.0)));
        Assert.Equal(42.0, Convert.ToDouble(script.InvokeRuntime("add", 10.0, 32.0)));
    }

    [Fact]
    public void BuildsGeneratedClassWithStandardLibraryRuntimeCalls()
    {
        var generated = OptimizedJavaScriptCSharpGenerator.Generate(
            """
            function load() {
                return net.getString("data:text/plain,generated");
            }
            """);

        var result = GeneratedCSharpCompiler.CreateScriptInstance(generated.Source);

        Assert.True(result.Success, result.Build.DiagnosticsText + Environment.NewLine + result.Exception?.Message);
        var script = Assert.IsType<GeneratedCSharpScriptInstance>(result.Instance);
        Assert.Equal("generated", script.InvokeRuntime("load"));
    }

    [Fact]
    public void ReportsGeneratedCSharpCompilationErrors()
    {
        var result = GeneratedCSharpCompiler.BuildLibrary("public class Broken { public void M( { }");

        Assert.False(result.Success);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Contains(result.Diagnostics, x => x.Severity == "Error");
    }
}

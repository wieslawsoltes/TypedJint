using Xunit;

namespace TypedJint.Tests;

public sealed class StandardLibraryCompilerTests
{
    [Fact]
    public void CompilesMathIntrinsicsThroughTypedStandardLibrary()
    {
        var engine = new TypedJintEngine().RegisterStandardLibrary();

        var verified = engine.ExecuteVerified(
            """
            /**
             * @param {number} x
             * @returns {number}
             */
            function calc(x) {
                return Math.sqrt(x) + Math.abs(-2) + Math.max(1, 3) + Math.min(10, 1);
            }
            """,
            new Dictionary<string, object?[][]>
            {
                ["calc"] =
                [
                    [16.0],
                    [25.0]
                ]
            });

        Assert.True(verified.Verified, verified.CompilerOutputs["calc"].ToMarkdown());
        Assert.Equal(10.0, Convert.ToDouble(engine.Invoke("calc", 16.0)));
    }

    [Fact]
    public void CompilesMathConstantsThroughTypedStandardLibrary()
    {
        var engine = new TypedJintEngine().RegisterStandardLibrary();

        var verified = engine.ExecuteVerified(
            """
            /**
             * @returns {number}
             */
            function constants() {
                return Math.PI + Math.E;
            }
            """);

        Assert.True(verified.Verified, verified.CompilerOutputs["constants"].ToMarkdown());
        Assert.Equal(Math.PI + Math.E, Convert.ToDouble(engine.Invoke("constants")), precision: 10);
    }

    [Fact]
    public void CompilesArrayAndStringLength()
    {
        var engine = new TypedJintEngine();

        var verified = engine.ExecuteVerified(
            """
            /**
             * @returns {number}
             */
            function lengths() {
                let values = [1, 2, 3];
                let text = "abcd";
                return values.length + text.length;
            }
            """);

        Assert.True(verified.Verified, verified.CompilerOutputs["lengths"].ToMarkdown());
        Assert.Equal(7.0, Convert.ToDouble(engine.Invoke("lengths")));
    }

    [Fact]
    public void RewritesMathAndLengthInGeneratedCSharp()
    {
        var csharp = JavaScriptCSharpGenerator.GenerateStaticClass(
            """
            /**
             * @param {number} value
             * @returns {number}
             */
            function rewrite(value) {
                let values = [1, 2, 3];
                return Math.sqrt(value) + values.length;
            }
            """);

        Assert.Contains("Math.Sqrt(value)", csharp, StringComparison.Ordinal);
        Assert.Contains("values.Length", csharp, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.sqrt", csharp, StringComparison.Ordinal);
        Assert.DoesNotContain(".length", csharp, StringComparison.Ordinal);
    }

    [Fact]
    public void RewritesMathAndLengthInOptimizedGeneratedCSharp()
    {
        var generated = OptimizedJavaScriptCSharpGenerator.Generate(
            """
            /**
             * @param {number} value
             * @returns {number}
             */
            function rewrite(value) {
                let values = [1, 2, 3];
                return Math.sqrt(value) + values.length;
            }
            """,
            new OptimizedJavaScriptCSharpGenerationOptions(EmitRuntimeFallback: false));

        Assert.Contains("rewrite", generated.NativeFunctions);
        Assert.Contains("Math.Sqrt(value)", generated.Source, StringComparison.Ordinal);
        Assert.Contains("values.Length", generated.Source, StringComparison.Ordinal);
    }
}

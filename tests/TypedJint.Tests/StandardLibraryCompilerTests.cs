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
    public void CompilesConsoleLogThroughTypedStandardLibrary()
    {
        var previousOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);

        try
        {
            var engine = new TypedJintEngine().RegisterStandardLibrary();

            var verified = engine.ExecuteVerified(
                """
                /**
                 * @param {number} value
                 * @returns {number}
                 */
                function printAndReturn(value) {
                    console.log("value", value);
                    return value;
                }
                """,
                new Dictionary<string, object?[][]>
                {
                    ["printAndReturn"] =
                    [
                        [42.0]
                    ]
                });

            Assert.True(verified.Verified, verified.CompilerOutputs["printAndReturn"].ToMarkdown());
            Assert.Equal(42.0, Convert.ToDouble(engine.Invoke("printAndReturn", 42.0)));
            Assert.Contains("value 42", writer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
        }
    }

    [Fact]
    public void CompilesNetworkDataUriThroughTypedStandardLibrary()
    {
        var engine = new TypedJintEngine().RegisterStandardLibrary();

        var verified = engine.ExecuteVerified(
            """
            /**
             * @returns {string}
             */
            function loadText() {
                return net.getString("data:text/plain,hello%20typedjint");
            }
            """);

        Assert.True(verified.Verified, verified.CompilerOutputs["loadText"].ToMarkdown());
        Assert.Equal("hello typedjint", engine.Invoke("loadText"));
    }

    [Fact]
    public void CompilesEncodingThroughTypedStandardLibrary()
    {
        var engine = new TypedJintEngine().RegisterStandardLibrary();

        var verified = engine.ExecuteVerified(
            """
            /**
             * @returns {string}
             */
            function encodeText() {
                return encoding.base64Decode(encoding.base64Encode("typed"));
            }
            """);

        Assert.True(verified.Verified, verified.CompilerOutputs["encodeText"].ToMarkdown());
        Assert.Equal("typed", engine.Invoke("encodeText"));
    }

    [Fact]
    public void RuntimeEngineCanUseStandardLibraryConsoleAndNetwork()
    {
        var previousOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);

        try
        {
            var engine = new JavaScriptRuntimeEngine().RegisterStandardLibrary();
            engine.Execute(
                """
                function run() {
                    const text = net.getString("data:text/plain,runtime");
                    console.log(text);
                    return text;
                }
                """);

            Assert.Equal("runtime", engine.Invoke("run"));
            Assert.Contains("runtime", writer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
        }
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
        Assert.Contains("using System;", csharp, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.sqrt", csharp, StringComparison.Ordinal);
        Assert.DoesNotContain(".length", csharp, StringComparison.Ordinal);
    }

    [Fact]
    public void RewritesStandardLibraryCallsInGeneratedCSharp()
    {
        var csharp = JavaScriptCSharpGenerator.GenerateStaticClass(
            """
            /**
             * @returns {string}
             */
            function rewriteStdlib() {
                console.log("hello");
                return encoding.base64Decode(net.getString("data:text/plain,dHlwZWQ="));
            }
            """);

        Assert.Contains("Console.WriteLine(\"hello\")", csharp, StringComparison.Ordinal);
        Assert.Contains("JavaScriptNetwork.Instance.getString", csharp, StringComparison.Ordinal);
        Assert.Contains("JavaScriptEncoding.Instance.base64Decode", csharp, StringComparison.Ordinal);
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

using Xunit;

namespace TypedJint.Tests;

public sealed class ControlFlowCompilerTests
{
    [Fact]
    public void CompilesIfElseReturnFlow()
    {
        var engine = new TypedJintEngine();

        var verified = engine.ExecuteVerified(
            """
            /**
             * @param {number} x
             * @returns {number}
             */
            function abs(x) {
                if (x < 0) {
                    return -x;
                }

                return x;
            }
            """,
            new Dictionary<string, object?[][]>
            {
                ["abs"] = new[]
                {
                    new object?[] { -10.0 },
                    new object?[] { 0.0 },
                    new object?[] { 42.0 }
                }
            });

        Assert.True(verified.Verified, verified.CompilerOutputs["abs"].ToMarkdown());
        Assert.Equal(10.0, engine.Invoke("abs", -10.0));
        Assert.Contains("if ((x < 0))", verified.CompilerOutputs["abs"].CSharpPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void CompilesWhileLoop()
    {
        var engine = new TypedJintEngine();

        var verified = engine.ExecuteVerified(
            """
            /**
             * @param {number} n
             * @returns {number}
             */
            function factorial(n) {
                let acc = 1;
                while (n > 1) {
                    acc = acc * n;
                    n = n - 1;
                }

                return acc;
            }
            """,
            new Dictionary<string, object?[][]>
            {
                ["factorial"] = new[]
                {
                    new object?[] { 1.0 },
                    new object?[] { 3.0 },
                    new object?[] { 5.0 }
                }
            });

        Assert.True(verified.Verified, verified.CompilerOutputs["factorial"].ToMarkdown());
        Assert.Equal(120.0, engine.Invoke("factorial", 5.0));
        Assert.Contains("while", verified.CompilerOutputs["factorial"].NormalizedIr, StringComparison.Ordinal);
    }

    [Fact]
    public void CompilesForLoopModuloStrictEqualityAndUpdateExpression()
    {
        var engine = new TypedJintEngine();

        var verified = engine.ExecuteVerified(
            """
            /**
             * @param {number} limit
             * @returns {number}
             */
            function sumEven(limit) {
                let acc = 0;
                for (let i = 0; i <= limit; i++) {
                    if (i % 2 === 0) {
                        acc = acc + i;
                    }
                }

                return acc;
            }
            """,
            new Dictionary<string, object?[][]>
            {
                ["sumEven"] = new[]
                {
                    new object?[] { 0.0 },
                    new object?[] { 6.0 },
                    new object?[] { 10.0 }
                }
            });

        Assert.True(verified.Verified, verified.CompilerOutputs["sumEven"].ToMarkdown());
        Assert.Equal(30.0, engine.Invoke("sumEven", 10.0));
        Assert.Contains("for", verified.CompilerOutputs["sumEven"].NormalizedIr, StringComparison.Ordinal);
        Assert.Contains("i++", verified.CompilerOutputs["sumEven"].CSharpPreview, StringComparison.Ordinal);
        Assert.Contains("%", verified.CompilerOutputs["sumEven"].CSharpPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void TranspilesWholeScriptToCSharpPreview()
    {
        var csharp = TypedJintTranspiler.TranspileToCSharp(
            """
            /**
             * @param {number} a
             * @param {number} b
             * @returns {number}
             */
            function max(a, b) {
                if (a > b) {
                    return a;
                }

                return b;
            }
            """);

        Assert.Contains("public static class ScriptModule", csharp, StringComparison.Ordinal);
        Assert.Contains("public static double max(double a, double b)", csharp, StringComparison.Ordinal);
        Assert.Contains("if ((a > b))", csharp, StringComparison.Ordinal);
    }
}

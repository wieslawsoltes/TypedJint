using Xunit;

namespace TypedJint.Tests;

public sealed class JavaScriptCSharpGeneratorTests
{
    [Fact]
    public void GeneratesStaticClassForTypedFunctions()
    {
        var csharp = JavaScriptCSharpGenerator.GenerateStaticClass(
            """
            /**
             * @param {number} a
             * @param {number} b
             * @returns {number}
             */
            function add(a, b) {
                return a + b;
            }
            """,
            "MathScript");

        Assert.Contains("public static class MathScript", csharp, StringComparison.Ordinal);
        Assert.Contains("public static double add(double a, double b)", csharp, StringComparison.Ordinal);
        Assert.Contains("return (a + b);", csharp, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratesTopLevelStatementsWithLocalFunctionsAndGlobals()
    {
        var csharp = JavaScriptCSharpGenerator.GenerateTopLevelStatements(
            """
            /**
             * @param {number} a
             * @param {number} b
             * @returns {number}
             */
            function add(a, b) {
                return a + b;
            }

            let value = add(10, 32);
            value += 1;
            """);

        Assert.Contains("double add(double a, double b)", csharp, StringComparison.Ordinal);
        Assert.DoesNotContain("public static double add", csharp, StringComparison.Ordinal);
        Assert.Contains("var value = add(10", csharp, StringComparison.Ordinal);
        Assert.Contains("value += 1", csharp, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratesRuntimeTopLevelStatementsForArbitraryJavaScript()
    {
        var csharp = JavaScriptCSharpGenerator.GenerateRuntimeTopLevelStatements(
            """
            class Counter {
                constructor(value) {
                    this.value = value;
                }
            }
            """);

        Assert.Contains("var engine = new JavaScriptRuntimeEngine().RegisterStandardLibrary();", csharp, StringComparison.Ordinal);
        Assert.Contains("engine.Execute(", csharp, StringComparison.Ordinal);
        Assert.Contains("class Counter", csharp, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeTopLevelRawStringExpandsFenceWhenNeeded()
    {
        var csharp = JavaScriptCSharpGenerator.GenerateRuntimeTopLevelStatements("const s = \"\"\";\n");

        Assert.Contains("engine.Execute(\"\"\"\"", csharp, StringComparison.Ordinal);
        Assert.Contains("const s", csharp, StringComparison.Ordinal);
    }
}

using Xunit;

namespace TypedJint.Tests;

public sealed class OptimizedJavaScriptCSharpGeneratorTests
{
    [Fact]
    public void GeneratesNativeMethodsAndRuntimeFallbackInSingleClass()
    {
        var result = OptimizedJavaScriptCSharpGenerator.Generate(
            """
            /**
             * @param {number} limit
             * @returns {number}
             */
            function sumEven(limit) {
                let acc = 0;
                for (let i = 0; i <= limit; i++) {
                    if (i % 2 === 0) {
                        acc += i;
                    }
                }

                return acc;
            }

            class Counter {
                constructor(value) {
                    this.value = value;
                }

                next() {
                    return ++this.value;
                }
            }

            function runDynamic() {
                const counter = new Counter(41);
                return counter.next();
            }
            """);

        Assert.Contains("sumEven", result.NativeFunctions);
        Assert.Contains("runDynamic", result.RuntimeFunctions);
        Assert.Contains("private readonly JavaScriptRuntimeEngine _runtime;", result.Source, StringComparison.Ordinal);
        Assert.Contains("public double sumEven(double limit)", result.Source, StringComparison.Ordinal);
        Assert.Contains("MethodImplOptions.AggressiveInlining", result.Source, StringComparison.Ordinal);
        Assert.Contains("public object? Invoke(string functionName", result.Source, StringComparison.Ordinal);
        Assert.Contains("class Counter", result.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void SkipsNativeMethodsWhenSourceCannotBeParsedByTypedSubset()
    {
        var result = OptimizedJavaScriptCSharpGenerator.Generate(
            """
            function dynamicObject() {
                const value = { answer: 42 };
                return value.answer;
            }
            """);

        Assert.Empty(result.NativeFunctions);
        Assert.Contains("dynamicObject", result.RuntimeFunctions);
        Assert.Contains("Native C# generation skipped", string.Join(Environment.NewLine, result.Diagnostics.Select(x => x.Message)), StringComparison.Ordinal);
        Assert.Contains("_runtime.Execute(Source);", result.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void CanDisableRuntimeFallbackForPureNativeModules()
    {
        var result = OptimizedJavaScriptCSharpGenerator.Generate(
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
            new OptimizedJavaScriptCSharpGenerationOptions(EmitRuntimeFallback: false));

        Assert.Contains("add", result.NativeFunctions);
        Assert.DoesNotContain("JavaScriptRuntimeEngine", result.Source, StringComparison.Ordinal);
        Assert.Contains("public double add(double a, double b)", result.Source, StringComparison.Ordinal);
    }
}

using Xunit;

namespace TypedJint.Tests;

public sealed class JavaScriptClassCSharpPreviewTests
{
    [Fact]
    public void PreviewSourceGeneratesClassProjectionForJavaScriptClass()
    {
        var result = OptimizedJavaScriptCSharpGenerator.Generate(
            """
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

        Assert.Contains("public sealed class Counter", result.PreviewSource, StringComparison.Ordinal);
        Assert.Contains("public dynamic? value { get; set; }", result.PreviewSource, StringComparison.Ordinal);
        Assert.Contains("public Counter(dynamic? value)", result.PreviewSource, StringComparison.Ordinal);
        Assert.Contains("this.value = value;", result.PreviewSource, StringComparison.Ordinal);
        Assert.Contains("public dynamic? next()", result.PreviewSource, StringComparison.Ordinal);
        Assert.Contains("return ++this.value;", result.PreviewSource, StringComparison.Ordinal);
        Assert.Contains("public object? runDynamic(params object?[] arguments)", result.PreviewSource, StringComparison.Ordinal);
        Assert.DoesNotContain("private const string Source", result.PreviewSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewSourceIncludesClassProjectionAndNativeMethodsForMixedScript()
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
                        acc = acc + i;
                    }
                }

                return acc;
            }

            function createCounter() {
                let count = 0;

                return function() {
                    count++;
                    return count;
                };
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
        Assert.Contains("createCounter", result.RuntimeFunctions);
        Assert.Contains("runDynamic", result.RuntimeFunctions);
        Assert.Contains("public sealed class Counter", result.PreviewSource, StringComparison.Ordinal);
        Assert.Contains("public object? createCounter(params object?[] arguments)", result.PreviewSource, StringComparison.Ordinal);
        Assert.Contains("public object? runDynamic(params object?[] arguments)", result.PreviewSource, StringComparison.Ordinal);
        Assert.Contains("public double sumEven(double limit)", result.PreviewSource, StringComparison.Ordinal);
        Assert.DoesNotContain("function createCounter", result.PreviewSource, StringComparison.Ordinal);
        Assert.DoesNotContain("let count", result.PreviewSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewClassProjectionBuildsWithRoslyn()
    {
        var result = OptimizedJavaScriptCSharpGenerator.Generate(
            """
            class Counter {
                constructor(value) {
                    this.value = value;
                }

                next() {
                    return ++this.value;
                }
            }
            """);

        var build = GeneratedCSharpCompiler.BuildLibrary(result.PreviewSource);

        Assert.True(build.Success, build.DiagnosticsText);
    }

    [Fact]
    public void PreviewClassProjectionCanBeInstantiatedAndCalled()
    {
        var result = OptimizedJavaScriptCSharpGenerator.Generate(
            """
            class Counter {
                constructor(value) {
                    this.value = value;
                }

                next() {
                    return ++this.value;
                }
            }
            """);

        var build = GeneratedCSharpCompiler.BuildLibrary(result.PreviewSource);

        Assert.True(build.Success, build.DiagnosticsText);
        var counterType = build.Assembly!.GetType("Counter")
            ?? build.Assembly.GetExportedTypes().Single(type => type.Name == "Counter");
        var counter = Activator.CreateInstance(counterType, 41.0)!;
        var next = counterType.GetMethod("next")!;

        Assert.Equal(42.0, Convert.ToDouble(next.Invoke(counter, null)));
    }
}

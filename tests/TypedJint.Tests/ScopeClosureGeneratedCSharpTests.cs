using Xunit;

namespace TypedJint.Tests;

public sealed class ScopeClosureGeneratedCSharpTests
{
    [Fact]
    public void GeneratedCSharpExecutesClosureCounterTopLevelScenario()
    {
        var generated = OptimizedJavaScriptCSharpGenerator.Generate(
            """
            function createCounter() {
                let count = 0;

                return function() {
                    count++;
                    return count;
                };
            }

            const counter = createCounter();
            console.log(counter());
            console.log(counter());
            """);

        using var capture = JavaScriptConsole.Capture();
        var execution = GeneratedCSharpCompiler.CreateScriptInstance(generated.Source);

        Assert.True(execution.Success, execution.Build.DiagnosticsText + Environment.NewLine + execution.Exception?.Message);
        Assert.Contains("createCounter", generated.RuntimeFunctions);
        Assert.DoesNotContain(generated.Diagnostics, x => x.Severity == TypedDiagnosticSeverity.Warning);

        var output = NormalizeLines(capture.Output.ToString());
        Assert.Contains("1", output);
        Assert.Contains("2", output);
    }

    [Fact]
    public void MixedNativeAndDynamicScriptHasCleanRuntimeClassification()
    {
        var generated = OptimizedJavaScriptCSharpGenerator.Generate(
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

            const counter = createCounter();
            console.log(counter());
            console.log(counter());

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
                console.log("runtime", counter.next());
                return counter.next();
            }
            """);

        Assert.Contains("sumEven", generated.NativeFunctions);
        Assert.Contains("createCounter", generated.RuntimeFunctions);
        Assert.Contains("runDynamic", generated.RuntimeFunctions);
        Assert.DoesNotContain(generated.Diagnostics, x => x.Severity == TypedDiagnosticSeverity.Warning);

        using var capture = JavaScriptConsole.Capture();
        var execution = GeneratedCSharpCompiler.CreateScriptInstance(generated.Source);

        Assert.True(execution.Success, execution.Build.DiagnosticsText + Environment.NewLine + execution.Exception?.Message);
        var script = Assert.IsType<GeneratedCSharpScriptInstance>(execution.Instance);
        Assert.Equal(30.0, Convert.ToDouble(script.InvokeMethod("sumEven", 10.0)));
        Assert.Equal(43.0, Convert.ToDouble(script.InvokeRuntime("runDynamic")));

        var output = NormalizeLines(capture.Output.ToString());
        Assert.Contains("1", output);
        Assert.Contains("2", output);
        Assert.Contains("runtime 42", output);
    }

    [Fact]
    public void GeneratedCSharpPreservesLexicalScopeAndNestedFunctions()
    {
        var generated = OptimizedJavaScriptCSharpGenerator.Generate(
            """
            const outer = "outer";

            function runScope() {
                const middle = "middle";

                function innerFunction() {
                    const inner = "inner";
                    return outer + ":" + middle + ":" + inner;
                }

                return innerFunction();
            }
            """);

        var execution = GeneratedCSharpCompiler.CreateScriptInstance(generated.Source);

        Assert.True(execution.Success, execution.Build.DiagnosticsText + Environment.NewLine + execution.Exception?.Message);
        var script = Assert.IsType<GeneratedCSharpScriptInstance>(execution.Instance);
        Assert.Equal("outer:middle:inner", script.InvokeRuntime("runScope"));
    }

    [Fact]
    public void GeneratedCSharpPreservesBlockScopeVarHoistingAndClosures()
    {
        var generated = OptimizedJavaScriptCSharpGenerator.Generate(
            """
            function runScopeRules() {
                let blockResult = "";

                if (true) {
                    var functionScoped = "var";
                    let blockLet = "let";
                    const blockConst = "const";
                    blockResult = blockLet + ":" + blockConst;
                }

                function hoisted() {
                    const before = x === undefined ? "undefined" : "bad";
                    var x = 7;
                    return before + ":" + x;
                }

                return functionScoped + ":" + blockResult + ":" + hoisted();
            }
            """);

        var execution = GeneratedCSharpCompiler.CreateScriptInstance(generated.Source);

        Assert.True(execution.Success, execution.Build.DiagnosticsText + Environment.NewLine + execution.Exception?.Message);
        var script = Assert.IsType<GeneratedCSharpScriptInstance>(execution.Instance);
        Assert.Equal("var:let:const:undefined:7", script.InvokeRuntime("runScopeRules"));
    }

    [Fact]
    public void GeneratedCSharpSupportsLanguageOverviewStyleSyntax()
    {
        var generated = OptimizedJavaScriptCSharpGenerator.Generate(
            """
            function overview() {
                const numbers = [1, 2, 3];
                const doubled = numbers.map(n => n * 2);
                const person = { name: "Miriam", count: doubled[2] };

                class Greeter {
                    constructor(prefix) {
                        this.prefix = prefix;
                    }

                    greet(target) {
                        return `${this.prefix}, ${target.name}:${target.count}`;
                    }
                }

                const { name, count } = person;
                return new Greeter("Hello").greet({ name, count });
            }
            """);

        var execution = GeneratedCSharpCompiler.CreateScriptInstance(generated.Source);

        Assert.True(execution.Success, execution.Build.DiagnosticsText + Environment.NewLine + execution.Exception?.Message);
        var script = Assert.IsType<GeneratedCSharpScriptInstance>(execution.Instance);
        Assert.Equal("Hello, Miriam:6", script.InvokeRuntime("overview"));
    }

    [Fact]
    public void ConsoleCaptureIsScopedAndDoesNotRequireRedirectingProcessConsole()
    {
        using var capture = JavaScriptConsole.Capture();
        var runtime = new JavaScriptRuntimeEngine().RegisterStandardLibrary();
        runtime.Execute(
            """
            console.log("captured", 42);
            """);

        Assert.Contains("captured 42", capture.Output.ToString(), StringComparison.Ordinal);
    }

    private static string NormalizeLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}

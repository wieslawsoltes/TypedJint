using Xunit;

namespace TypedJint.Tests;

public sealed class VerifiedCompilerOutputTests
{
    [Fact]
    public void ExecuteVerified_VerifiesDelegateSignatureAndNormalizedIr()
    {
        var engine = new TypedJintEngine();

        var verified = engine.ExecuteVerified(
            """
            /**
             * @param {number} a
             * @param {number} b
             * @returns {number}
             */
            function add(a, b) {
                let c = a + b;
                return c;
            }
            """,
            new Dictionary<string, object?[][]>
            {
                ["add"] = new[]
                {
                    new object?[] { 10.0, 20.0 },
                    new object?[] { -1.0, 1.0 },
                    new object?[] { 2.5, 4.0 }
                }
            });

        Assert.True(verified.Verified, string.Join(Environment.NewLine, verified.CompilerOutputs.Values.Select(x => x.ToMarkdown())));
        Assert.True(verified.CompilerOutputs.ContainsKey("add"));

        var output = verified.CompilerOutputs["add"];
        Assert.True(output.Verified);
        Assert.Equal("add(number a, number b): number", output.SemanticSignature);
        Assert.Contains("Double", output.DelegateSignature, StringComparison.Ordinal);
        Assert.Contains("let c = (a + b)", output.NormalizedIr, StringComparison.Ordinal);
        Assert.Contains("return c", output.NormalizedIr, StringComparison.Ordinal);

        Assert.True(verified.RuntimeOutputs["add"].Verified);
    }

    [Fact]
    public void ExecuteVerified_LeavesUnannotatedFunctionAsFallbackOnly()
    {
        var engine = new TypedJintEngine();

        var verified = engine.ExecuteVerified(
            """
            function add(a, b) {
                return a + b;
            }
            """);

        Assert.True(verified.Compilation.Fallbacks.ContainsKey("add"));
        Assert.False(verified.CompilerOutputs.ContainsKey("add"));
        Assert.Equal(30.0, Convert.ToDouble(engine.Invoke("add", 10, 20)));
    }

    [Fact]
    public void ExecuteVerified_VerifiesDomInteropDelegateOutput()
    {
        var engine = new TypedJintEngine();
        var button = engine.Document.createElement("button");

        var verified = engine.ExecuteVerified(
            """
            /**
             * @param {DomElement} button
             * @returns {void}
             */
            function setup(button) {
                button.textContent = "Ready";
                button.classList.add("primary");
                button.style.backgroundColor = "red";
            }
            """);

        Assert.True(verified.Verified, string.Join(Environment.NewLine, verified.CompilerOutputs.Values.Select(x => x.ToMarkdown())));
        Assert.True(verified.CompilerOutputs["setup"].Verified);
        Assert.Equal("setup(DomElement button): void", verified.CompilerOutputs["setup"].SemanticSignature);
        Assert.Contains("button.textContent = \"Ready\"", verified.CompilerOutputs["setup"].NormalizedIr, StringComparison.Ordinal);

        engine.Invoke("setup", button);

        Assert.Equal("Ready", button.textContent);
        Assert.True(button.classList.contains("primary"));
        Assert.Equal("red", button.style.backgroundColor);
    }

    [Fact]
    public void VerifyAgainstJint_ReportsRuntimeMismatch()
    {
        var engine = new TypedJintEngine();

        engine.Execute(
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

        var verified = engine.VerifyAgainstJint(
            "add",
            new[]
            {
                new object?[] { 1.0, 2.0 },
                new object?[] { 100.0, -25.0 }
            });

        Assert.True(verified.Verified, verified.ToMarkdown());
        Assert.All(verified.Observations, observation => Assert.True(observation.Equivalent, observation.Message));
    }
}

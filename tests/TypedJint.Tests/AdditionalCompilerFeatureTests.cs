using Xunit;

namespace TypedJint.Tests;

public sealed class AdditionalCompilerFeatureTests
{
    [Fact]
    public void CompilesArrayLiteralAndIndexAccess()
    {
        var engine = new TypedJintEngine();

        var verified = engine.ExecuteVerified(
            """
            /**
             * @param {number} index
             * @returns {number}
             */
            function pick(index) {
                let values = [10, 20, 30, 40];
                return values[index];
            }
            """,
            new Dictionary<string, object?[][]>
            {
                ["pick"] = new[]
                {
                    new object?[] { 0.0 },
                    new object?[] { 2.0 },
                    new object?[] { 3.0 }
                }
            });

        Assert.True(verified.Verified, verified.CompilerOutputs["pick"].ToMarkdown());
        Assert.Equal(30.0, engine.Invoke("pick", 2.0));
    }

    [Fact]
    public void CompilesConditionalExpression()
    {
        var engine = new TypedJintEngine();

        var verified = engine.ExecuteVerified(
            """
            /**
             * @param {number} a
             * @param {number} b
             * @returns {number}
             */
            function max(a, b) {
                return a > b ? a : b;
            }
            """,
            new Dictionary<string, object?[][]>
            {
                ["max"] = new[]
                {
                    new object?[] { 1.0, 2.0 },
                    new object?[] { 10.0, -1.0 }
                }
            });

        Assert.True(verified.Verified, verified.CompilerOutputs["max"].ToMarkdown());
        Assert.Equal(10.0, engine.Invoke("max", 10.0, -1.0));
    }

    [Fact]
    public void CompilesCompoundAssignment()
    {
        var engine = new TypedJintEngine();

        var verified = engine.ExecuteVerified(
            """
            /**
             * @param {number} value
             * @returns {number}
             */
            function compound(value) {
                let acc = value;
                acc += 10;
                acc *= 2;
                acc %= 7;
                return acc;
            }
            """,
            new Dictionary<string, object?[][]>
            {
                ["compound"] = new[]
                {
                    new object?[] { 1.0 },
                    new object?[] { 5.0 }
                }
            });

        Assert.True(verified.Verified, verified.CompilerOutputs["compound"].ToMarkdown());
        Assert.Equal(2.0, engine.Invoke("compound", 5.0));
    }

    [Fact]
    public void CompilesLoopTransferStatements()
    {
        var engine = new TypedJintEngine();

        var verified = engine.ExecuteVerified(
            """
            /**
             * @param {number} limit
             * @returns {number}
             */
            function loopTransfer(limit) {
                let acc = 0;
                for (let i = 0; i < limit; i++) {
                    if (i === 2) {
                        continue;
                    }

                    if (i === 5) {
                        break;
                    }

                    acc += i;
                }

                return acc;
            }
            """,
            new Dictionary<string, object?[][]>
            {
                ["loopTransfer"] = new[]
                {
                    new object?[] { 10.0 },
                    new object?[] { 4.0 }
                }
            });

        Assert.True(verified.Verified, verified.CompilerOutputs["loopTransfer"].ToMarkdown());
        Assert.Equal(8.0, engine.Invoke("loopTransfer", 10.0));
    }
}

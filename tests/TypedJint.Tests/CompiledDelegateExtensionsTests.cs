using Xunit;

namespace TypedJint.Tests;

public sealed class CompiledDelegateExtensionsTests
{
    [Fact]
    public void GetDelegate_ReturnsStronglyTypedCompiledDelegate()
    {
        var engine = new TypedJintEngine();

        var result = engine.Execute("""
        /**
         * @param {number} a
         * @param {number} b
         * @returns {number}
         */
        function add(a, b) {
            return a + b;
        }
        """);

        var add = result.GetDelegate<Func<double, double, double>>("add");

        Assert.Equal(42.0, add(10.0, 32.0));
    }

    [Fact]
    public void TryGetDelegate_ReturnsFalseForMismatchedDelegateType()
    {
        var engine = new TypedJintEngine();

        var result = engine.Execute("""
        /**
         * @param {number} a
         * @param {number} b
         * @returns {number}
         */
        function add(a, b) {
            return a + b;
        }
        """);

        var ok = result.TryGetDelegate<Func<double, double>>("add", out var add);

        Assert.False(ok);
        Assert.Null(add);
    }

    [Fact]
    public void DirectDelegateMatchesVerifiedRuntimeResult()
    {
        var engine = new TypedJintEngine();

        var result = engine.Execute("""
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
        """);

        var sumEven = result.GetDelegate<Func<double, double>>("sumEven");

        Assert.Equal(30.0, sumEven(10.0));
        Assert.Equal(Convert.ToDouble(engine.Invoke("sumEven", 10.0)), sumEven(10.0));
    }
}

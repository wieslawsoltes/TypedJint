using BenchmarkDotNet.Attributes;

namespace TypedJint.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class CompilerFeatureBenchmarks
{
    private const string Source = """
    /**
     * @param {number} value
     * @returns {number}
     */
    function featureMix(value) {
        let values = [1, 2, 3, 4, 5];
        let acc = value > 0 ? value : -value;
        for (let i = 0; i < 5; i++) {
            if (i === 2) {
                continue;
            }

            acc += values[i];
        }

        return acc;
    }
    """;

    private Func<double, double> _featureMix = null!;

    [GlobalSetup]
    public void Setup()
    {
        var engine = new TypedJintEngine();
        var result = engine.Execute(Source);
        _featureMix = result.GetDelegate<Func<double, double>>("featureMix");
    }

    [Benchmark]
    public double CompiledFeatureMix()
    {
        return _featureMix(-10.0);
    }
}

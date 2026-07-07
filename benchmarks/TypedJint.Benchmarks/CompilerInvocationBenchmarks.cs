using BenchmarkDotNet.Attributes;
using Jint;

namespace TypedJint.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class CompilerInvocationBenchmarks
{
    private const string Source = """
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
    """;

    private TypedJintEngine _typedEngine = null!;
    private Engine _jint = null!;
    private Func<double, double> _directDelegate = null!;
    private ICompiledFunction _compiledFunction = null!;

    [GlobalSetup]
    public void Setup()
    {
        _typedEngine = new TypedJintEngine();
        var result = _typedEngine.Execute(Source);
        _directDelegate = result.GetDelegate<Func<double, double>>("sumEven");
        _compiledFunction = result.CompiledFunctions["sumEven"];

        _jint = new Engine();
        _jint.Execute(Source);
    }

    [Benchmark(Baseline = true)]
    public object? TypedEngineInvokeDynamicInvoke()
    {
        return _typedEngine.Invoke("sumEven", 100.0);
    }

    [Benchmark]
    public object? CompiledFunctionDynamicInvoke()
    {
        return _compiledFunction.Invoke(100.0);
    }

    [Benchmark]
    public double DirectTypedDelegate()
    {
        return _directDelegate(100.0);
    }

    [Benchmark]
    public object? JintInvoke()
    {
        return _jint.Invoke("sumEven", 100.0).ToObject();
    }
}

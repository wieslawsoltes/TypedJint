using BenchmarkDotNet.Running;
using TypedJint.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(CompilerInvocationBenchmarks).Assembly).Run(args);

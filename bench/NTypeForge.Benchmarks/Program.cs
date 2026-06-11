using BenchmarkDotNet.Running;
using NTypeForge.Benchmarks;

// Switcher (not a hardcoded Run<T>) so individual suites can be selected with --filter.
BenchmarkSwitcher.FromAssembly(typeof(CompileBenchmarks).Assembly).Run(args);

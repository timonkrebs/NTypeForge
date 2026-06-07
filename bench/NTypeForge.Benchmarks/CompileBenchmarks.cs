using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NTypeForge.SourceGenerator;

namespace NTypeForge.Benchmarks;

// Measures the compile-time cost of NTypeForge by compiling one fixed program three ways. The
// source is *identical* across the "off" and "on" cases - it uses `Duck<T>()`, which binds to the
// NTypeForge runtime fallback when the generator is absent and to a generated proxy when it is
// present - so the only variable is whether the generator participates in the compilation.
//
// Every benchmarked operation builds a fresh Compilation (and fresh GeneratorDriver) from re-parsed
// source. Roslyn memoizes binding on a Compilation instance, so reusing one would measure a warm
// re-emit; a clean build is cold, and cold-vs-cold is the only fair comparison.
[ShortRunJob]
[MemoryDiagnoser]
public class CompileBenchmarks
{
    // Number of distinct Duck<T> sites (each contributes one interface, one class, one proxy, and
    // one generated Duck<T> extension).
    [Params(10, 50, 100)]
    public int Sites;

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    private static readonly CSharpCompilationOptions CompilationOptions = new(OutputKind.DynamicallyLinkedLibrary);

    private string _source = null!;
    private MetadataReference[] _references = null!;

    [GlobalSetup]
    public void Setup()
    {
        _source = BuildSource(Sites);
        _references = BuildReferences();

        // Honesty guard: both paths must be a clean, successful compile so we time real builds, not
        // an error path. Throws (failing the benchmark) if either produces a compile error.
        EnsureNoErrors(NewCompilation(), "generator OFF");
        NewDriver().RunGeneratorsAndUpdateCompilation(NewCompilation(), out var augmented, out _);
        EnsureNoErrors(augmented, "generator ON");
    }

    [Benchmark(Baseline = true, Description = "Compile only (NTypeForge OFF)")]
    public bool CompileOnly_GeneratorOff()
        => NewCompilation().Emit(Stream.Null).Success;

    [Benchmark(Description = "Generator pass only (no emit)")]
    public object GeneratorPassOnly()
        => NewDriver().RunGenerators(NewCompilation());

    [Benchmark(Description = "Full compile (NTypeForge ON)")]
    public bool CompileFull_GeneratorOn()
    {
        NewDriver().RunGeneratorsAndUpdateCompilation(NewCompilation(), out var augmented, out _);
        return augmented.Emit(Stream.Null).Success;
    }

    private CSharpCompilation NewCompilation()
        => CSharpCompilation.Create(
            assemblyName: "NTypeForge.Bench.Generated",
            syntaxTrees: new[] { CSharpSyntaxTree.ParseText(_source, ParseOptions) },
            references: _references,
            options: CompilationOptions);

    private GeneratorDriver NewDriver()
        => CSharpGeneratorDriver.Create(
            generators: new[] { new DuckTypingGenerator().AsSourceGenerator() },
            additionalTexts: null,
            parseOptions: ParseOptions,
            optionsProvider: null);

    private static void EnsureNoErrors(Compilation compilation, string label)
    {
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Take(5)
            .ToImmutableArray();
        if (errors.Length > 0)
        {
            throw new InvalidOperationException(
                $"Benchmark setup ({label}) produced compile errors:{Environment.NewLine}" +
                string.Join(Environment.NewLine, errors));
        }
    }

    // N self-contained Duck<T> sites. Each interface carries a method and a property so the
    // generated proxy exercises more than the trivial single-method case.
    private static string BuildSource(int n)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using NTypeForge;");
        sb.AppendLine("namespace Bench {");
        for (int i = 0; i < n; i++)
        {
            sb.AppendLine($"  public interface ICalc{i} {{ float Calculate(float a, float b); int Value {{ get; }} }}");
            sb.AppendLine($"  public class Calc{i} {{ public float Calculate(float a, float b) => a + b; public int Value => {i}; }}");
        }
        sb.AppendLine("  public static class Entry {");
        sb.AppendLine("    public static int Run() {");
        sb.AppendLine("      int acc = 0;");
        for (int i = 0; i < n; i++)
        {
            sb.AppendLine($"      acc += new Calc{i}().Duck<ICalc{i}>().Value;");
        }
        sb.AppendLine("      return acc;");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static MetadataReference[] BuildReferences()
        => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(NTypeForge.IProxy).Assembly.Location))
            .ToArray();
}

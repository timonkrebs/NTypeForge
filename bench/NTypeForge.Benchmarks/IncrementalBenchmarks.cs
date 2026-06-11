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

// Measures the generator on an IDE-shaped workload, complementing CompileBenchmarks (which is
// cold and 100% duck-dense). The corpus is mostly ordinary code - thousands of invocations that
// are not duck sites - plus a band of small duck files, so the numbers expose what the generator
// costs while someone types in a realistic project:
//
//   ColdGeneratorPass      - one full generator pass over the corpus. Transform-stage cost; this
//                            is where the syntactic predicate's selectivity shows, since most
//                            corpus invocations must be rejected before any semantic bind.
//   WarmEditUnrelatedFile  - warm re-run after an edit to a file with no duck sites: the
//                            steady-state keystroke cost.
//   WarmEditMovesDuckSites - warm re-run after an edit that only shifts a duck site down one
//                            line. The models change location but not content, so the codegen
//                            branch must serve every generated file from cache; only the
//                            (cheap) transform of the edited file should be paid.
//
// Warm benchmarks reuse one warmed driver against a pre-built edited compilation. RunGenerators
// returns a fresh driver and leaves the warmed one untouched, so every invocation measures the
// same single-edit transition. The edited compilations are derived from the warmed compilation
// via ReplaceSyntaxTree, preserving the tree identity of every unedited file - exactly how an
// IDE evolves a compilation between keystrokes.
[ShortRunJob]
[MemoryDiagnoser]
public class IncrementalBenchmarks
{
    // ~2400 ordinary invocations across 40 files (half syntactically rejectable: zero-argument
    // or identifier calls), plus 20 single-site duck files.
    private const int OrdinaryFiles = 40;
    private const int BlocksPerFile = 10;
    private const int DuckFiles = 20;

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    private static readonly CSharpCompilationOptions CompilationOptions = new(OutputKind.DynamicallyLinkedLibrary);

    private MetadataReference[] _references = null!;
    private SyntaxTree[] _baseTrees = null!;
    private CSharpCompilation _baseCompilation = null!;
    private CSharpCompilation _editedUnrelated = null!;
    private CSharpCompilation _editedDuckShift = null!;
    private GeneratorDriver _warmDriver = null!;

    [GlobalSetup]
    public void Setup()
    {
        _references = BuildReferences();

        _baseTrees = BuildCorpusTrees();
        _baseCompilation = NewCompilation(_baseTrees);

        // Append a method to ordinary file 0: new code, no duck sites anywhere near it.
        var editedOrdinary = Parse(OrdinaryFileSource(0, extraMethod: true));
        _editedUnrelated = _baseCompilation.ReplaceSyntaxTree(_baseTrees[0], editedOrdinary);

        // Prepend a comment to duck file 0: every span in the file shifts down one line, but no
        // content the generated code depends on changes.
        var shiftedDuck = Parse("// shifts every span in this file down one line\n" + DuckFileSource(0));
        _editedDuckShift = _baseCompilation.ReplaceSyntaxTree(_baseTrees[OrdinaryFiles], shiftedDuck);

        _warmDriver = NewDriver().RunGenerators(_baseCompilation);

        // Honesty guard, as in CompileBenchmarks: the corpus must be a clean generator-on
        // compile, so the benchmarks time real work rather than an error path.
        NewDriver().RunGeneratorsAndUpdateCompilation(NewCompilation(_baseTrees), out var augmented, out _);
        EnsureNoErrors(augmented);
    }

    [Benchmark(Baseline = true, Description = "Cold generator pass (corpus)")]
    public object ColdGeneratorPass()
        => NewDriver().RunGenerators(NewCompilation(_baseTrees));

    [Benchmark(Description = "Warm re-run: edit non-duck file")]
    public object WarmEditUnrelatedFile()
        => _warmDriver.RunGenerators(_editedUnrelated);

    [Benchmark(Description = "Warm re-run: edit shifts duck site")]
    public object WarmEditMovesDuckSites()
        => _warmDriver.RunGenerators(_editedDuckShift);

    private SyntaxTree[] BuildCorpusTrees()
    {
        var trees = new SyntaxTree[OrdinaryFiles + DuckFiles + 1];
        for (int i = 0; i < OrdinaryFiles; i++)
        {
            trees[i] = Parse(OrdinaryFileSource(i, extraMethod: false));
        }
        for (int i = 0; i < DuckFiles; i++)
        {
            trees[OrdinaryFiles + i] = Parse(DuckFileSource(i));
        }
        trees[OrdinaryFiles + DuckFiles] = Parse(SharedBaseSource());
        return trees;
    }

    // A framework-sized base (think Control or DbContext): every ducked concrete inherits its
    // public surface, so each structural scan must walk and key all of it. This is the realistic
    // shape that makes the surface-scan cost visible; trivial two-member concretes would hide it.
    private static string SharedBaseSource()
    {
        var types = new[] { "int", "string", "double", "long", "bool", "DateTime", "List<int>" };
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("namespace Bench.Shared");
        sb.AppendLine("{");
        sb.AppendLine("    public class FatBase");
        sb.AppendLine("    {");
        for (int k = 0; k < 100; k++)
        {
            var ret = types[k % types.Length];
            var p1 = types[(k + 1) % types.Length];
            var p2 = types[(k + 3) % types.Length];
            sb.AppendLine($"        public {ret} M{k}({p1} a, {p2} b) => default!;");
        }
        for (int k = 0; k < 20; k++)
        {
            sb.AppendLine($"        public {types[k % types.Length]} P{k} {{ get; set; }} = default!;");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // One file of ordinary, non-duck code. Each block mixes the invocation shapes the predicate
    // must triage: member calls with arguments (reach the semantic bind), zero-argument member
    // calls, and identifier calls (both syntactically rejectable).
    private static string OrdinaryFileSource(int index, bool extraMethod)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Text;");
        sb.AppendLine($"namespace Bench.Ordinary{index}");
        sb.AppendLine("{");
        sb.AppendLine("    public class Worker");
        sb.AppendLine("    {");
        sb.AppendLine("        private readonly StringBuilder _sb = new StringBuilder();");
        sb.AppendLine("        private readonly List<int> _items = new List<int>();");
        sb.AppendLine();
        sb.AppendLine("        public int Run(int seed)");
        sb.AppendLine("        {");
        sb.AppendLine("            var acc = seed;");
        for (int j = 0; j < BlocksPerFile; j++)
        {
            sb.AppendLine($"            _items.Add({j});");
            sb.AppendLine($"            _sb.Append({j});");
            sb.AppendLine($"            acc += Step({j});");
            sb.AppendLine("            acc += _sb.ToString().Length;");
            sb.AppendLine("            _items.Clear();");
            sb.AppendLine($"            acc += Math.Max(acc, {j});");
        }
        sb.AppendLine("            return acc;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        private static int Step(int v) => v + 1;");
        if (extraMethod)
        {
            sb.AppendLine();
            sb.AppendLine("        public int Extra() => Step(41);");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // One self-contained duck site per file (namespace-distinct, so each contributes its own
    // interface, concrete, proxy, and extension class). The concrete inherits FatBase, so its
    // structural scan covers a realistic, framework-sized public surface.
    private static string DuckFileSource(int index)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using NTypeForge;");
        sb.AppendLine($"namespace Bench.Ducks{index}");
        sb.AppendLine("{");
        sb.AppendLine("    public interface ICalc { float Calculate(float a, float b); int Value { get; } }");
        sb.AppendLine($"    public class Calc : Bench.Shared.FatBase {{ public float Calculate(float a, float b) => a + b; public int Value => {index}; }}");
        sb.AppendLine("    public static class Use");
        sb.AppendLine("    {");
        sb.AppendLine("        public static int Run() => new Calc().Duck<ICalc>().Value;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static SyntaxTree Parse(string source) => CSharpSyntaxTree.ParseText(source, ParseOptions);

    private CSharpCompilation NewCompilation(SyntaxTree[] trees)
        => CSharpCompilation.Create(
            assemblyName: "NTypeForge.Bench.Corpus",
            syntaxTrees: trees,
            references: _references,
            options: CompilationOptions);

    private static GeneratorDriver NewDriver()
        => CSharpGeneratorDriver.Create(
            generators: new[] { new DuckTypingGenerator().AsSourceGenerator() },
            additionalTexts: null,
            parseOptions: ParseOptions,
            optionsProvider: null);

    private static void EnsureNoErrors(Compilation compilation)
    {
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Take(5)
            .ToImmutableArray();
        if (errors.Length > 0)
        {
            throw new InvalidOperationException(
                $"Benchmark setup produced compile errors:{Environment.NewLine}" +
                string.Join(Environment.NewLine, errors));
        }
    }

    private static MetadataReference[] BuildReferences()
        => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(NTypeForge.IProxy).Assembly.Location))
            .ToArray();
}

using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NTypeForge.SourceGenerator;

namespace NTypeForge.Generator.Tests;

internal static class GeneratorTestHarness
{
    // The generator emits C# `extension` blocks, so both the snippet and the generated trees must
    // be parsed at preview language level (matching the real test projects).
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    // Runs DuckTypingGenerator over a source snippet and returns the diagnostics it
    // reported. We only assert generator diagnostics (NTF00x), so the snippet only
    // needs to *bind* (Duck<T> resolves to the library fallback) — the generated
    // output itself is not compiled here.
    public static ImmutableArray<Diagnostic> GetGeneratorDiagnostics(string source)
    {
        var driver = CreateDriver().RunGenerators(CreateCompilation(source));
        return driver.GetRunResult().Diagnostics;
    }

    // Runs the generator AND compiles the resulting compilation (snippet + generated trees),
    // returning every compile error. Diagnostic-only assertions can't see invalid generated C#;
    // this is what catches emitted code that doesn't parse or bind (bad identifiers, CS0535, etc.).
    public static ImmutableArray<Diagnostic> GetEmittedCompileErrors(string source)
    {
        CreateDriver().RunGeneratorsAndUpdateCompilation(CreateCompilation(source), out var output, out _);

        return output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
    }

    // Concatenated text of every generated source file, ordered by hint name. Used to assert the
    // generator is deterministic (same input -> byte-identical output across runs).
    public static string GetGeneratedText(string source)
    {
        var driver = CreateDriver().RunGenerators(CreateCompilation(source));

        var results = driver.GetRunResult().Results.SelectMany(r => r.GeneratedSources)
            .OrderBy(s => s.HintName, StringComparer.Ordinal)
            .Select(s => $"// {s.HintName}\n{s.SourceText}");
        return string.Join("\n", results);
    }

    private static GeneratorDriver CreateDriver()
        => CSharpGeneratorDriver.Create(
            generators: new[] { new DuckTypingGenerator().AsSourceGenerator() },
            additionalTexts: null,
            parseOptions: ParseOptions,
            optionsProvider: null);

    // Compiles `source`, exposed so caching tests can build an evolved second compilation.
    public static CSharpCompilation Compile(string source) => CreateCompilation(source);

    // A driver that records per-step run reasons (Cached/Modified/...), so a test can assert the
    // source-output stage is reused when the candidate set is unchanged.
    public static GeneratorDriver CreateStepTrackingDriver()
        => CSharpGeneratorDriver.Create(
            generators: new[] { new DuckTypingGenerator().AsSourceGenerator() },
            additionalTexts: null,
            parseOptions: ParseOptions,
            optionsProvider: null,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

    // The run reasons of every source-output step on the most recent run of `driver`.
    public static IEnumerable<IncrementalStepRunReason> OutputStepReasons(GeneratorDriver driver)
        => driver.GetRunResult().Results
            .SelectMany(r => r.TrackedOutputSteps)
            .SelectMany(kvp => kvp.Value)
            .SelectMany(step => step.Outputs)
            .Select(o => o.Reason);

    private static CSharpCompilation CreateCompilation(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, ParseOptions);

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(NTypeForge.IProxy).Assembly.Location));

        return CSharpCompilation.Create(
            assemblyName: "NTypeForge.GeneratorTests.Snippet",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    public static bool HasDiagnostic(this ImmutableArray<Diagnostic> diagnostics, string id)
        => diagnostics.Any(d => d.Id == id);

    public static int CountDiagnostics(this ImmutableArray<Diagnostic> diagnostics, string id)
        => diagnostics.Count(d => d.Id == id);
}

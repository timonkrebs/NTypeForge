using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NTypeForge.Variadic.SourceGenerator;

namespace NTypeForge.Variadic.Tests;

// Runs VariadicGenerator over a source snippet in-memory. The snippet only needs to bind the
// (generator-provided) [assembly: VariadicArity] attribute; the generated output is compiled
// separately by GetEmittedCompileErrors to prove it is valid C#.
internal static class VariadicGeneratorHarness
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    public static ImmutableArray<Diagnostic> GetGeneratorDiagnostics(string source)
        => CreateDriver().RunGenerators(CreateCompilation(source)).GetRunResult().Diagnostics;

    // Runs the generator AND compiles snippet + generated trees, returning every compile error.
    // This is what catches emitted code that doesn't parse or bind (a bad arity, a stray comma).
    public static ImmutableArray<Diagnostic> GetEmittedCompileErrors(string source)
    {
        CreateDriver().RunGeneratorsAndUpdateCompilation(CreateCompilation(source), out var output, out _);
        return output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
    }

    // Concatenated text of every generated source (attribute + variadic family), for substring and
    // determinism assertions.
    public static string GetGeneratedText(string source)
    {
        var sources = CreateDriver().RunGenerators(CreateCompilation(source)).GetRunResult()
            .Results.SelectMany(r => r.GeneratedSources)
            .OrderBy(s => s.HintName, StringComparer.Ordinal)
            .Select(s => s.SourceText.ToString());
        return string.Join("\n", sources);
    }

    private static GeneratorDriver CreateDriver()
        => CSharpGeneratorDriver.Create(
            generators: new[] { new VariadicGenerator().AsSourceGenerator() },
            additionalTexts: null,
            parseOptions: ParseOptions,
            optionsProvider: null);

    private static CSharpCompilation CreateCompilation(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, ParseOptions);

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));

        return CSharpCompilation.Create(
            assemblyName: "NTypeForge.Variadic.Snippet",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    public static bool HasDiagnostic(this ImmutableArray<Diagnostic> diagnostics, string id)
        => diagnostics.Any(d => d.Id == id);
}

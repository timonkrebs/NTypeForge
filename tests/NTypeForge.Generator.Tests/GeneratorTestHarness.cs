using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NTypeForge.SourceGenerator;

namespace NTypeForge.Generator.Tests;

internal static class GeneratorTestHarness
{
    // Runs DuckTypingGenerator over a source snippet and returns the diagnostics it
    // reported. We only assert generator diagnostics (NTF00x), so the snippet only
    // needs to *bind* (Duck<T> resolves to the library fallback) — the generated
    // output itself is not compiled here.
    public static ImmutableArray<Diagnostic> GetGeneratorDiagnostics(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(NTypeForge.IProxy).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            assemblyName: "NTypeForge.GeneratorTests.Snippet",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new DuckTypingGenerator());
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult().Diagnostics;
    }

    public static bool HasDiagnostic(this ImmutableArray<Diagnostic> diagnostics, string id)
        => diagnostics.Any(d => d.Id == id);

    public static int CountDiagnostics(this ImmutableArray<Diagnostic> diagnostics, string id)
        => diagnostics.Count(d => d.Id == id);
}

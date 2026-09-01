using Microsoft.CodeAnalysis;

namespace NTypeForge.Variadic.Tests;

// Drive the generator directly over snippets to prove the arity scaling, the configuration
// attribute, the diagnostics, and that every emitted family compiles. These are independent of the
// test assembly's own [assembly: VariadicArity(12)] - each snippet sets (or omits) its own.
public class VariadicGenerationTests
{
    [Fact]
    public void MarkerAttributeIsEmitted()
    {
        var text = VariadicGeneratorHarness.GetGeneratedText("public class C {}");

        Assert.Contains("class VariadicArityAttribute", text);
    }

    // No [assembly: VariadicArity] -> the silent default of 8, and no diagnostics.
    [Fact]
    public void Default_GeneratesUpToArityEight()
    {
        const string source = "public class C {}";

        var text = VariadicGeneratorHarness.GetGeneratedText(source);

        Assert.Contains("in T8, out TResult>", text);        // an 8-argument VariadicFunc exists
        Assert.DoesNotContain("in T9", text);                // and nothing beyond 8
        Assert.False(VariadicGeneratorHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTFV001"));
        Assert.False(VariadicGeneratorHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTFV002"));
        Assert.Empty(VariadicGeneratorHarness.GetEmittedCompileErrors(source));
    }

    [Fact]
    public void Attribute_ControlsMaxArity()
    {
        const string source = "[assembly: NTypeForge.Variadic.VariadicArity(5)]";

        var text = VariadicGeneratorHarness.GetGeneratedText(source);

        Assert.Contains("in T5, out TResult>", text);
        Assert.DoesNotContain("in T6", text);
        Assert.Empty(VariadicGeneratorHarness.GetGeneratorDiagnostics(source));
        Assert.Empty(VariadicGeneratorHarness.GetEmittedCompileErrors(source));
    }

    // The headline claim: an arity well past System.Func's 16-argument ceiling generates AND
    // compiles - a 20-argument delegate and a 20-sequence Zip that the BCL cannot express.
    [Fact]
    public void ArityBeyondFuncCeiling_GeneratesAndCompiles()
    {
        const string source = "[assembly: NTypeForge.Variadic.VariadicArity(20)]";

        var text = VariadicGeneratorHarness.GetGeneratedText(source);

        Assert.Contains("in T20, out TResult>", text);
        Assert.Contains("Zip<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15, T16, T17, T18, T19, T20, TResult>", text);
        Assert.Empty(VariadicGeneratorHarness.GetEmittedCompileErrors(source));
    }

    // Zip is generated from arity 3 up (the BCL owns 2-sequence Zip); ForEach from arity 2 up.
    [Fact]
    public void Zip_StartsAtThree_ForEach_StartsAtTwo()
    {
        const string source = "[assembly: NTypeForge.Variadic.VariadicArity(4)]";

        var text = VariadicGeneratorHarness.GetGeneratedText(source);

        Assert.Contains("Zip<T1, T2, T3, TResult>", text);   // 3-sequence Zip present
        Assert.DoesNotContain("Zip<T1, T2, TResult>", text); // 2-sequence Zip deliberately absent
        Assert.Contains("ForEach<T1, T2>", text);            // 2-sequence ForEach present
    }

    [Fact]
    public void ArityBelowMinimum_ReportsErrorAndClampsToValidOutput()
    {
        const string source = "[assembly: NTypeForge.Variadic.VariadicArity(0)]";

        Assert.True(VariadicGeneratorHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTFV001"));
        // Clamped up to 1: a lone 1-argument delegate family, still valid C#.
        Assert.Empty(VariadicGeneratorHarness.GetEmittedCompileErrors(source));
    }

    [Fact]
    public void ArityAboveMaximum_WarnsAndClampsToSixtyFour()
    {
        const string source = "[assembly: NTypeForge.Variadic.VariadicArity(100)]";

        var text = VariadicGeneratorHarness.GetGeneratedText(source);

        Assert.True(VariadicGeneratorHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTFV002"));
        Assert.Contains("in T64, out TResult>", text);
        Assert.DoesNotContain("in T65", text);
        Assert.Empty(VariadicGeneratorHarness.GetEmittedCompileErrors(source));
    }

    [Fact]
    public void Output_IsDeterministic()
    {
        const string source = "[assembly: NTypeForge.Variadic.VariadicArity(6)]";

        Assert.Equal(
            VariadicGeneratorHarness.GetGeneratedText(source),
            VariadicGeneratorHarness.GetGeneratedText(source));
    }
}

using Xunit;

namespace NTypeForge.Generator.Tests;

// "Silent" duck-failures: cases where NTypeForge deliberately neither rewires a failed call nor
// raises an NTF diagnostic, so the user is left with the compiler's own overload-resolution error
// (the same as if NTypeForge were not referenced). These characterize that contract from the
// user's side - the generator stays quiet (no NTF00x) AND the original call still fails to compile.
// If a future change adds an NTF hint for any of these, update the matching test.
//
// The ducked-argument-by-ref/out/in cases are the gap these fill. Ambiguity and partial (multi-arg)
// matches are already covered from the "no proxy is emitted" angle in DiagnosticTests
// (AmbiguousMethodArgumentDuck_IsNotRewired, TwoDuckableArguments_OneWithoutMatch_IsNotRewired); the
// last two tests here add the complementary "the compiler error still stands" assertion.
public class SilentDuckFailureTests
{
    // Ducking substitutes a freshly constructed proxy for the argument expression, which is valid
    // only for a by-value parameter: a ref/out/in parameter needs a real variable of the exact type
    // (CandidateAnalyzer.IsDuckableArgument rejects RefKind != None). So the argument is not ducked,
    // no proxy is emitted, no NTF is raised, and the compiler's ref-kind error stands.

    [Fact]
    public void RefDuckedArgument_StaysSilent_AndLeavesCompilerError()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class Mgr
                {
                    public int H(ref ICalc c) => c.Add(1, 2);
                    public void M() { var m = new Mgr(); var a = new Adder(); m.H(ref a); }
                }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetGeneratorDiagnostics(source));
        Assert.DoesNotContain("_Proxy_", GeneratorTestHarness.GetGeneratedText(source));
        Assert.NotEmpty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    [Fact]
    public void OutDuckedArgument_StaysSilent_AndLeavesCompilerError()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class Mgr
                {
                    public void H(out ICalc c) => c = null;
                    public void M() { var m = new Mgr(); Adder a; m.H(out a); }
                }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetGeneratorDiagnostics(source));
        Assert.DoesNotContain("_Proxy_", GeneratorTestHarness.GetGeneratedText(source));
        Assert.NotEmpty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    [Fact]
    public void InDuckedArgument_StaysSilent_AndLeavesCompilerError()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class Mgr
                {
                    public int H(in ICalc c) => c.Add(1, 2);
                    public void M() { var m = new Mgr(); var a = new Adder(); m.H(in a); }
                }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetGeneratorDiagnostics(source));
        Assert.DoesNotContain("_Proxy_", GeneratorTestHarness.GetGeneratedText(source));
        Assert.NotEmpty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // Ambiguity: the value structurally matches both candidate interfaces, so choosing one overload
    // would be arbitrary - the call is left alone (CandidateAnalyzer keeps a site only when there is
    // exactly one interpretation). No NTF, and the compiler's own error is what the user sees.
    [Fact]
    public void AmbiguousDuck_StaysSilent_AndLeavesCompilerError()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IA { int Do(); }
                public interface IB { int Do(); }
                public class Impl { public int Do() => 1; }
                public class Mgr
                {
                    public void H(IA a) {}
                    public void H(IB b) {}
                    public void M() { var m = new Mgr(); m.H(new Impl()); }
                }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetGeneratorDiagnostics(source));
        Assert.NotEmpty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // Partial match: one argument of a multi-argument call has no structural match, so the site is
    // not bridged as a whole (ducking only the matching argument could never make the call bind).
    // No NTF, and the compiler's own error stands.
    [Fact]
    public void PartialMultiArgumentMatch_StaysSilent_AndLeavesCompilerError()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public interface ILog { string Log(string m); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class NotALogger { public int Other() => 0; }
                public class Mgr
                {
                    public string H(ICalc c, ILog l) => l.Log(c.Add(1, 2).ToString());
                    public void M() { var m = new Mgr(); m.H(new Adder(), new NotALogger()); }
                }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetGeneratorDiagnostics(source));
        Assert.NotEmpty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }
}

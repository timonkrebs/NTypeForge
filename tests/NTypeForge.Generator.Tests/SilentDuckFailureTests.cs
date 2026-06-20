using Xunit;

namespace NTypeForge.Generator.Tests;

// Cases an *implicit* (non-Duck<T>) call deliberately leaves alone with NO diagnostic at all, so the
// user is left with the compiler's own overload-resolution error. These characterize that silence
// from the user's side - no NTF00x AND the original call still fails to compile.
//
// (The ref/out/in near-miss is NOT silent - it warns NTF004; see DiagnosticTests. Ambiguity and
// partial matches are also covered from the "no proxy emitted" angle in DiagnosticTests
// (AmbiguousMethodArgumentDuck_IsNotRewired, TwoDuckableArguments_OneWithoutMatch_IsNotRewired);
// the tests here add the complementary "the compiler error still stands" assertion.)
public class SilentDuckFailureTests
{
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

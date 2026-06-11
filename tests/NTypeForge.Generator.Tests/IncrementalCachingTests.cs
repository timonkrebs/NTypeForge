using Microsoft.CodeAnalysis;
using Xunit;

namespace NTypeForge.Generator.Tests;

// Proves the equatable-model refactor actually caches: because the transform projects each duck
// site into a value-equatable CandidateModel (no symbols), an edit elsewhere in the compilation
// re-runs the transform but yields an equal model, so the source-output stage is reused. With the
// previous symbol-bearing model these would never compare equal and Execute would always re-run.
public class IncrementalCachingTests
{
    private const string DuckSource = """
        using NTypeForge;
        namespace T
        {
            public interface ICalc { int Add(int a, int b); }
            public class Adder { public int Add(int a, int b) => a + b; }
            public class C { public void M() { var x = new Adder().Duck<ICalc>(); } }
        }
        """;

    [Fact]
    public void UnrelatedEdit_ReusesCachedOutput()
    {
        // An unrelated type appended *after* the duck site: the duck call's span is unchanged, so
        // its projected model is identical and the output must be served from cache.
        const string evolved = DuckSource + "\nnamespace Extra { public class Unrelated { public int Q() => 0; } }\n";

        var driver = GeneratorTestHarness.CreateStepTrackingDriver();
        driver = driver.RunGenerators(GeneratorTestHarness.Compile(DuckSource));
        driver = driver.RunGenerators(GeneratorTestHarness.Compile(evolved));

        var reasons = GeneratorTestHarness.OutputStepReasons(driver).ToList();
        Assert.NotEmpty(reasons);
        Assert.All(reasons, r => Assert.Equal(IncrementalStepRunReason.Cached, r));
    }

    [Fact]
    public void EditThatMovesDuckSite_ReusesCodegenOutput()
    {
        // The same duck site shifted down one line: every content field of its model is
        // unchanged, only the diagnostic span moved. Codegen compares models location-free
        // (CandidateModel.CodegenComparer), so the emitted sources must be served from cache -
        // before the diagnostics/codegen split, the moved span invalidated the collected array
        // and re-emitted every generated file.
        const string moved = "// pushes every span down one line\n" + DuckSource;

        var driver = GeneratorTestHarness.CreateStepTrackingDriver();
        driver = driver.RunGenerators(GeneratorTestHarness.Compile(DuckSource));
        driver = driver.RunGenerators(GeneratorTestHarness.Compile(moved));

        var reasons = GeneratorTestHarness.OutputStepReasons(driver).ToList();
        Assert.NotEmpty(reasons);
        Assert.All(reasons, r => Assert.Equal(IncrementalStepRunReason.Cached, r));
    }

    [Fact]
    public void MovedDiagnosticSite_ReportsFreshLocation()
    {
        // The complement of the location-free codegen comparison: the diagnostics branch keeps
        // full (location-sensitive) equality, so moving a failing duck site must re-report its
        // diagnostic at the new location, not replay the stale cached one.
        const string brokenDuck = """
            using NTypeForge;
            namespace T
            {
                public interface IShout { void Shout(); }
                public class Quiet { }
                public class C { public void M() { var x = new Quiet().Duck<IShout>(); } }
            }
            """;
        const string moved = "// pushes every span down one line\n" + brokenDuck;

        var driver = GeneratorTestHarness.CreateStepTrackingDriver();
        driver = driver.RunGenerators(GeneratorTestHarness.Compile(brokenDuck));
        var before = Assert.Single(driver.GetRunResult().Diagnostics, d => d.Id == "NTF001");

        driver = driver.RunGenerators(GeneratorTestHarness.Compile(moved));
        var after = Assert.Single(driver.GetRunResult().Diagnostics, d => d.Id == "NTF001");

        Assert.Equal(
            before.Location.GetLineSpan().StartLinePosition.Line + 1,
            after.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public void NewDuckSite_RecomputesOutput()
    {
        // Negative control: adding a genuinely new duck site changes the candidate set, so the
        // output stage must NOT be cached - otherwise the "cached" assertion above is vacuous.
        const string withSecondDuck = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public interface ICalc2 { int Add(int a, int b); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class C { public void M() { var x = new Adder().Duck<ICalc>(); } }
                public class D { public void N() { var y = new Adder().Duck<ICalc2>(); } }
            }
            """;

        var driver = GeneratorTestHarness.CreateStepTrackingDriver();
        driver = driver.RunGenerators(GeneratorTestHarness.Compile(DuckSource));
        driver = driver.RunGenerators(GeneratorTestHarness.Compile(withSecondDuck));

        var reasons = GeneratorTestHarness.OutputStepReasons(driver).ToList();
        Assert.Contains(reasons, r => r != IncrementalStepRunReason.Cached);
    }
}

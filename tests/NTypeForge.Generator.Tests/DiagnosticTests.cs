using Xunit;

namespace NTypeForge.Generator.Tests;

public class DiagnosticTests
{
    [Fact]
    public void NTF001_ReportedWhenDuckTargetDoesNotStructurallyMatch()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IFoo { int Bar(); }
                public class NoMatch { public int Other() => 0; }
                public class C
                {
                    public void M() { var x = new NoMatch().Duck<IFoo>(); }
                }
            }
            """;

        var diagnostics = GeneratorTestHarness.GetGeneratorDiagnostics(source);

        Assert.Equal(1, diagnostics.CountDiagnostics("NTF001"));
        Assert.False(diagnostics.HasDiagnostic("NTF002"));
    }

    [Fact]
    public void NTF002_NotReportedWhenInterfaceHasProperty()
    {
        // Properties are now supported, so NTF002 should not be reported.
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IHasProp { int Value { get; } int Do(); }
                public class Impl { public int Value => 1; public int Do() => 2; }
                public class C
                {
                    public void M() { var x = new Impl().Duck<IHasProp>(); }
                }
            }
            """;

        var diagnostics = GeneratorTestHarness.GetGeneratorDiagnostics(source);

        Assert.False(diagnostics.HasDiagnostic("NTF002"));
        Assert.False(diagnostics.HasDiagnostic("NTF001"));
    }

    [Fact]
    public void NTF002_NotReportedWhenInheritedInterfaceHasProperty()
    {
        // Properties are now supported, so NTF002 should not be reported.
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IBaseProp { int P { get; } }
                public interface IDerivedM : IBaseProp { int Do(); }
                public class Impl { public int P => 1; public int Do() => 2; }
                public class C
                {
                    public void M() { var x = new Impl().Duck<IDerivedM>(); }
                }
            }
            """;

        var diagnostics = GeneratorTestHarness.GetGeneratorDiagnostics(source);

        Assert.False(diagnostics.HasDiagnostic("NTF002"));
    }

    [Fact]
    public void NoDiagnostic_ForImplicitMethodArgumentDucking()
    {
        // An implicit conversion failure (no Duck<T>) is silently handled by
        // generating an extension; it must not report NTF00x.
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public class Add { public int Add(int a, int b) => a + b; }
                public class Mgr
                {
                    public int H(ICalc c) => c.Add(1, 2);
                    public void M() { var m = new Mgr(); m.H(new Add()); }
                }
            }
            """;

        var diagnostics = GeneratorTestHarness.GetGeneratorDiagnostics(source);

        Assert.False(diagnostics.HasDiagnostic("NTF001"));
        Assert.False(diagnostics.HasDiagnostic("NTF002"));
    }

    [Fact]
    public void NoDiagnostic_ForValidDuckMatch()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public class Add { public int Add(int a, int b) => a + b; }
                public class C
                {
                    public void M() { var x = new Add().Duck<ICalc>(); }
                }
            }
            """;

        var diagnostics = GeneratorTestHarness.GetGeneratorDiagnostics(source);

        Assert.False(diagnostics.HasDiagnostic("NTF001"));
        Assert.False(diagnostics.HasDiagnostic("NTF002"));
    }
}

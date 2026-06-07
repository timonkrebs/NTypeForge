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
    public void NoDiagnostic_WhenInterfaceHasProperty()
    {
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
    public void NoDiagnostic_WhenInheritedInterfaceHasProperty()
    {
        // Guards the inherited-member fix: the unsupported property lives on the
        // *base* interface, so a scan of direct members only would miss it.
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
        Assert.False(diagnostics.HasDiagnostic("NTF001"));
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

    // An init-only *underlying* setter cannot be forwarded: the proxy wraps an already-constructed
    // instance, so `_instance.Value = value` would be CS8852. The generator must report a clean
    // NTF001 (no structural match) instead of emitting code that fails to compile.
    [Fact]
    public void NTF001_WhenInterfaceNeedsSetterButUnderlyingIsInitOnly()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IWritable { int Value { get; set; } }
                public class InitOnly { public int Value { get; init; } }
                public class C
                {
                    public void M() { var x = new InitOnly().Duck<IWritable>(); }
                }
            }
            """;

        Assert.Equal(1, GeneratorTestHarness.GetGeneratorDiagnostics(source).CountDiagnostics("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // Regression: a concrete with a *private* setter must not be treated as satisfying a `{ get; set; }`
    // requirement. The proxy would emit `_instance.Value = value` against an inaccessible setter
    // (CS0272). Expect a clean NTF001 with no emitted compile errors instead.
    [Fact]
    public void NTF001_WhenConcreteSetterIsPrivate()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IWritable { int Value { get; set; } }
                public class PrivateSetter { public int Value { get; private set; } }
                public class C { public void M() { var x = new PrivateSetter().Duck<IWritable>(); } }
            }
            """;

        Assert.Equal(1, GeneratorTestHarness.GetGeneratorDiagnostics(source).CountDiagnostics("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // Regression: a *private* method must not count toward structural matching - forwarding
    // `_instance.Do()` to an inaccessible method is CS0122. Expect a clean NTF001 instead.
    [Fact]
    public void NTF001_WhenConcreteMethodIsPrivate()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IDo { int Do(); }
                public class PrivateDo { private int Do() => 1; }
                public class C { public void M() { var x = new PrivateDo().Duck<IDo>(); } }
            }
            """;

        Assert.Equal(1, GeneratorTestHarness.GetGeneratorDiagnostics(source).CountDiagnostics("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A property whose name matches but whose type differs is not a structural match.
    [Fact]
    public void NTF001_WhenPropertyTypeDiffers()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IIntValue { int Value { get; } }
                public class HasString { public string Value { get; set; } = ""; }
                public class C { public void M() { var x = new HasString().Duck<IIntValue>(); } }
            }
            """;

        Assert.Equal(1, GeneratorTestHarness.GetGeneratorDiagnostics(source).CountDiagnostics("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A non-generic method cannot satisfy a generic-method requirement (arity differs).
    [Fact]
    public void NTF001_WhenGenericMethodArityDiffers()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IGen { TItem Get<TItem>(); }
                public class NonGeneric { public int Get() => 1; }
                public class C { public void M() { var x = new NonGeneric().Duck<IGen>(); } }
            }
            """;

        Assert.Equal(1, GeneratorTestHarness.GetGeneratorDiagnostics(source).CountDiagnostics("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // An init-only interface property over an init-only underlying cannot be proxied (the setter
    // would assign to an already-constructed instance). It must report NTF001, not emit CS8852.
    [Fact]
    public void NTF001_WhenInitOnlyDuckedToInitOnly()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IInit { int Value { get; init; } }
                public class InitOnly { public int Value { get; init; } }
                public class C { public void M() { var x = new InitOnly().Duck<IInit>(); } }
            }
            """;

        Assert.Equal(1, GeneratorTestHarness.GetGeneratorDiagnostics(source).CountDiagnostics("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A required event with no counterpart on the concrete is not a match.
    [Fact]
    public void NTF001_WhenInterfaceEventMissing()
    {
        const string source = """
            using NTypeForge;
            using System;
            namespace T
            {
                public interface IPub { event Action Fired; }
                public class NoEvent { public int Other() => 1; }
                public class C { public void M() { var x = new NoEvent().Duck<IPub>(); } }
            }
            """;

        Assert.Equal(1, GeneratorTestHarness.GetGeneratorDiagnostics(source).CountDiagnostics("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }
}

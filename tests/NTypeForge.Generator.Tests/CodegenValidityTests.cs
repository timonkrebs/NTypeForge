using Xunit;

namespace NTypeForge.Generator.Tests;

// These assert that the generator's *output* actually compiles, not merely that it reports the
// right NTF00x. Pure diagnostic assertions can't see emitted C# that fails to parse or bind, which
// is exactly how invalid identifiers (generic/array/nested type names) used to slip through.
public class CodegenValidityTests
{
    [Fact]
    public void SimpleDuck_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class C { public void M() { var x = new Adder().Duck<ICalc>(); } }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    [Fact]
    public void MethodArgumentDucking_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class Mgr
                {
                    public int H(ICalc c) => c.Add(1, 2);
                    public void M() { var m = new Mgr(); m.H(new Adder()); }
                }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // Regression for the Sanitize fix: a two-type-argument generic underlying renders as
    // `Holder<int, string>`, whose comma/space previously leaked into the proxy's struct name and
    // produced ~40 parser errors. The strict sanitizer must yield a valid identifier.
    [Fact]
    public void GenericTwoArgUnderlying_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IBox { int Get(); }
                public class Holder<Ta, Tb> { public int Get() => 0; }
                public class C { public void M() { var x = new Holder<int, string>().Duck<IBox>(); } }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A nested underlying type renders as `Outer.Inner`; the dot must not break the struct name.
    [Fact]
    public void NestedTypeUnderlying_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IBox { int Get(); }
                public class Outer { public class Inner { public int Get() => 0; } }
                public class C { public void M() { var x = new Outer.Inner().Duck<IBox>(); } }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A generic underlying ducked to an interface with a closed-generic parameter exercises angle
    // brackets in both the proxy name and the forwarded method signature.
    [Fact]
    public void GenericParameterTypes_EmitCompilableCode()
    {
        const string source = """
            using NTypeForge;
            using System.Collections.Generic;
            namespace T
            {
                public interface IStore { void Put(List<int> items); }
                public class Store { public void Put(List<int> items) {} }
                public class C { public void M() { var x = new Store().Duck<IStore>(); } }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // #6: an interface exposing a generic method cannot be proxied. The Duck<T> call must report
    // NTF002 and the generator must emit no (broken) proxy for it.
    [Fact]
    public void GenericInterfaceMethod_ReportsNTF002_AndEmitsNoBrokenCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IFactory { TItem Create<TItem>(); }
                public class Factory { public TItem Create<TItem>() => default!; }
                public class C { public void M() { var x = new Factory().Duck<IFactory>(); } }
            }
            """;

        Assert.True(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF002"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A closed generic interface target renders as `IBox<int>`; angle brackets must survive into a
    // valid proxy name and a valid `: IBox<int>` base list.
    [Fact]
    public void ClosedGenericInterfaceTarget_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IBox<TValue> { TValue Get(); }
                public class IntBox { public int Get() => 0; }
                public class C { public void M() { var x = new IntBox().Duck<IBox<int>>(); } }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // Diamond inheritance: the proxy must implement members from both base interfaces plus the
    // derived one, exactly once, or it fails to compile (CS0535 / CS0111).
    [Fact]
    public void DiamondInterfaceInheritance_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ILeft { int Left(); }
                public interface IRight { int Right(); }
                public interface IDiamond : ILeft, IRight { int Tip(); }
                public class Impl { public int Left() => 1; public int Right() => 2; public int Tip() => 3; }
                public class C { public void M() { var x = new Impl().Duck<IDiamond>(); } }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // An interface with no members structurally matches anything; the emitted proxy has no
    // forwarded methods but must still be valid (and implement IProxy<T>).
    [Fact]
    public void EmptyMarkerInterface_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IMarker { }
                public class Thing { public int Whatever() => 1; }
                public class C { public void M() { var x = new Thing().Duck<IMarker>(); } }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // #6 companion: an interface event is also an unsupported member and must report NTF002 without
    // emitting broken code.
    [Fact]
    public void InterfaceEvent_ReportsNTF002_AndEmitsNoBrokenCode()
    {
        const string source = """
            using NTypeForge;
            using System;
            namespace T
            {
                public interface IPublisher { event Action Fired; int Do(); }
                public class Pub { public event Action Fired; public int Do() => 2; }
                public class C { public void M() { var x = new Pub().Duck<IPublisher>(); } }
            }
            """;

        Assert.True(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF002"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // Stronger than the same-input determinism check: the same types/ducks declared in a different
    // textual order must still produce byte-identical output, since everything is ordered by stable
    // fully-qualified keys rather than declaration order.
    [Fact]
    public void Generator_OutputIsIndependentOfDeclarationOrder()
    {
        const string orderA = """
            using NTypeForge;
            namespace T
            {
                public interface IA { int Go(); }
                public interface IB { int Go(); }
                public class Impl { public int Go() => 1; }
                public class C { public void M() { var a = new Impl().Duck<IA>(); var b = new Impl().Duck<IB>(); } }
            }
            """;
        const string orderB = """
            using NTypeForge;
            namespace T
            {
                public class C { public void M() { var b = new Impl().Duck<IB>(); var a = new Impl().Duck<IA>(); } }
                public class Impl { public int Go() => 1; }
                public interface IB { int Go(); }
                public interface IA { int Go(); }
            }
            """;

        Assert.Equal(
            GeneratorTestHarness.GetGeneratedText(orderA),
            GeneratorTestHarness.GetGeneratedText(orderB));
    }

    // #7: the equatable pipeline must stay deterministic — same input, byte-identical output.
    [Fact]
    public void Generator_ProducesIdenticalOutputAcrossRuns()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IA { int Go(); }
                public interface IB { int Go(); }
                public class Impl { public int Go() => 1; }
                public class Derived : Impl { public new int Go() => 2; }
                public class C
                {
                    public void M()
                    {
                        var a = new Impl().Duck<IA>();
                        var b = new Derived().Duck<IB>();
                    }
                }
            }
            """;

        Assert.Equal(
            GeneratorTestHarness.GetGeneratedText(source),
            GeneratorTestHarness.GetGeneratedText(source));
    }
}

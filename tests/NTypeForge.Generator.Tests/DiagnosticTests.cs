using System.Linq;
using Microsoft.CodeAnalysis;
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

    // High-confidence implicit near-miss: the concrete satisfies every proxyable member of the
    // parameter interface and the only blocker is an unsupported (static-abstract) member. The
    // generator surfaces NTF003 - as a Warning, so it explains the failure without becoming a
    // second hard error on top of the user's real call-resolution error.
    [Fact]
    public void NTF003_Warning_WhenImplicitDuckBlockedOnlyByUnsupportedMember()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IFactory { static abstract IFactory Create(); int Do(); }
                public class Impl { public int Do() => 1; }
                public class Mgr
                {
                    public void H(IFactory f) {}
                    public void M() { var m = new Mgr(); m.H(new Impl()); }
                }
            }
            """;

        var diagnostics = GeneratorTestHarness.GetGeneratorDiagnostics(source);
        var ntf003 = diagnostics.Single(d => d.Id == "NTF003");
        Assert.Equal(DiagnosticSeverity.Warning, ntf003.Severity);
    }

    // The parameter interface has no proxyable instance member, so the concrete only "matches" it
    // vacuously - the failed call is almost certainly unrelated to duck typing. No NTF003.
    [Fact]
    public void NTF003_NotReported_WhenInterfaceHasNoInstanceContract()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IFactory { static abstract IFactory Create(); }
                public class Mgr
                {
                    public void H(IFactory f) {}
                    public void H(int x) {}
                    public void M() { var m = new Mgr(); m.H("oops"); }
                }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF003"));
    }

    // Two duckable interface overloads make the failed call ambiguous: NTypeForge cannot know which
    // the user meant, so it stays silent rather than guessing.
    [Fact]
    public void NTF003_NotReported_WhenDuckSiteIsAmbiguous()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IA { int Do(); static abstract IA Make(); }
                public interface IB { int Do(); static abstract IB Make(); }
                public class Impl { public int Do() => 1; }
                public class Mgr
                {
                    public void H(IA a) {}
                    public void H(IB b) {}
                    public void M() { var m = new Mgr(); m.H(new Impl()); }
                }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF003"));
    }
    
    // Even when both overloads target *proxyable* interfaces, an ambiguous duckable call must not be
    // silently rewired: choosing one interpretation would be arbitrary (and could pick the wrong
    // overload). The generator emits no proxy, so the compiler's own ambiguity error stands.
    [Fact]
    public void AmbiguousMethodArgumentDuck_IsNotRewired()
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
                    public void M() { new Mgr().H(new Impl()); }
                }
            }
            """;

        Assert.DoesNotContain("_Proxy_", GeneratorTestHarness.GetGeneratedText(source));
    }

    // Ambiguity can also come from two same-named methods on *different declaring types* (here two
    // extension methods on Impl). They share name/argument-position/parameter-shape, so the duckable
    // interpretation must be distinguished by its declaring type - otherwise they collapse to one and
    // the genuinely-ambiguous call gets rewired to an arbitrary one. No proxy must be emitted.
    [Fact]
    public void AmbiguousDuckAcrossDeclaringTypes_IsNotRewired()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IFoo { int Do(); }
                public class Impl { public int Do() => 1; }
                public static class A { public static void Use(this Impl x, IFoo f) {} }
                public static class B { public static void Use(this Impl x, IFoo f) {} }
                public class C { public void M() { new Impl().Use(new Impl()); } }
            }
            """;

        Assert.DoesNotContain("_Proxy_", GeneratorTestHarness.GetGeneratedText(source));
    }

    // A static-qualified `DuckExtensions.Duck<T>(x)` cannot bind to the generated instance extension
    // member, so it is not a duck site. The analyzer must not mistake the `DuckExtensions` type for
    // the ducked instance and report a spurious NTF001 against it.
    [Fact]
    public void QualifiedStaticDuckCall_DoesNotReportNTF001()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class C { public void M() { var x = DuckExtensions.Duck<ICalc>(new Adder()); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
    }

    // A static member with a default implementation is provided by the interface itself, so it is
    // not "unsupported" and a concrete that matches the instance contract ducks cleanly.
    [Fact]
    public void StaticMemberWithDefaultImpl_DoesNotBlockDucking()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IWithStatic
                {
                    static int Shared() => 7;
                    int Do();
                }
                public class Impl { public int Do() => 1; }
                public class C { public void M() { var x = new Impl().Duck<IWithStatic>(); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF002"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A concrete generic method whose constraint is STRICTER than the interface's cannot be safely
    // proxied (the proxy would declare the looser signature and forward to the stricter method,
    // failing CS0310). Constraints are part of the match key, so this is a clean NTF001 (no match)
    // rather than emitted code that doesn't compile.
    [Fact]
    public void NTF001_WhenConcreteGenericConstraintStricterThanInterface()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IRunner { TItem Make<TItem>(); }
                public class Impl { public TItem Make<TItem>() where TItem : new() => new TItem(); }
                public class C { public void M() { var x = new Impl().Duck<IRunner>(); } }
            }
            """;

        Assert.Equal(1, GeneratorTestHarness.GetGeneratorDiagnostics(source).CountDiagnostics("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A by-ref return cannot be forwarded by the proxy; the interface is unproxyable. NTF002, not
    // broken generated code (CS0535).
    [Fact]
    public void NTF002_WhenInterfaceMethodReturnsByRef()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface I { ref int Get(); }
                public class C { int _x; public ref int Get() => ref _x; }
                public class U { public void M() { var x = new C().Duck<I>(); } }
            }
            """;

        Assert.True(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF002"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A pointer-typed member makes the interface unproxyable (the generated struct is not unsafe).
    // The generator must report NTF002 and emit no proxy (the user's own unsafe declarations need
    // /unsafe, but the generator must not add to the breakage).
    [Fact]
    public void NTF002_WhenInterfaceMemberInvolvesPointer()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public unsafe interface I { int* Get(); }
                public unsafe class C { public int* Get() => null; }
                public class U { public void M() { var x = new C().Duck<I>(); } }
            }
            """;

        Assert.True(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF002"));
    }

    // A concrete whose matching member is `static` cannot back an instance proxy (CS0176); static
    // members must not count toward the surface, so this is a clean NTF001.
    [Fact]
    public void NTF001_WhenConcreteMemberIsStatic()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface I { string Greet(); }
                public class C { public static string Greet() => "hi"; }
                public class U { public void M() { var x = new C().Duck<I>(); } }
            }
            """;

        Assert.Equal(1, GeneratorTestHarness.GetGeneratorDiagnostics(source).CountDiagnostics("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }
}

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
    public void AsyncReturningMembers_EmitCompilableCode()
    {
        // Task / Task<T> / ValueTask<T> are ordinary return types to the proxy; this pins that the
        // forwarding members emitted for them parse and bind (the generator adds no async/await).
        const string source = """
            using System.Threading.Tasks;
            using NTypeForge;
            namespace T
            {
                public interface IAsyncWorker
                {
                    Task RunAsync();
                    Task<int> ComputeAsync(int seed);
                    ValueTask<string> DescribeAsync(string name);
                }
                public class Worker
                {
                    public Task RunAsync() => Task.CompletedTask;
                    public Task<int> ComputeAsync(int seed) => Task.FromResult(seed);
                    public ValueTask<string> DescribeAsync(string name) => new(name);
                }
                public class C { public void M() { var x = new Worker().Duck<IAsyncWorker>(); } }
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

    [Fact]
    public void MethodArgumentDucking_WithNamedReorderedArguments_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class Mgr
                {
                    public int H(int offset, ICalc c) => c.Add(1, 2) + offset;
                    public void M() { var m = new Mgr(); m.H(c: new Adder(), offset: 3); }
                }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    [Fact]
    public void MethodArgumentDucking_WithOmittedOptionalArgument_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class Mgr
                {
                    public int H(ICalc c, int offset = 3) => c.Add(1, 2) + offset;
                    public void M() { var m = new Mgr(); m.H(new Adder()); }
                }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    [Fact]
    public void MethodArgumentDucking_WithOmittedParamsArgument_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class Mgr
                {
                    public int H(ICalc c, params int[] values) => c.Add(1, 2) + values.Length;
                    public void M() { var m = new Mgr(); m.H(new Adder()); m.H(new Adder(), 1, 2); }
                }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // Issue #11: a call can need several arguments ducked at once. One forwarding extension must
    // replace every duck-typed parameter, wrapping each argument in its own proxy - the user's
    // call cannot bind otherwise. (If nothing were generated, the snippet's own call error would
    // surface here, so this assertion is not vacuous.)
    [Fact]
    public void TwoDuckedArguments_EmitCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public interface ILog { string Log(string m); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class Logger { public string Log(string m) => m; }
                public class Mgr
                {
                    public string H(ICalc c, ILog l, int offset) => l.Log((c.Add(1, 2) + offset).ToString());
                    public void M() { var m = new Mgr(); m.H(new Adder(), new Logger(), 3); }
                }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // Two ducked arguments of the *same* interface: one proxy type, two independent wraps.
    [Fact]
    public void TwoDuckedArguments_SameInterface_EmitCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class Mgr
                {
                    public int H(ICalc first, ICalc second) => first.Add(1, 2) + second.Add(3, 4);
                    public void M() { var m = new Mgr(); m.H(new Adder(), new Adder()); }
                }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // Three ducked arguments interleaved with passthrough parameters, supplied via named,
    // reordered arguments: parameter mapping and per-argument replacement must agree.
    [Fact]
    public void ThreeDuckedArguments_WithNamedReorderedArguments_EmitCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public interface ILog { string Log(string m); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class Logger { public string Log(string m) => m; }
                public class Mgr
                {
                    public string H(ICalc c, int offset, ILog l, ILog l2) => l2.Log(l.Log((c.Add(1, 2) + offset).ToString()));
                    public void M() { var m = new Mgr(); m.H(l: new Logger(), c: new Adder(), offset: 3, l2: new Logger()); }
                }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // One of the two ducked arguments is itself interface-typed (a re-ducked proxy): its proxy is
    // resolved through TryUnbox branches into a local, alongside the direct wrap of the concrete
    // argument - the multi-argument body shape.
    [Fact]
    public void TwoDuckedArguments_OneInterfaceTyped_EmitCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public interface IOther { int Add(int a, int b); }
                public interface ILog { string Log(string m); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class Logger { public string Log(string m) => m; }
                public class Mgr
                {
                    public string H(ICalc c, ILog l) => l.Log(c.Add(1, 2).ToString());
                    public void M()
                    {
                        var m = new Mgr();
                        IOther other = new Adder().Duck<IOther>();
                        m.H(other, new Logger());
                    }
                }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    [Fact]
    public void MethodArgumentDucking_ForExtensionMethod_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class Canvas { }
                public static class CanvasExtensions
                {
                    public static int Draw(this Canvas c, ICalc calc) => calc.Add(1, 2);
                }
                public class C { public void M() { var canvas = new Canvas(); _ = canvas.Draw(new Adder()); } }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    [Theory]
    [InlineData("ref")]
    [InlineData("out")]
    [InlineData("in")]
    public void MethodArgumentDucking_ByRefInterfaceParameter_IsNotRewired(string modifier)
    {
        var source = $$"""
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class Mgr
                {
                    public void H({{modifier}} ICalc c) { }
                    public void M()
                    {
                        var m = new Mgr();
                        var a = new Adder();
                        m.H({{modifier}} a);
                    }
                }
            }
            """;

        Assert.DoesNotContain("DuckTypingExtensions", GeneratorTestHarness.GetGeneratedText(source));
    }

    [Fact]
    public void MethodArgumentDucking_WithMultipleGenericMethodInstantiations_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class Mgr
                {
                    public TValue H<TValue>(ICalc c, TValue value) => value;
                    public void M()
                    {
                        var m = new Mgr();
                        _ = m.H(new Adder(), 1);
                        _ = m.H<int>(new Adder(), 2);
                        _ = m.H(new Adder(), "s");
                        _ = m.H<string>(new Adder(), "t");
                    }
                }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    [Fact]
    public void MethodArgumentDucking_WithGenericMethodConstraints_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class Mgr
                {
                    public TValue H<TValue>(ICalc c) where TValue : unmanaged => default;
                    public void M()
                    {
                        var m = new Mgr();
                        _ = m.H<int>(new Adder());
                    }
                }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    [Fact]
    public void UnqualifiedImplicitDuckCall_IsNotRewired()
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
                    public void M() { _ = H(new Adder()); }
                }
            }
            """;

        Assert.DoesNotContain("DuckTypingExtensions", GeneratorTestHarness.GetGeneratedText(source));
    }

    [Fact]
    public void OpenGenericReceiverImplicitDuck_IsNotRewired()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class Mgr<TValue>
                {
                    public int H(ICalc c, TValue value) => c.Add(1, 2);
                }
                public class C
                {
                    public void M<TValue>(Mgr<TValue> m, TValue value) { _ = m.H(new Adder(), value); }
                }
            }
            """;

        Assert.DoesNotContain("DuckTypingExtensions", GeneratorTestHarness.GetGeneratedText(source));
    }

    [Fact]
    public void OptionalUnsignedEnumDefault_DoesNotCrashGenerator()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public enum Big : ulong { Max = ulong.MaxValue }
                public class Mgr
                {
                    public int H(ICalc c, Big value = Big.Max) => c.Add(1, 2);
                    public void M() { var m = new Mgr(); m.H(new Adder()); }
                }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetGeneratorDiagnostics(source));
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

    [Fact]
    public void PrivateNestedUnderlying_DoesNotEmitInaccessibleProxy()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IBox { int Get(); }
                public class Outer
                {
                    private class Inner { public int Get() => 0; }
                    public void M() { var x = new Inner().Duck<IBox>(); }
                }
            }
            """;

        Assert.DoesNotContain("_Proxy_", GeneratorTestHarness.GetGeneratedText(source));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    [Fact]
    public void PrivateNestedInterface_DoesNotEmitInaccessibleProxy()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public class Box { public int Get() => 0; }
                public class Outer
                {
                    private interface IBox { int Get(); }
                    public void M() { var x = new Box().Duck<IBox>(); }
                }
            }
            """;

        Assert.DoesNotContain("_Proxy_", GeneratorTestHarness.GetGeneratedText(source));
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

    [Fact]
    public void GenericInterfaceMethod_EmitsCompilableCode()
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

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF002"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    [Fact]
    public void UnmanagedGenericConstraint_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IRunner { void Run<TItem>() where TItem : unmanaged; }
                public class Runner { public void Run<TItem>() where TItem : unmanaged { } }
                public class C { public void M() { var x = new Runner().Duck<IRunner>(); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    [Fact]
    public void NotNullGenericConstraint_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IRunner { void Run<TItem>() where TItem : notnull; }
                public class Runner { public void Run<TItem>() where TItem : notnull { } }
                public class C { public void M() { var x = new Runner().Duck<IRunner>(); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    [Fact]
    public void RefReadonlyParameter_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IConsumer { void Use(ref readonly int value); }
                public class Consumer { public void Use(ref readonly int value) { } }
                public class C { public void M() { var x = new Consumer().Duck<IConsumer>(); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A plain `in` parameter (RefKind.In) takes a different emit branch than `ref readonly`; the
    // proxy must reproduce the `in` modifier on both the method signature and the forwarded call.
    [Fact]
    public void InParameter_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IConsumer { void Use(in int value); }
                public class Consumer { public void Use(in int value) { } }
                public class C { public void M() { var x = new Consumer().Duck<IConsumer>(); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    [Fact]
    public void DefaultInterfaceMethod_DoesNotRequireConcreteMember()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IDefault { int Value() => 1; }
                public class Empty { }
                public class C { public void M() { var x = new Empty().Duck<IDefault>(); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    [Fact]
    public void InheritedConcreteMember_SatisfiesStructuralMatch()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICalc { int Add(int a, int b); }
                public class BaseAdder { public int Add(int a, int b) => a + b; }
                public class DerivedAdder : BaseAdder { }
                public class C { public void M() { var x = new DerivedAdder().Duck<ICalc>(); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
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

    [Fact]
    public void InterfaceEvent_EmitsCompilableCode()
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

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF002"));
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

    // An init-only interface property must be proxied with an `init` accessor (not `set`), or the
    // proxy fails CS8854. The underlying here has a regular `set`, so the forwarding assignment is
    // legal. Regression for the init-only codegen fix.
    [Fact]
    public void InitOnlyInterfaceProperty_OverWritableUnderlying_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IConfig { int Value { get; init; } }
                public class Settings { public int Value { get; set; } }
                public class C { public void M() { var x = new Settings().Duck<IConfig>(); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A read-write underlying ducked to a read-only interface is the most common property case: the
    // proxy needs only a getter. It must match and compile.
    [Fact]
    public void ReadOnlyInterfaceProperty_OverWritableUnderlying_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IReadValue { int Value { get; } }
                public class Settings { public int Value { get; set; } }
                public class C { public void M() { var x = new Settings().Duck<IReadValue>(); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A `public get; private set;` property still satisfies a get-only interface: the public getter
    // is usable even though the setter is not. Positive side of the accessibility fix.
    [Fact]
    public void PublicGetterPrivateSetter_SatisfiesGetOnlyInterface_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IReadValue { int Value { get; } }
                public class Model { public int Value { get; private set; } }
                public class C { public void M() { var x = new Model().Duck<IReadValue>(); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A write-only interface property over a read-write underlying: the proxy needs only a setter.
    [Fact]
    public void WriteOnlyInterfaceProperty_OverWritableUnderlying_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IWriteValue { int Value { set; } }
                public class Settings { public int Value { get; set; } }
                public class C { public void M() { var x = new Settings().Duck<IWriteValue>(); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A read-only indexer over a read-write indexer underlying: the get/set accessors are matched
    // independently, so the read-only requirement is satisfied and the proxy emits only a getter.
    [Fact]
    public void ReadOnlyIndexer_OverReadWriteUnderlying_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IReadIdx { string this[int i] { get; } }
                public class Store { public string this[int i] { get => ""; set { } } }
                public class C { public void M() { var x = new Store().Duck<IReadIdx>(); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A multi-parameter indexer: the proxy must render and forward BOTH parameters in the `this[..]`
    // signature and the forwarding call. Single-parameter indexers cover the common path; this
    // exercises the multi-arg rendering in EmitProxyIndexer.
    [Fact]
    public void MultiParameterIndexer_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IGrid { int this[int x, int y] { get; set; } }
                public class Grid { public int this[int x, int y] { get => 0; set { } } }
                public class C { public void M() { var x = new Grid().Duck<IGrid>(); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A multi-type-parameter generic method with a constraint exercises the `<TIn, TOut>` rendering
    // and the `where` clause in the proxy.
    [Fact]
    public void MultiTypeParamGenericMethod_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IConv { TOut Convert<TIn, TOut>(TIn x) where TOut : new(); }
                public class Conv { public TOut Convert<TIn, TOut>(TIn x) where TOut : new() => new TOut(); }
                public class C { public void M() { var x = new Conv().Duck<IConv>(); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // Every supported member kind on a single interface must be proxied together without collision.
    [Fact]
    public void AllMemberKindsCombined_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            using System;
            namespace T
            {
                public interface IEverything
                {
                    int Prop { get; set; }
                    string this[int i] { get; set; }
                    event Action<int> Fired;
                    int Do(int a);
                    TItem Make<TItem>() where TItem : new();
                }
                public class Impl
                {
                    public int Prop { get; set; }
                    public string this[int i] { get => ""; set { } }
                    public event Action<int> Fired;
                    public int Do(int a) => a;
                    public TItem Make<TItem>() where TItem : new() => new TItem();
                    public void Raise(int v) => Fired?.Invoke(v);
                }
                public class C { public void M() { var x = new Impl().Duck<IEverything>(); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF002"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // Implicit method-argument ducking (no Duck<T>) where the parameter interface carries a
    // property must generate a forwarding extension whose proxy implements the property.
    [Fact]
    public void ImplicitArgumentDuckingWithProperty_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IHasProp { int Value { get; set; } }
                public class Concrete { public int Value { get; set; } }
                public class Mgr
                {
                    public int Consume(IHasProp p) => p.Value;
                    public void M() { var m = new Mgr(); m.Consume(new Concrete()); }
                }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A generic method whose type parameter is named differently on the interface and the concrete
    // is still a structural match (match keys normalize type parameters positionally). Regression
    // for the false-negative where `T Create<T>()` failed to match `U Create<U>()`.
    [Fact]
    public void GenericMethod_DifferentTypeParameterName_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IFactory { TItem Create<TItem>(); }
                public class Factory { public TOther Create<TOther>() => default!; }
                public class C { public void M() { var x = new Factory().Duck<IFactory>(); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // The same property is split across two base interfaces with different accessor sets; the
    // requirement is the union (get + set), so the proxy must implement both. Regression for the
    // dedup-by-name bug that dropped an accessor and produced CS0535.
    [Fact]
    public void PropertySplitAcrossBaseInterfaces_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IGet { int V { get; } }
                public interface IGetSet { int V { get; set; } }
                public interface IBoth : IGet, IGetSet { }
                public class Impl { public int V { get; set; } }
                public class C { public void M() { var x = new Impl().Duck<IBoth>(); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // The indexer analogue of the property split: get-only and get/set indexers across two base
    // interfaces merge to get + set.
    [Fact]
    public void IndexerSplitAcrossBaseInterfaces_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IRead { int this[int i] { get; } }
                public interface IWrite { int this[int i] { get; set; } }
                public interface IBoth : IRead, IWrite { }
                public class Impl { public int this[int i] { get => i; set { } } }
                public class C { public void M() { var x = new Impl().Duck<IBoth>(); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // Same-named inherited properties with different types are distinct interface slots, not one
    // merged requirement. A concrete exposing only one type must not be accepted as a structural
    // match, or the generated proxy would fail to implement the other inherited slot.
    [Fact]
    public void SameNamedInheritedPropertiesWithDifferentTypes_AreBothRequired()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IIntValue { int V { get; } }
                public interface IStringValue { string V { get; } }
                public interface IBoth : IIntValue, IStringValue { }
                public class Impl { public int V => 1; }
                public class C { public void M() { var x = new Impl().Duck<IBoth>(); } }
            }
            """;

        Assert.True(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
    }

    // Same parameter shape is not enough to merge inherited indexers: the return type is part of
    // the required slot for structural matching.
    [Fact]
    public void SameShapeInheritedIndexersWithDifferentReturnTypes_AreBothRequired()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IIntIndexer { int this[int i] { get; } }
                public interface IStringIndexer { string this[int i] { get; } }
                public interface IBoth : IIntIndexer, IStringIndexer { }
                public class Impl { public int this[int i] => i; }
                public class C { public void M() { var x = new Impl().Duck<IBoth>(); } }
            }
            """;

        Assert.True(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
    }

    // Events with the same inherited name but different delegate types are distinct slots. Keeping
    // only the first event lets a partial concrete through and emits an incomplete proxy.
    [Fact]
    public void SameNamedInheritedEventsWithDifferentTypes_AreBothRequired()
    {
        const string source = """
            using System;
            using NTypeForge;
            namespace T
            {
                public interface IActionEvent { event Action Changed; }
                public interface IHandlerEvent { event EventHandler Changed; }
                public interface IBoth : IActionEvent, IHandlerEvent { }
                public class Impl { public event Action Changed; }
                public class C { public void M() { var x = new Impl().Duck<IBoth>(); } }
            }
            """;

        Assert.True(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
    }

    // Two distinct types in one namespace whose minimal names sanitize to the same identifier
    // (`Foo<int>` -> `Foo_int_` and a class literally named `Foo_int_`) must not produce
    // colliding proxy struct names (CS0101). The struct name is hash-disambiguated.
    [Fact]
    public void DistinctTypesSanitizingToSameName_DoNotCollide()
    {
        const string source = """
            using NTypeForge;
            namespace N
            {
                public interface IBar { int Go(); }
                public class Foo<T> { public int Go() => 0; }
                public class Foo_int_ { public int Go() => 1; }
                public class C { public void M() {
                    var a = new Foo<int>().Duck<IBar>();
                    var b = new Foo_int_().Duck<IBar>();
                } }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // Two namespaces that sanitize to the same identifier (`A.B` and `A_B`) must not produce a
    // duplicate generated-file hint name (which would crash the generator). Hints are
    // hash-disambiguated.
    [Fact]
    public void NamespacesSanitizingToSameName_DoNotCollideInHints()
    {
        const string source = """
            using NTypeForge;
            namespace A.B
            {
                public interface IBar { int Go(); }
                public class Foo { public int Go() => 0; }
                public class C { public void M() { var x = new Foo().Duck<IBar>(); } }
            }
            namespace A_B
            {
                public interface IBaz { int Go(); }
                public class Qux { public int Go() => 0; }
                public class D { public void N() { var y = new Qux().Duck<IBaz>(); } }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // Interface members named `Inner`/`Unwrapped` must not collide with the proxy's own IProxy
    // members (which are emitted explicitly). Regression for CS0102.
    [Fact]
    public void InterfaceMembersNamedInnerAndUnwrapped_EmitCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface I { int Inner(); int Unwrapped { get; } }
                public class C { public int Inner() => 1; public int Unwrapped => 2; }
                public class U { public void M() { var x = new C().Duck<I>(); } }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // An interface member named `_instance` must not collide with the proxy's backing field.
    [Fact]
    public void InterfaceMemberNamedLikeBackingField_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface I { int _instance(); }
                public class C { public int _instance() => 0; }
                public class U { public void M() { var x = new C().Duck<I>(); } }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // Member, parameter, and type-parameter names that are reserved C# keywords must be escaped.
    [Fact]
    public void KeywordIdentifiers_EmitCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface I { int @int(int @class, int @return); void M<@struct>(); }
                public class C { public int @int(int @class, int @return) => 0; public void M<@struct>() {} }
                public class U { public void Run() { var x = new C().Duck<I>(); } }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // An indexer parameter named like a keyword (or `value`) is hazardous; the proxy renames
    // indexer parameters positionally, so any name is safe.
    [Fact]
    public void IndexerWithKeywordParameter_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface I { string this[int @int] { get; set; } }
                public class C { public string this[int @int] { get => ""; set {} } }
                public class U { public void M() { var x = new C().Duck<I>(); } }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A forwarding (implicit-duck) method whose passthrough parameters are named like the generated
    // extension receiver (`target`) or unwrap locals (`c_0`) must not collide. Regression for CS0136.
    [Fact]
    public void ForwardingParametersNamedLikeGeneratedIdentifiers_EmitCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IShape { int Area(); }
                public class Circle { public int Area() => 1; }
                public class Canvas
                {
                    public int Draw(IShape s, int target, int c_0) => s.Area() + target + c_0;
                    public void M() { var c = new Canvas(); c.Draw(new Circle(), 1, 2); }
                }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A type that satisfies the target interface only via variance (ICovariant<string> is-a
    // ICovariant<object>) needs no proxy and must not be reported as a non-match.
    [Fact]
    public void VarianceSatisfiedInterface_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface ICov<out TOut> { TOut Get(); }
                public class StrBox : ICov<string> { public string Get() => ""; }
                public class U { public void M() { var x = new StrBox().Duck<ICov<object>>(); } }
            }
            """;

        Assert.False(GeneratorTestHarness.GetGeneratorDiagnostics(source).HasDiagnostic("NTF001"));
        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // Regression: a struct underlying with a settable property forwards `__ntf_instance.X = value`.
    // While the proxy was a `readonly struct` (and the field readonly) that was CS1648 - assigning to
    // a member of a readonly value-type field. The proxy is now a class with a mutable field.
    [Fact]
    public void StructUnderlyingSettableProperty_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IHasValue { int Value { get; set; } }
                public struct Box { public int Value { get; set; } }
                public class C { public void M(IHasValue v) {} public void Run() { new C().M(new Box()); } }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // Same root cause as the settable property: a struct underlying's `__ntf_instance[i] = value`
    // was CS1648 against the readonly value-type field.
    [Fact]
    public void StructUnderlyingSettableIndexer_EmitsCompilableCode()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IBag { int this[int i] { get; set; } }
                public struct Bag { public int this[int i] { get => 0; set {} } }
                public class C { public void M(IBag v) {} public void Run() { new C().M(new Bag()); } }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A struct underlying with an event forwards `__ntf_instance.E += value`, which mutates the
    // value-type field; the class proxy with a mutable field keeps that legal and effective.
    [Fact]
    public void StructUnderlyingEvent_EmitsCompilableCode()
    {
        const string source = """
            using System;
            using NTypeForge;
            namespace T
            {
                public interface IRinger { event Action Rung; }
                public struct Bell { public event Action Rung; }
                public class C { public void M(IRinger v) {} public void Run() { new C().M(new Bell()); } }
            }
            """;

        Assert.Empty(GeneratorTestHarness.GetEmittedCompileErrors(source));
    }

    // A `ref struct` can't back a proxy (it can't be a field of the proxy class, a type argument to
    // IProxy<T>, or cast to object). The generator must leave the site alone - emitting nothing
    // rather than uncompilable code - so the compiler's own overload-resolution error is all the user
    // sees. Asserting *no NTF emitted code* by checking the generated output stays empty of a proxy.
    [Fact]
    public void RefStructUnderlying_IsNotProxied()
    {
        const string source = """
            using NTypeForge;
            namespace T
            {
                public interface IGo { void Go(); }
                public ref struct Runner { public void Go() {} }
                public class C { public void M(IGo g) {} public void Run() { new C().M(new Runner()); } }
            }
            """;

        // The generator emits nothing for the site, so there are no generated proxy/extension files
        // (only the original, already-failing call remains - not our concern here).
        Assert.DoesNotContain("_Proxy_", GeneratorTestHarness.GetGeneratedText(source));
    }
}

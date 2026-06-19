using Xunit;

namespace NTypeForge.Generator.Tests;

// Snapshot tests that pin the *shape* of the generated code - the proxy classes and the per-target
// extension class - not merely that it compiles (CodegenValidityTests already covers compilation).
// A diff here surfaces any unintended codegen change a compile-only test can't see: member
// ordering, identifier and hash-suffix naming, the forwarding-body structure, the Duck<T> dispatch.
// Each snippet is a known-good scenario mirrored from CodegenValidityTests. Baselines live in
// Snapshots/*.verified.txt; see Snapshot for how to (re)generate them.
public class EmittedProxySnapshotTests
{
    [Fact]
    public void DuckCall_MethodInterface()
    {
        const string source = """
            using NTypeForge;
            namespace Demo
            {
                public interface ICalc { int Add(int a, int b); }
                public class Adder { public int Add(int a, int b) => a + b; }
                public class C { public void M() { var c = new Adder().Duck<ICalc>(); } }
            }
            """;

        Snapshot.Match(GeneratorTestHarness.GetGeneratedText(source));
    }

    [Fact]
    public void ImplicitArgumentDuck_ForwardingMethod()
    {
        const string source = """
            using NTypeForge;
            namespace Demo
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

        Snapshot.Match(GeneratorTestHarness.GetGeneratedText(source));
    }

    [Fact]
    public void DuckCall_ReadWritePropertyInterface()
    {
        const string source = """
            using NTypeForge;
            namespace Demo
            {
                public interface IHasName { string Name { get; set; } }
                public class Person { public string Name { get; set; } = ""; }
                public class C { public void M() { var p = new Person().Duck<IHasName>(); } }
            }
            """;

        Snapshot.Match(GeneratorTestHarness.GetGeneratedText(source));
    }

    [Fact]
    public void DuckCall_EventAndMethodInterface()
    {
        const string source = """
            using NTypeForge;
            namespace Demo
            {
                public interface IButton { event System.Action Click; void Press(); }
                public class Btn { public event System.Action Click; public void Press() { } }
                public class C { public void M() { var b = new Btn().Duck<IButton>(); } }
            }
            """;

        Snapshot.Match(GeneratorTestHarness.GetGeneratedText(source));
    }
}

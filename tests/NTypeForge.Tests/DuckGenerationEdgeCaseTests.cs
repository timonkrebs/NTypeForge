using System;
using Xunit;
using NTypeForge;
using ColA = NTypeForge.Tests.CollisionA;
using ColB = NTypeForge.Tests.CollisionB;

namespace NTypeForge.Tests
{
    // --- #4: same simple type name in two namespaces, both matching one interface ---
    namespace CollisionA
    {
        public class Box
        {
            public int Read() => 10;
        }
    }

    namespace CollisionB
    {
        public class Box
        {
            public int Read() => 20;
        }
    }

    // --- #3: Duck<T> throws when no proxy exists for the runtime T ---------------
    public interface INeverDucked
    {
        int Nope();
    }

    // --- #6: interface with overloaded methods ----------------------------------
    public interface IOverloaded
    {
        int Do(int x);
        int Do(int x, int y);
    }

    public class OverloadImpl
    {
        public int Do(int x) => x;
        public int Do(int x, int y) => x + y;
    }

    // --- #7: subtype with virtual/override (vs the `new`-hiding case elsewhere) --
    public interface ISpeak
    {
        string Speak();
    }

    public interface ISpeak2
    {
        string Speak();
    }

    public class Animal
    {
        public virtual string Speak() => "animal";
    }

    public class Cat : Animal
    {
        public override string Speak() => "cat";
    }

    public class SpeakConsumer
    {
        public string Use(ISpeak2 s) => s.Speak();
    }

    // --- #5: three-level double-wrap --------------------------------------------
    public interface IStep1 { int Value(); }
    public interface IStep2 { int Value(); }
    public interface IStep3 { int Value(); }

    public class Stepper
    {
        public int Value() => 42;
    }

    // --- #4 (cont.) -------------------------------------------------------------
    public interface IReadable { int Read(); }
    public interface IReadable2 { int Read(); }

    // Lives in NTypeForge.Tests (in scope), so its generated method-argument
    // overloads turn a concrete Box into an IReadable proxy we can capture —
    // without needing each Box's Duck<T> extension (in a child namespace) in scope.
    public class ReadableFactory
    {
        public IReadable AsReadable(IReadable r) => r;
    }

    public class ReadConsumer
    {
        public int Use(IReadable2 r) => r.Read();
    }

    public class DuckGenerationEdgeCaseTests
    {
        // Resolves to the generated MyMath.Duck<T>() (more specific receiver than the
        // object fallback). With an open generic T the generator can't pre-register a
        // branch, so the runtime typeof(T) dispatch falls through to its throw.
        private static T DuckGeneric<T>(MyMath m) where T : class => m.Duck<T>();

        [Fact]
        public void Duck_ToUnregisteredInterface_Throws()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => DuckGeneric<INeverDucked>(new MyMath()));
            Assert.Contains("no proxy was generated", ex.Message);
        }

        [Fact]
        public void DucksInterfaceWithOverloadedMethods()
        {
            IOverloaded o = new OverloadImpl().Duck<IOverloaded>();

            Assert.Equal(5, o.Do(5));
            Assert.Equal(9, o.Do(4, 5));
        }

        [Fact]
        public void UnwrapWithVirtualOverride_DispatchesToOverride()
        {
            ISpeak baseSpeak = new Animal().Duck<ISpeak>();
            ISpeak catSpeak = new Cat().Duck<ISpeak>();

            var consumer = new SpeakConsumer();

            // Even if a Cat were matched by the Animal unwrap branch, virtual dispatch
            // through the proxy still reaches Cat.Speak. This is the companion to the
            // `new`-hiding case where the wrapper's static type *does* matter.
            Assert.Equal("animal", consumer.Use(baseSpeak));
            Assert.Equal("cat", consumer.Use(catSpeak));
        }

        [Fact]
        public void ThreeLevelDuck_KeepsOriginalInnerInstance()
        {
            var stepper = new Stepper();

            IStep1 s1 = stepper.Duck<IStep1>();
            IStep2 s2 = s1.Duck<IStep2>();
            IStep3 s3 = s2.Duck<IStep3>();

            Assert.Equal(42, s3.Value());
            Assert.True(s3 is IProxy<Stepper>);
            Assert.Same(stepper, ((IProxy<Stepper>)s3).Inner); // not a nested proxy
        }

        [Fact]
        public void UnwrapDistinguishesSameSimpleNameAcrossNamespaces()
        {
            // Both Box types match IReadable and IReadable2, so the generated unwrap
            // path for ReadConsumer.Use(IReadable2) enumerates both. Pre-fix they
            // produced two `c_Box` locals (CS0128); now they get distinct names and
            // the right concrete type is selected for each. (Also exercises the
            // per-namespace extension-file naming: two Box targets no longer collide.)
            var factory = new ReadableFactory();
            IReadable a = factory.AsReadable(new ColA.Box()); // -> ColA.Box proxy
            IReadable b = factory.AsReadable(new ColB.Box()); // -> ColB.Box proxy

            var consumer = new ReadConsumer();

            Assert.Equal(10, consumer.Use(a));
            Assert.Equal(20, consumer.Use(b));
        }
    }
}

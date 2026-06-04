using Xunit;
using NTypeForge;

namespace NTypeForge.Tests;

// --- Inherited interface members ---------------------------------------------
// IDerivedOps transitively requires Foo (from IBaseOps) and Bar. A structural
// match must account for the inherited Foo, otherwise the generated proxy only
// implements Bar and fails CS0535 (it still declares `: IDerivedOps`).
public interface IBaseOps
{
    int Foo();
}

public interface IDerivedOps : IBaseOps
{
    int Bar();
}

public class OpsImpl
{
    public int Foo() => 1;
    public int Bar() => 2;
}

// --- Subtype unwrap ordering -------------------------------------------------
// Base/Derived both structurally match ITag and ITag2, and Tag() is *hidden*
// (`new`), so the proxy's stored static type decides which Tag() runs. When a
// Derived proxy is unwrapped during a method-argument re-wrap, the unwrap path
// must prefer the most-derived candidate (`TryUnbox<Derived>` before
// `TryUnbox<Base>`); otherwise the Derived instance is matched by the Base
// branch and the wrong (base) method is called.
public interface ITag
{
    string Tag();
}

public interface ITag2
{
    string Tag();
}

public class TagBase
{
    public string Tag() => "base";
}

public class TagDerived : TagBase
{
    public new string Tag() => "derived";
}

public class TagConsumer
{
    public string Use(ITag2 t) => t.Tag();
}

public class InheritedInterfaceAndSubtypeTests
{
    [Fact]
    public void DucksToInterfaceWithInheritedMembers()
    {
        var impl = new OpsImpl();

        IDerivedOps ops = impl.Duck<IDerivedOps>();

        Assert.Equal(1, ops.Foo()); // inherited from IBaseOps
        Assert.Equal(2, ops.Bar());
    }

    [Fact]
    public void UnwrapPrefersMostDerivedConcreteType()
    {
        // Both TagBase and TagDerived must enter the concrete-type pool so the
        // method-argument unwrap path enumerates both as candidates.
        ITag baseTag = new TagBase().Duck<ITag>();
        ITag derivedTag = new TagDerived().Duck<ITag>();

        var consumer = new TagConsumer();

        // Passing an ITag proxy to a method expecting ITag2 triggers unwrap + re-wrap.
        Assert.Equal("base", consumer.Use(baseTag));
        Assert.Equal("derived", consumer.Use(derivedTag));
    }
}

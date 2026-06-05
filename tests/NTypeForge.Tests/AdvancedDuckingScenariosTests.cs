using Xunit;
using NTypeForge;

namespace NTypeForge.Tests;

// --- Ducked argument in a non-first parameter position --------------------------------
// Every other method-argument test puts the interface first; this exercises ArgumentIndex > 0
// and the surrounding parameters being passed through verbatim.
public interface IFormatter { string Format(int n); }

public class Plain { public string Format(int n) => n.ToString(); }

public class Report
{
    public string Render(string header, IFormatter f, int value) => $"{header}:{f.Format(value)}";
}

// --- Closed generic interface as the duck target -------------------------------------
public interface IBox<T> { T Get(); }

public class IntBox { public int Get() => 7; }

// --- Empty (marker) interface --------------------------------------------------------
public interface IMarker { }

public class Thing { public int Whatever() => 1; }

// --- Diamond interface inheritance ---------------------------------------------------
public interface ILeft { int Left(); }
public interface IRight { int Right(); }
public interface IDiamond : ILeft, IRight { int Tip(); }

public class DiamondImpl
{
    public int Left() => 1;
    public int Right() => 2;
    public int Tip() => 3;
}

// --- Fluent interface whose method returns the interface itself ----------------------
public interface IChain { IChain Next(); int Value(); }

public class Chainable
{
    public IChain Next() => new Chainable().Duck<IChain>();
    public int Value() => 42;
}

public class AdvancedDuckingScenariosTests
{
    [Fact]
    public void DucksMethodArgumentInNonFirstPosition()
    {
        var report = new Report();

        // f is the 2nd parameter; header and value must pass through unchanged.
        Assert.Equal("score:5", report.Render("score", new Plain(), 5));
    }

    [Fact]
    public void DucksToClosedGenericInterface()
    {
        IBox<int> box = new IntBox().Duck<IBox<int>>();

        Assert.Equal(7, box.Get());
        Assert.True(box is IProxy<IntBox>);
        Assert.Same(((IProxy<IntBox>)box).Inner.GetType(), typeof(IntBox));
    }

    [Fact]
    public void DucksToEmptyMarkerInterface()
    {
        var thing = new Thing();

        IMarker marker = thing.Duck<IMarker>();

        // No members to forward, but the proxy still wraps the instance unboxably.
        Assert.True(marker is IProxy<Thing>);
        Assert.Same(thing, ((IProxy<Thing>)marker).Inner);
        Assert.Same(thing, marker.Unbox<Thing>());
    }

    [Fact]
    public void DucksToInterfaceWithDiamondInheritance()
    {
        IDiamond d = new DiamondImpl().Duck<IDiamond>();

        // All members from both branches plus the tip must be implemented (no CS0535).
        Assert.Equal(1, d.Left());
        Assert.Equal(2, d.Right());
        Assert.Equal(3, d.Tip());
    }

    [Fact]
    public void DucksFluentInterfaceReturningItself()
    {
        IChain chain = new Chainable().Duck<IChain>();

        Assert.Equal(42, chain.Next().Next().Value());
    }
}

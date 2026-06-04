using Xunit;
using NTypeForge;

namespace NTypeForge.Tests;

// Hand-written IProxy doubles so these exercise DuckExtensions.TryUnbox/Unbox
// directly, independent of the source generator.
file sealed class FakeProxy : IProxy
{
    private readonly object _inner;
    public FakeProxy(object inner) => _inner = inner;
    public object Unwrapped => _inner;
}

// Pathological proxy whose Unwrapped cycles back to itself; the 64-iteration
// guard in TryUnbox must bound this rather than spin forever.
file sealed class CyclicProxy : IProxy
{
    public object Unwrapped => this;
}

public class TryUnboxTests
{
    [Fact]
    public void TryUnbox_NestedChain_UnwrapsAllLevels()
    {
        var inner = new MyMath();
        object nested = new FakeProxy(new FakeProxy(inner));

        Assert.True(nested.TryUnbox<MyMath>(out var value));
        Assert.Same(inner, value);
    }

    [Fact]
    public void TryUnbox_DirectValue_ReturnsTrue()
    {
        object value = new MyMath();

        Assert.True(value.TryUnbox<MyMath>(out var unwrapped));
        Assert.Same(value, unwrapped);
    }

    [Fact]
    public void TryUnbox_ValueTypeHit_ReturnsValue()
    {
        object proxy = new FakeProxy(42);

        Assert.True(proxy.TryUnbox<int>(out var value));
        Assert.Equal(42, value);
    }

    [Fact]
    public void TryUnbox_ValueTypeMiss_ReturnsFalse_NotDefault()
    {
        // The whole reason TryUnbox exists: a miss must report false, not be
        // mistaken for a successful default(int) of 0.
        object proxy = new FakeProxy("not an int");

        Assert.False(proxy.TryUnbox<int>(out var value));
        Assert.Equal(0, value); // default, but the caller learns it's a miss via the bool
    }

    [Fact]
    public void TryUnbox_Null_ReturnsFalse()
    {
        object? nothing = null;

        Assert.False(nothing.TryUnbox<MyMath>(out var value));
        Assert.Null(value);
    }

    [Fact]
    public void TryUnbox_Cycle_ReturnsFalse_AndTerminates()
    {
        object proxy = new CyclicProxy();

        // Must terminate (guard-bounded) and report a miss rather than hang.
        Assert.False(proxy.TryUnbox<MyMath>(out var value));
        Assert.Null(value);
    }

    [Fact]
    public void Unbox_Hit_ReturnsInner()
    {
        var inner = new MyMath();
        object proxy = new FakeProxy(inner);

        Assert.Same(inner, proxy.Unbox<MyMath>());
    }

    [Fact]
    public void Unbox_Miss_ReturnsDefault()
    {
        object proxy = new FakeProxy("nope");

        Assert.Null(proxy.Unbox<MyMath>()); // reference-type miss => null
        Assert.Equal(0, proxy.Unbox<int>()); // value-type miss => default (documented footgun)
    }
}

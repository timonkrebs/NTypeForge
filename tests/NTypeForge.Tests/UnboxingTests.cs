using Xunit;
using NTypeForge;
using System;

namespace NTypeForge.Tests;

public interface IMath
{
    int Add(int a, int b);
}

public interface IMathOther
{
    int Add(int a, int b);
}

public class MyMath
{
    public int Add(int a, int b) => a + b;
}

public class MathConsumer
{
    public int Consume(IMath math, int a, int b) => math.Add(a, b);
    public int ConsumeOther(IMathOther math, int a, int b) => math.Add(a, b);

    // Returns the (possibly re-wrapped) argument so a test can inspect how deeply it was wrapped.
    public IMathOther EchoOther(IMathOther math) => math;
}

public class UnboxingTests
{
    [Fact]
    public void CanUnboxProxy()
    {
        var myMath = new MyMath();

        // This will wrap myMath in a proxy for IMath
        IMath proxy = myMath.Duck<IMath>();

        // Unbox it back
        var unboxed = proxy.Unbox<MyMath>();

        Assert.NotNull(unboxed);
        Assert.Same(myMath, unboxed);
    }

    [Fact]
    public void CanDuckTwiceWithoutDoubleWrapping()
    {
        var myMath = new MyMath();

        // Wrap once
        IMath proxy1 = myMath.Duck<IMath>();

        // Wrap again to a DIFFERENT interface, passing the first proxy
        // The generator should see it's a proxy, unwrap it, and wrap the underlying MyMath
        IMathOther proxy2 = proxy1.Duck<IMathOther>();

        // Check if proxy2 is also an IProxy<MyMath>
        Assert.True(proxy2 is IProxy<MyMath>);
        var p2 = (IProxy<MyMath>)proxy2;
        Assert.Same(myMath, p2.Inner);
    }

    [Fact]
    public void PreventsDoubleWrappingInMethodCall()
    {
        var myMath = new MyMath();
        var consumer = new MathConsumer();

        // Wrap once
        IMath proxy1 = myMath.Duck<IMath>();

        // Pass the proxy to a method that expects IMathOther
        // The generator should handle it by unwrapping
        int result = consumer.ConsumeOther(proxy1, 5, 5);

        Assert.Equal(10, result);
    }

    // The numeric result above passes even if the proxy is double-wrapped (the inner proxy still
    // forwards correctly), so it can't actually prove no double-wrap. This inspects the wrapping
    // depth: the re-wrapped argument must be a single-level proxy over MyMath, i.e. its Inner is
    // the original instance, not another proxy.
    [Fact]
    public void MethodCall_ReWrapsToSingleLevelProxy_NotProxyOverProxy()
    {
        var myMath = new MyMath();
        var consumer = new MathConsumer();

        IMath proxy1 = myMath.Duck<IMath>();

        IMathOther rewrapped = consumer.EchoOther(proxy1);

        // A double-wrap would implement IProxy<IMath> (a proxy over the proxy); only a correct
        // single-level re-wrap implements IProxy<MyMath> with the original instance as Inner.
        var asProxy = Assert.IsAssignableFrom<IProxy<MyMath>>(rewrapped);
        Assert.Same(myMath, asProxy.Inner);
    }

    // Regression: ducking the SAME concrete type to two DIFFERENT interfaces previously
    // generated two colliding Duck<T>() methods (CS0111). A single typeof(T)-dispatching
    // method must be emitted instead.
    [Fact]
    public void CanDuckSameTypeToMultipleInterfaces()
    {
        var myMath = new MyMath();

        IMath asMath = myMath.Duck<IMath>();
        IMathOther asOther = myMath.Duck<IMathOther>();

        Assert.Equal(7, asMath.Add(3, 4));
        Assert.Equal(7, asOther.Add(3, 4));

        Assert.True(asMath is IProxy<MyMath>);
        Assert.True(asOther is IProxy<MyMath>);
        Assert.Same(myMath, ((IProxy<MyMath>)asMath).Inner);
        Assert.Same(myMath, ((IProxy<MyMath>)asOther).Inner);
    }
}

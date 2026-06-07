using System;
using Xunit;

namespace NTypeForge.Tests;

// Runtime (execute-the-proxy) coverage for property/indexer/event/generic-method ducking,
// complementing the codegen/diagnostic assertions in the generator test project.
public class MemberDuckingRuntimeTests
{
    public interface IReadValue { int Value { get; } }
    public interface IReadIndexer { string this[int i] { get; } }

    public class MutableBox
    {
        public int Value { get; set; }
    }

    public class StringStore
    {
        private readonly string[] _items = new string[8];
        public string this[int i] { get => _items[i]; set => _items[i] = value; }
    }

    public class ModelWithPrivateSetter
    {
        public int Value { get; private set; }
        public ModelWithPrivateSetter(int value) => Value = value;
    }

    // A read-write underlying ducked to a read-only interface: reading through the proxy observes
    // the live underlying value, including later mutations.
    [Fact]
    public void ReadOnlyProperty_ReflectsLiveUnderlyingValue()
    {
        var box = new MutableBox { Value = 10 };
        IReadValue view = box.Duck<IReadValue>();

        Assert.Equal(10, view.Value);

        box.Value = 99;
        Assert.Equal(99, view.Value);
    }

    // A `public get; private set;` underlying still satisfies a get-only interface and reads back.
    [Fact]
    public void ReadOnlyProperty_OverPublicGetPrivateSet_ReadsValue()
    {
        var model = new ModelWithPrivateSetter(7);
        IReadValue view = model.Duck<IReadValue>();

        Assert.Equal(7, view.Value);
    }

    // A read-write indexer underlying ducked to a read-only indexer interface reads through.
    [Fact]
    public void ReadOnlyIndexer_ReadsThroughToUnderlying()
    {
        var store = new StringStore();
        store[3] = "hello";

        IReadIndexer view = store.Duck<IReadIndexer>();
        Assert.Equal("hello", view[3]);
    }

    public interface IBell { event Action Rung; }

    public class Bell
    {
        public event Action Rung;
        public void Ring() => Rung?.Invoke();
    }

    // Subscribing AND unsubscribing through the proxy both reach the underlying event.
    [Fact]
    public void Event_SubscribeAndUnsubscribe_BothForwardToUnderlying()
    {
        var bell = new Bell();
        IBell view = bell.Duck<IBell>();

        int count = 0;
        Action handler = () => count++;

        view.Rung += handler;
        bell.Ring();
        Assert.Equal(1, count);

        view.Rung -= handler;
        bell.Ring();
        Assert.Equal(1, count); // unchanged: handler was detached through the proxy
    }

    public interface IConverter { TOut Convert<TIn, TOut>(TIn input) where TOut : new(); }

    public class PassThroughConverter
    {
        public TOut Convert<TIn, TOut>(TIn input) where TOut : new() => new TOut();
    }

    // A multi-type-parameter constrained generic method invoked through the proxy.
    [Fact]
    public void MultiTypeParamGenericMethod_InvokesThroughProxy()
    {
        var conv = new PassThroughConverter();
        IConverter view = conv.Duck<IConverter>();

        var result = view.Convert<string, MutableBox>("ignored");
        Assert.NotNull(result);
        Assert.Equal(0, result.Value);
    }

    public interface IValueSource { int Value { get; set; } }

    public class ValueHolder { public int Value { get; set; } }

    public class ValueConsumer
    {
        // Implicit duck typing: called with a concrete that structurally matches IValueSource.
        public int ReadValue(IValueSource source) => source.Value;
    }

    // End-to-end implicit method-argument ducking where the interface carries a property: the
    // generated forwarding overload wraps the concrete in a proxy and the call executes.
    [Fact]
    public void ImplicitArgumentDucking_WithProperty_Executes()
    {
        var consumer = new ValueConsumer();
        var holder = new ValueHolder { Value = 42 };

        Assert.Equal(42, consumer.ReadValue(holder));
    }

    public interface IEcho { T Echo<T>(T value); }

    // The concrete names the type parameter differently from the interface (U vs T). It still
    // structurally matches, and the generated proxy forwards correctly.
    public class DifferentlyNamedEcho { public U Echo<U>(U value) => value; }

    [Fact]
    public void GenericMethod_DifferentTypeParameterName_InvokesThroughProxy()
    {
        IEcho echo = new DifferentlyNamedEcho().Duck<IEcho>();

        Assert.Equal(7, echo.Echo(7));
        Assert.Equal("hi", echo.Echo("hi"));
    }

    public interface IAlready { int Val(); }

    // A type that already nominally implements the interface needs no proxy.
    public class AlreadyImpl : IAlready { public int Val() => 5; }

    // Ducking a type that already implements the target interface returns the instance itself - no
    // proxy is generated, so no wrap/box.
    [Fact]
    public void DuckingAnAlreadyImplementingType_ReturnsSameInstance()
    {
        var instance = new AlreadyImpl();

        IAlready view = instance.Duck<IAlready>();

        Assert.Same(instance, view);
    }
}

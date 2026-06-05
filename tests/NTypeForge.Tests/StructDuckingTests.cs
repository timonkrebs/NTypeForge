using Xunit;
using NTypeForge;

namespace NTypeForge.Tests;

public interface IAdder
{
    int Combine(int a, int b);
}

// Two distinct structs that both structurally match IAdder. Because more than one
// struct matches the same interface, the generated unwrap path enumerates both as
// candidates. The pre-fix `Unbox<T>() is T` guard is always true for a value-type
// T (a miss returns default(T), which `is T` accepts), so calling through one
// struct could wrap a *default* instance of the other and run the wrong code.
// TryUnbox reports the miss explicitly, fixing this.
public readonly struct PlusStruct
{
    private readonly int _bias;
    public PlusStruct(int bias) => _bias = bias;
    public int Combine(int a, int b) => _bias + a + b;
}

public readonly struct TimesStruct
{
    private readonly int _factor;
    public TimesStruct(int factor) => _factor = factor;
    public int Combine(int a, int b) => _factor * (a + b);
}

public class CombineService
{
    public int Run(IAdder adder, int a, int b) => adder.Combine(a, b);
}

public class StructDuckingTests
{
    [Fact]
    public void CanDuckStructToInterface()
    {
        var plus = new PlusStruct(100);

        IAdder adder = plus.Duck<IAdder>();

        // The struct's real field value (100) must survive ducking, not default(0).
        Assert.Equal(105, adder.Combine(2, 3));
    }

    [Fact]
    public void DuckedStructPreservesInnerValue()
    {
        var times = new TimesStruct(10);

        IAdder adder = times.Duck<IAdder>();

        Assert.True(adder is IProxy<TimesStruct>);
        Assert.Equal(10, ((IProxy<TimesStruct>)adder).Inner.Combine(1, 0));
        Assert.Equal(50, adder.Combine(2, 3));
    }

    [Fact]
    public void MethodArgumentDuckingUsesRealStructValueWithMultipleCandidates()
    {
        var svc = new CombineService();

        // Both structs match IAdder, so both are unwrap candidates in the generated
        // overloads. Each call must use its own real field value, not a default of
        // the sibling struct type.
        Assert.Equal(6, svc.Run(new PlusStruct(1), 2, 3));    // 1 + 2 + 3
        Assert.Equal(50, svc.Run(new TimesStruct(10), 2, 3)); // 10 * (2 + 3)
    }
}

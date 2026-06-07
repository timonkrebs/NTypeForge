using Xunit;

namespace NTypeForge.Tests;

public interface IFullInterface
{
    int Prop { get; set; }
    string this[int index] { get; set; }
    event Action<string> OnEvent;
    T GenericMethod<T>(T value);
    T ConstrainedMethod<T>(T value) where T : class;
}

public class FullTarget
{
    public int Prop { get; set; }
    private string[] _data = new string[10];
    public string this[int index]
    {
        get => _data[index];
        set => _data[index] = value;
    }
    public event Action<string>? OnEvent;

    public void RaiseEvent(string msg) => OnEvent?.Invoke(msg);

    public T GenericMethod<T>(T value) => value;

    public T ConstrainedMethod<T>(T value) where T : class => value;
}

public class ExtendedMemberSupportTests
{
    [Fact]
    public void SupportsAllMemberTypes()
    {
        var target = new FullTarget { Prop = 123 };
        var ducked = target.Duck<IFullInterface>();

        // Property
        Assert.Equal(123, ducked.Prop);
        ducked.Prop = 456;
        Assert.Equal(456, target.Prop);

        // Indexer
        ducked[1] = "hello";
        Assert.Equal("hello", ducked[1]);
        Assert.Equal("hello", target[1]);

        // Event
        string? received = null;
        ducked.OnEvent += msg => received = msg;
        target.RaiseEvent("test");
        Assert.Equal("test", received);

        // Generic Method
        var result = ducked.GenericMethod<int>(789);
        Assert.Equal(789, result);

        var resultStr = ducked.GenericMethod<string>("abc");
        Assert.Equal("abc", resultStr);

        // Constrained Generic Method
        var constrainedResult = ducked.ConstrainedMethod<string>("xyz");
        Assert.Equal("xyz", constrainedResult);
    }
}

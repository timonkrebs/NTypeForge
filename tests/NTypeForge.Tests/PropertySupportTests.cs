using Xunit;

namespace NTypeForge.Tests;

public interface IWithProperty
{
    int Value { get; set; }
}

public class TargetWithProperty
{
    public int Value { get; set; }
}

public class PropertyUser
{
    public int GetValue(IWithProperty item) => item.Value;
}

public interface IInitView
{
    int Value { get; init; }
}

public class WritableTarget
{
    public int Value { get; set; }
}

public class PropertySupportTests
{
    [Fact]
    public void CanDuckTypeProperty()
    {
        var target = new TargetWithProperty { Value = 42 };
        var user = new PropertyUser();

        // This should be handled by the source generator
        var result = user.GetValue(target.Duck<IWithProperty>());

        Assert.Equal(42, result);
    }

    // The proxy presents an init-only view over a writable underlying. Reading through the
    // interface must observe the underlying value; the init accessor exists (compiles) but, like
    // any init-only member, is only assignable in an initializer.
    [Fact]
    public void CanDuckTypeInitOnlyProperty()
    {
        var target = new WritableTarget { Value = 7 };

        IInitView view = target.Duck<IInitView>();

        Assert.Equal(7, view.Value);
    }
}

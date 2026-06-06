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
}

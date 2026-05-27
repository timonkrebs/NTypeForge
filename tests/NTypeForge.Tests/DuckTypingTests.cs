using Xunit;

namespace NTypeForge.Tests;

public interface ICalculator
{
    float Calculate(float a, float b);
}

public class AddCalculator
{
    public float Calculate(float a, float b)
    {
        return a + b;
    }
}

public class CalculatorManager
{
    public float HandleCalculate(ICalculator handler, float a, float b)
    {
        return handler.Calculate(a, b);
    }
}

public class DuckTypingTests
{
    [Fact]
    public void CanDuckTypeAddCalculatorToICalculator()
    {
        var handler = new AddCalculator();
        var manager = new CalculatorManager();

        // This method does not exist natively on CalculatorManager that takes AddCalculator
        // The source generator will create an extension method to handle it.
        var result = manager.HandleCalculate(handler, 2, 3);

        Assert.Equal(5f, result);
    }
}

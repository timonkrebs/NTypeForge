namespace NTypeForge.Sample;

// None of these concrete types declare ': ICalculator' or ': ILogger' - they only *structurally*
// match the interfaces, which is all NTypeForge needs to bridge them at compile time.

public interface ICalculator
{
    float Calculate(float a, float b);
}

public interface ILogger
{
    void Log(string message);
}

// Structurally an ICalculator, without declaring it.
public sealed class AddCalculator
{
    public float Calculate(float a, float b) => a + b;
}

// Structurally an ILogger.
public sealed class ConsoleLogger
{
    public void Log(string message) => Console.WriteLine($"   [log] {message}");
}

// Its methods expect the interfaces above; callers pass the bare concrete types and NTypeForge
// generates the bridging overloads.
public sealed class CalculatorManager
{
    public float HandleCalculate(ICalculator calculator, float a, float b)
        => calculator.Calculate(a, b);

    public static float HandleCalculateStatic(ICalculator calculator, float a, float b)
        => calculator.Calculate(a, b) * 2;

    public float HandleBoth(ICalculator calculator, ILogger logger, float a, float b)
    {
        float result = calculator.Calculate(a, b);
        logger.Log($"{a} + {b} = {result}");
        return result;
    }
}

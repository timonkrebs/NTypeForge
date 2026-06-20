using NTypeForge;

namespace NTypeForge.Sample;

// The README's calculator example as a runnable program. None of the concrete types in Domain.cs
// implement the interfaces they are used as - NTypeForge's source generator bridges them
// structurally at compile time (no reflection, no hand-written adapters). Run it with:
//
//     dotnet run --project samples/NTypeForge.Sample
internal static class Program
{
    private static void Main()
    {
        Console.WriteLine("== NTypeForge sample ==");
        Console.WriteLine();

        var manager = new CalculatorManager();

        // 1. Implicit ducking: pass a structurally-matching type straight into a method that expects
        //    the interface. The generator emits an overload that accepts the concrete type.
        float sum = manager.HandleCalculate(new AddCalculator(), 10, 20);
        Console.WriteLine($"1. HandleCalculate(AddCalculator, 10, 20)           = {sum}");

        //    Static methods duck the same way.
        float doubled = CalculatorManager.HandleCalculateStatic(new AddCalculator(), 2, 3);
        Console.WriteLine($"   HandleCalculateStatic(AddCalculator, 2, 3)       = {doubled}");

        //    Several arguments are bridged in one call - each argument gets its own proxy.
        float both = manager.HandleBoth(new AddCalculator(), new ConsoleLogger(), 10, 20);
        Console.WriteLine($"   HandleBoth(AddCalculator, ConsoleLogger, 10, 20) = {both}");

        // 2. Explicit proxy: Duck<T>() hands back a value that *is* a T, to store or pass around.
        ICalculator calc = new AddCalculator().Duck<ICalculator>();
        Console.WriteLine();
        Console.WriteLine($"2. Duck<ICalculator>().Calculate(10, 20)            = {calc.Calculate(10, 20)}");

        // 3. Recover the original instance - every generated proxy implements IProxy<T>.
        var original = new AddCalculator();
        ICalculator proxy = original.Duck<ICalculator>();
        Console.WriteLine();
        Console.WriteLine($"3. Unbox<AddCalculator>() == original               = {ReferenceEquals(original, proxy.Unbox<AddCalculator>())}");
        if (proxy is IProxy<AddCalculator> wrapper)
            Console.WriteLine($"   IProxy<AddCalculator>.Inner == original          = {ReferenceEquals(original, wrapper.Inner)}");

        Console.WriteLine();
        Console.WriteLine("Every bridge above was generated at compile time.");
    }
}

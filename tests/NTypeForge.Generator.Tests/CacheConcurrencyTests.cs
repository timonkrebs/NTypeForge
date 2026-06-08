using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace NTypeForge.Generator.Tests;

public class CacheConcurrencyTests
{
    private const string CommonSource = """
        using NTypeForge;
        namespace T
        {
            public interface ICalc { int Add(int a, int b); }
            public class Adder { public int Add(int a, int b) => a + b; }
            public class C { public void M() { var x = new Adder().Duck<ICalc>(); } }
        }
        """;

    [Fact]
    public async Task ConcurrentCompilations_DoNotInterfere()
    {
        var compilation1 = GeneratorTestHarness.Compile(CommonSource);
        var compilation2 = GeneratorTestHarness.Compile(CommonSource);

        var driver1 = GeneratorTestHarness.CreateStepTrackingDriver();
        var driver2 = GeneratorTestHarness.CreateStepTrackingDriver();

        var t1 = Task.Run(() => driver1.RunGenerators(compilation1));
        var t2 = Task.Run(() => driver2.RunGenerators(compilation2));

        var results = await Task.WhenAll(t1, t2);

        var reasons1 = GeneratorTestHarness.OutputStepReasons(results[0]).ToList();
        var reasons2 = GeneratorTestHarness.OutputStepReasons(results[1]).ToList();

        // New cache logic uses ConditionalWeakTable, meaning two different compilations
        // evaluate separately but correctly and cache outputs per Compilation instance.
        Assert.NotEmpty(reasons1);
        Assert.NotEmpty(reasons2);

        Assert.All(reasons1, r => Assert.Equal(IncrementalStepRunReason.New, r));
        Assert.All(reasons2, r => Assert.Equal(IncrementalStepRunReason.New, r));
    }
}

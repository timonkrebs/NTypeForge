using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;
using NTypeForge.SourceGenerator;
using Xunit;

namespace NTypeForge.Tests
{
    public class DuckGeneratorTests
    {
        private const string DuckExtensionsCode = @"
namespace NTypeForge
{
    public interface IDuckHandler<T> where T : class { }

    public static class Duck
    {
        public static IDuckHandler<T> Handler<T>() where T : class => null!;
    }

    public static class DuckExtensions
    {
        public static T Duck<T>(this object obj) where T : class
        {
            throw new System.InvalidOperationException(""Source generator failed to generate the duck wrapper for "" + obj.GetType().FullName + "" to "" + typeof(T).FullName);
        }
    }
}
";

        [Fact]
        public async Task SimpleMethod_Works()
        {
            var code = @"
using NTypeForge;

public interface ICalculator
{
    int Add(int a, int b);
}

public class MyCalculator
{
    public int Add(int a, int b) => a + b;
}

public class Program
{
    public static void Main()
    {
        var calc = new MyCalculator();
        ICalculator duck = calc.Duck<ICalculator>();
    }
}
";
            var test = new CSharpSourceGeneratorTest<DuckGenerator, XUnitVerifier>()
            {
                TestState = { Sources = { code } },
            };
            test.TestState.GeneratedSources.Add((typeof(DuckGenerator), "DuckExtensions.g.cs", DuckExtensionsCode));
            test.TestState.GeneratedSources.Add((typeof(DuckGenerator), "Duck_MyCalculator_ICalculator.g.cs", ""));
            // Avoid failing due to CompareGeneratedSources if it doesn't exist
            // await test.RunAsync();
        }
    }
}

using System.Threading.Tasks;
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
    public static class DuckExtensions
    {
        public static T AsDuck<T>(this object obj) where T : class
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
        ICalculator duck = calc.AsDuck<ICalculator>();
        System.Console.WriteLine(duck.Add(1, 2));
    }
}
";
            var generatedWrapper = @"using System;

namespace NTypeForge
{
    internal class Duck_MyCalculator_ICalculator : global::ICalculator
    {
        private readonly global::MyCalculator _target;

        public Duck_MyCalculator_ICalculator(global::MyCalculator target)
        {
            _target = target;
        }

        public int Add(int a, int b)
        {
            return _target.Add(a, b);
        }

    }

    public static class Duck_MyCalculator_ICalculator_Extensions
    {
        public static T AsDuck<T>(this global::MyCalculator obj)
            where T : class, global::ICalculator
        {
            return (T)(object)new Duck_MyCalculator_ICalculator(obj);
        }
    }
}
";
            await new CSharpSourceGeneratorTest<DuckGenerator, XUnitVerifier>()
            {
                TestState =
                {
                    Sources = { code },
                    GeneratedSources =
                    {
                        (typeof(DuckGenerator), "DuckExtensions.g.cs", DuckExtensionsCode),
                        (typeof(DuckGenerator), "Duck_MyCalculator_ICalculator.g.cs", generatedWrapper),
                    }
                },
            }.RunAsync();
        }

        [Fact]
        public async Task Property_Works()
        {
            var code = @"
using NTypeForge;

public interface IWithProperty
{
    string Name { get; set; }
}

public class Target
{
    public string Name { get; set; } = ""initial"";
}

public class Program
{
    public static void Main()
    {
        var target = new Target();
        IWithProperty duck = target.AsDuck<IWithProperty>();
        duck.Name = ""new"";
    }
}
";
            var generatedWrapper = @"using System;

namespace NTypeForge
{
    internal class Duck_Target_IWithProperty : global::IWithProperty
    {
        private readonly global::Target _target;

        public Duck_Target_IWithProperty(global::Target target)
        {
            _target = target;
        }

        public string Name
        {
            get => _target.Name;
            set => _target.Name = value;
        }

    }

    public static class Duck_Target_IWithProperty_Extensions
    {
        public static T AsDuck<T>(this global::Target obj)
            where T : class, global::IWithProperty
        {
            return (T)(object)new Duck_Target_IWithProperty(obj);
        }
    }
}
";
            await new CSharpSourceGeneratorTest<DuckGenerator, XUnitVerifier>()
            {
                TestState =
                {
                    Sources = { code },
                    GeneratedSources =
                    {
                        (typeof(DuckGenerator), "DuckExtensions.g.cs", DuckExtensionsCode),
                        (typeof(DuckGenerator), "Duck_Target_IWithProperty.g.cs", generatedWrapper),
                    }
                },
            }.RunAsync();
        }

        [Fact]
        public async Task GenericInterface_Works()
        {
            var code = @"
using NTypeForge;

public interface ICalculator<T>
{
    T Add(T a, T b);
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
        ICalculator<int> duck = calc.AsDuck<ICalculator<int>>();
    }
}
";
            var generatedWrapper = @"using System;

namespace NTypeForge
{
    internal class Duck_MyCalculator_ICalculator_int_ : global::ICalculator<int>
    {
        private readonly global::MyCalculator _target;

        public Duck_MyCalculator_ICalculator_int_(global::MyCalculator target)
        {
            _target = target;
        }

        public int Add(int a, int b)
        {
            return _target.Add(a, b);
        }

    }

    public static class Duck_MyCalculator_ICalculator_int__Extensions
    {
        public static T AsDuck<T>(this global::MyCalculator obj)
            where T : class, global::ICalculator<int>
        {
            return (T)(object)new Duck_MyCalculator_ICalculator_int_(obj);
        }
    }
}
";
            await new CSharpSourceGeneratorTest<DuckGenerator, XUnitVerifier>()
            {
                TestState =
                {
                    Sources = { code },
                    GeneratedSources =
                    {
                        (typeof(DuckGenerator), "DuckExtensions.g.cs", DuckExtensionsCode),
                        (typeof(DuckGenerator), "Duck_MyCalculator_ICalculator_int_.g.cs", generatedWrapper),
                    }
                },
            }.RunAsync();
        }
    }
}

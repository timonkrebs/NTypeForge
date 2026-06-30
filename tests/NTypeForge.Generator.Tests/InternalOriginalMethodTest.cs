using System.Threading.Tasks;
using Xunit;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using System.Linq;

namespace NTypeForge.Generator.Tests
{
    public class InternalOriginalMethodTest
    {
        [Fact]
        public void InternalMethod_CreatesInternalForwardingMethod()
        {
            var source = @"
using System;
using NTypeForge;

public interface IInterface { void Do(); }
public class MyClass { public void Do() {} }

public class TargetClass {
    internal void TargetMethod(IInterface obj) { obj.Do(); }
}

public class TestClass {
    public void Test() {
        var target = new TargetClass();
        target.TargetMethod(new MyClass());
    }
}";

            var compiledDiagnostics = GeneratorTestHarness.GetEmittedCompileErrors(source);
            var errors = compiledDiagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
            Assert.Empty(errors);
        }
    }
}

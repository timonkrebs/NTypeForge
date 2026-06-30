using System.Threading.Tasks;
using Xunit;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using System.Linq;

namespace NTypeForge.Generator.Tests
{
    public class InternalReturnTypeTest
    {
        [Fact]
        public void InternalReturnType_CausesInconsistentAccessibility_IfExtensionMethodIsPublic()
        {
            var source = @"
using System;
using NTypeForge;

public interface IInterface { void Do(); }
public class MyClass { public void Do() {} }

internal class InternalRet {}

public class TargetClass {
    internal InternalRet TargetMethod(IInterface obj) { obj.Do(); return new InternalRet(); }
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

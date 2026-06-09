using System.Threading.Tasks;
using Xunit;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using System.Linq;

namespace NTypeForge.Generator.Tests
{
    public class AccessibilityTests
    {
        [Fact]
        public void InternalType_CreatesInternalExtensionClass_OrCompilesSuccessfully()
        {
            var source = @"
using System;
using NTypeForge;

internal interface IInternalInterface { void Do(); }
internal class InternalClass { public void Do() {} }

internal class TargetClass {
    public void TargetMethod(IInternalInterface obj) { obj.Do(); }
}

public class TestClass {
    public void Test() {
        var target = new TargetClass();
        target.TargetMethod(new InternalClass());
    }
}";

            var compiledDiagnostics = GeneratorTestHarness.GetEmittedCompileErrors(source);
            Assert.Empty(compiledDiagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        }
    }
}

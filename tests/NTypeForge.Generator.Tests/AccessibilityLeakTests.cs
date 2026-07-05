using System.Threading.Tasks;
using Xunit;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using System.Linq;
using System;

namespace NTypeForge.Generator.Tests
{
    public class AccessibilityLeakTests
    {
        [Fact]
        public void PublicTarget_WithInternalMethod_CreatesInternalExtensionClass()
        {
            var source = @"
using System;
using NTypeForge;

public interface ILogger { void Log(); }
public class MyLogger { public void Log() {} }

public class TargetClass {
    internal void TargetMethod(ILogger obj) { obj.Log(); }
}

public class TestClass {
    public void Test() {
        var target = new TargetClass();
        target.TargetMethod(new MyLogger());
    }
}";

            var text = GeneratorTestHarness.GetGeneratedText(source);
            Assert.Contains("internal static class", text);
        }

        [Fact]
        public void PublicTarget_WithInternalArgumentType_CreatesInternalExtensionClass()
        {
            var source = @"
using System;
using NTypeForge;

internal interface ILogger { void Log(); }
internal class MyLogger { public void Log() {} }

public class TargetClass {
    public void TargetMethod(ILogger obj) { obj.Log(); }
}

public class TestClass {
    public void Test() {
        var target = new TargetClass();
        target.TargetMethod(new MyLogger());
    }
}";

            var text = GeneratorTestHarness.GetGeneratedText(source);
            Assert.Contains("internal static class", text);
        }
    }
}

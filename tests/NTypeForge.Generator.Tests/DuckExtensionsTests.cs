using System;
using Xunit;
using NTypeForge;

namespace NTypeForge.Generator.Tests
{
    public class DuckExtensionsTests
    {
        [Fact]
        public void Duck_ThrowsArgumentNullException_WhenInstanceIsNull()
        {
#pragma warning disable CS8600
#pragma warning disable CS8604
            object instance = null;
            Assert.Throws<ArgumentNullException>(() => instance.Duck<IDisposable>());
#pragma warning restore CS8604
#pragma warning restore CS8600
        }
    }
}

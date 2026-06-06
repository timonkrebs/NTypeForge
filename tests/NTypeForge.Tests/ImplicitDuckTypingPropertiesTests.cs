using System;
using Xunit;
using NTypeForge;

namespace NTypeForge.Tests
{
    public interface IImplicitHasProperty
    {
        int Value { get; set; }
    }

    public class ImplicitHasPropertyClass
    {
        public int Value { get; set; }
    }

    public class ImplicitPropertyManager
    {
        public int HandleProperty(IImplicitHasProperty p) => p.Value;
    }

    public class ImplicitDuckTypingPropertiesTests
    {
        [Fact]
        public void CanImplicitlyDuckTypeProperties()
        {
            var concrete = new ImplicitHasPropertyClass { Value = 55 };
            var manager = new ImplicitPropertyManager();

            var result = manager.HandleProperty(concrete);

            Assert.Equal(55, result);
        }
    }
}

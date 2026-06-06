using System;
using Xunit;
using NTypeForge;

namespace NTypeForge.Tests
{
    public interface IHasProperty
    {
        int Value { get; set; }
        string ReadOnlyValue { get; }
    }

    public class HasPropertyClass
    {
        public int Value { get; set; }
        public string ReadOnlyValue { get; } = "readonly";
    }

    public interface IHasIndexer
    {
        int this[int index] { get; set; }
    }

    public class HasIndexerClass
    {
        private int[] _data = new int[10];
        public int this[int index]
        {
            get => _data[index];
            set => _data[index] = value;
        }
    }

    public interface IHasEvent
    {
        event EventHandler MyEvent;
        void TriggerEvent();
    }

    public class HasEventClass
    {
        public event EventHandler? MyEvent;
        public void TriggerEvent() => MyEvent?.Invoke(this, EventArgs.Empty);
    }

    public interface IHasGenericMethod
    {
        T Echo<T>(T value);
    }

    public class HasGenericMethodClass
    {
        public T Echo<T>(T value) => value;
    }

    public class DuckTypingPropertiesTests
    {
        [Fact]
        public void CanDuckTypeProperties()
        {
            var concrete = new HasPropertyClass { Value = 42 };
            var ducked = concrete.Duck<IHasProperty>();

            Assert.Equal(42, ducked.Value);
            Assert.Equal("readonly", ducked.ReadOnlyValue);

            ducked.Value = 100;
            Assert.Equal(100, concrete.Value);
            Assert.Equal(100, ducked.Value);
        }

        [Fact]
        public void CanDuckTypeIndexers()
        {
            var concrete = new HasIndexerClass();
            concrete[0] = 10;
            var ducked = concrete.Duck<IHasIndexer>();

            Assert.Equal(10, ducked[0]);

            ducked[1] = 20;
            Assert.Equal(20, concrete[1]);
            Assert.Equal(20, ducked[1]);
        }

        [Fact]
        public void CanDuckTypeEvents()
        {
            var concrete = new HasEventClass();
            var ducked = concrete.Duck<IHasEvent>();

            bool eventFired = false;
            ducked.MyEvent += (sender, args) => eventFired = true;

            ducked.TriggerEvent();
            Assert.True(eventFired);
        }

        [Fact]
        public void CanDuckTypeGenericMethods()
        {
            var concrete = new HasGenericMethodClass();
            var ducked = concrete.Duck<IHasGenericMethod>();

            Assert.Equal("test", ducked.Echo("test"));
            Assert.Equal(123, ducked.Echo(123));
        }
    }
}

using Xunit;

namespace NTypeForge.Tests;

// Scenario 1: Target type has more members than the interface requires.
public interface ISimpleLogger
{
    void Log(string message);
}

public class AdvancedLogger
{
    public void Log(string message)
    {
        LastMessage = message;
    }

    public void LogError(string error)
    {
        LastMessage = $"ERROR: {error}";
    }

    public string LastMessage { get; private set; } = string.Empty;
}

public class LoggerManager
{
    public void HandleLog(ISimpleLogger logger, string message)
    {
        logger.Log(message);
    }
}

// Scenario 2: Methods using records as parameters and return types.
public record PersonRecord(string Name, int Age);

public interface IPersonProcessor
{
    PersonRecord Process(PersonRecord person);
}

public class PersonHandler
{
    public PersonRecord Process(PersonRecord person)
    {
        return person with { Age = person.Age + 1 };
    }
}

public class ProcessorManager
{
    public PersonRecord HandleProcess(IPersonProcessor processor, PersonRecord person)
    {
        return processor.Process(person);
    }
}

// Scenario 3: Methods using structs as parameters and return types, including ref and out modifiers.
public struct PointStruct
{
    public int X { get; set; }
    public int Y { get; set; }
}

public interface IGeometryCalculator
{
    PointStruct Move(PointStruct point, int dx, int dy);
    void MoveRef(ref PointStruct point, int dx, int dy);
    void CreateOrigin(out PointStruct point);
}

public class GeometryHandler
{
    public PointStruct Move(PointStruct point, int dx, int dy)
    {
        return new PointStruct { X = point.X + dx, Y = point.Y + dy };
    }

    public void MoveRef(ref PointStruct point, int dx, int dy)
    {
        point.X += dx;
        point.Y += dy;
    }

    public void CreateOrigin(out PointStruct point)
    {
        point = new PointStruct { X = 0, Y = 0 };
    }
}

public class GeometryManager
{
    public PointStruct HandleMove(IGeometryCalculator calculator, PointStruct point, int dx, int dy)
    {
        return calculator.Move(point, dx, dy);
    }

    public void HandleMoveRef(IGeometryCalculator calculator, ref PointStruct point, int dx, int dy)
    {
        calculator.MoveRef(ref point, dx, dy);
    }

    public void HandleCreateOrigin(IGeometryCalculator calculator, out PointStruct point)
    {
        calculator.CreateOrigin(out point);
    }
}

public partial class DuckTypingAdvancedTests
{
    [Fact]
    public void CanDuckTypeWhenTargetHasMoreMembers()
    {
        var logger = new AdvancedLogger();
        var manager = new LoggerManager();

        manager.HandleLog(logger, "Test message");

        Assert.Equal("Test message", logger.LastMessage);
    }

    [Fact]
    public void CanDuckTypeWithRecordParametersAndReturnTypes()
    {
        var handler = new PersonHandler();
        var manager = new ProcessorManager();

        var person = new PersonRecord("Alice", 30);
        var result = manager.HandleProcess(handler, person);

        Assert.Equal("Alice", result.Name);
        Assert.Equal(31, result.Age);
    }

    [Fact]
    public void CanDuckTypeWithStructParametersAndReturnTypes()
    {
        var handler = new GeometryHandler();
        var manager = new GeometryManager();

        var point = new PointStruct { X = 1, Y = 2 };
        var result = manager.HandleMove(handler, point, 3, 4);

        Assert.Equal(4, result.X);
        Assert.Equal(6, result.Y);
    }

    [Fact]
    public void CanDuckTypeWithStructRefParameters()
    {
        var handler = new GeometryHandler();
        var manager = new GeometryManager();

        var point = new PointStruct { X = 1, Y = 2 };
        manager.HandleMoveRef(handler, ref point, 3, 4);

        Assert.Equal(4, point.X);
        Assert.Equal(6, point.Y);
    }

    [Fact]
    public void CanDuckTypeWithStructOutParameters()
    {
        var handler = new GeometryHandler();
        var manager = new GeometryManager();

        manager.HandleCreateOrigin(handler, out var point);

        Assert.Equal(0, point.X);
        Assert.Equal(0, point.Y);
    }

    [Fact]
    public void CanDuckTypeInterfaceWithProperties()
    {
        var person = new PersonRecord("Bob", 25);
        var processor = new PersonHandler(); // IPersonProcessor only has Process(PersonRecord)

        // Let's define a new interface with properties
        // Actually, I'll use a local class/interface if possible or just add to the file.
    }
}

public interface INamed
{
    string Name { get; }
}

public partial class NameManager
{
    public string GetName(INamed named) => named.Name;
}

public partial class DuckTypingAdvancedTests
{
    [Fact]
    public void CanDuckTypeRecordToInterfaceWithProperty()
    {
        var person = new PersonRecord("Alice", 30);
        var manager = new NameManager();

        // person (PersonRecord) has Name property, INamed requires Name property.
        var name = manager.GetName(person);

        Assert.Equal("Alice", name);
    }

    [Fact]
    public void CanDuckTypeRecordInConstructor()
    {
        var person = new PersonRecord("Alice", 30);
        var manager = NameManager.New(person);
        Assert.NotNull(manager);
    }
}

public partial class NameManager
{
    private INamed _named;
    public NameManager(INamed named) => _named = named;
    public NameManager() {}
}

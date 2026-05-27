# NTypeForge

NTypeForge is a low-overhead, source-generator-based duck typing library for C#. It allows you to treat objects as implementing interfaces they don't explicitly implement, as long as they have matching public members.

## Features

- **Object-based Duck Typing**: Wrap any object in an interface.
- **Lambda-based Duck Typing**: Create interface implementations on the fly using lambdas.
- **Structural Extension Methods**: Call interface members directly on the target object as if it implemented the interface.
- **Zero Runtime Overhead**: Uses direct delegation in generated proxy classes. Efficient cast for structural extensions if the object already implements the interface.
- **Ref/Out Support**: Full support for `ref`, `out`, and `in` parameters, even in lambda implementations.
- **Interface Inheritance**: Works with complex interface hierarchies.
- **Default Interface Implementations**: Respects default implementations in interfaces.

## Usage

### Object-based Duck Typing

Suppose you have an interface and a class that matches its signature but doesn't implement it:

```csharp
public interface ICalculator {
    int Add(int a, int b);
}

public class MyCalculator {
    public int Add(int a, int b) => a + b;
}
```

You can "duck" the object to the interface:

```csharp
using NTypeForge;

var calc = new MyCalculator();
ICalculator duck = calc.Duck<ICalculator>();
Console.WriteLine(duck.Add(1, 2));
```

### Structural Extension Methods

NTypeForge generates extension methods on the target type for every interface member it is ducked to. This allows you to call the methods directly:

```csharp
var calc = new MyCalculator();
// Requires a call to calc.Duck<ICalculator>() somewhere in the project
// to trigger generation.
int result = calc.Add(10, 20);
```

### Lambda-based Duck Typing

You can create an implementation of an interface using lambdas. NTypeForge generates custom delegates to support any signature, including those with `ref` or `out` parameters:

```csharp
var duck = Duck.Handler<ICalculator>().Create(
    Add: (a, b) => a + b
);
Console.WriteLine(duck.Add(5, 5));
```

For properties, provide getter and setter delegates:

```csharp
var name = "initial";
var duck = Duck.Handler<IWithName>().Create(
    get_Name: () => name,
    set_Name: (value) => name = value
);
```

## How it works

The source generator looks for calls to `.Duck<T>()` and `Duck.Handler<T>().Create()`. It then generates a internal proxy class in your project that implements the interface and delegates calls to the target object or provided delegates.

## Installation

Add the `NTypeForge.SourceGenerator` project to your solution and reference it as an analyzer in your project:

```xml
<ItemGroup>
  <ProjectReference Include="..\NTypeForge.SourceGenerator\NTypeForge.SourceGenerator.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

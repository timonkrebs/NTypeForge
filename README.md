# NTypeForge

**Structural (duck) typing for C#, generated at compile time.**

NTypeForge lets you pass a type to a method that expects an *interface* it doesn't
implement — as long as the type *structurally* has all the members the interface
requires. A [Roslyn](https://github.com/dotnet/roslyn) incremental source generator
spots these calls, generates a tiny wrapper, and rewires the call for you. No
reflection, no runtime type inspection, no hand-written adapters.

> **Status:** experimental. It relies on C# 14 extension members, which are still in
> preview. Treat it as a research project, not a production dependency.

---

## Contents

- [The problem it solves](#the-problem-it-solves)
- [Requirements](#requirements)
- [Installation](#installation)
- [Usage](#usage)
  - [1. Pass a structurally-matching type to a method](#1-pass-a-structurally-matching-type-to-a-method)
  - [2. Create a proxy explicitly with `Duck<T>()`](#2-create-a-proxy-explicitly-with-duckt)
  - [3. Get the original instance back](#3-get-the-original-instance-back)
- [Advanced types](#advanced-types)
- [How it works](#how-it-works)
- [Performance & overhead](#performance--overhead)
- [Limitations](#limitations)
- [Diagnostics](#diagnostics)

---

## The problem it solves

Two types can have the *exact same shape* yet be incompatible in C#, because the
type system is **nominal**: matching only happens through an explicit
`: IInterface` declaration.

```cs
public interface ICalculator
{
    float Calculate(float a, float b);
}

// Note: AddCalculator does NOT declare ': ICalculator'.
// It just happens to have a method with the same signature.
public class AddCalculator
{
    public float Calculate(float a, float b) => a + b;
}

public class CalculatorManager
{
    public float HandleCalculate(ICalculator calculator, float a, float b)
        => calculator.Calculate(a, b);
}
```

Without NTypeForge, this is a compile error (`CS1503` — cannot convert
`AddCalculator` to `ICalculator`):

```cs
var calculator = new AddCalculator();
var manager    = new CalculatorManager();

float result = manager.HandleCalculate(calculator, 10, 20); // ❌ does not compile
```

With NTypeForge referenced, **the same code compiles and runs** — the generator
sees that `AddCalculator` structurally satisfies `ICalculator` and bridges the gap
for you:

```cs
var calculator = new AddCalculator();
var manager    = new CalculatorManager();

float result = manager.HandleCalculate(calculator, 10, 20); // ✅ compiles
Console.WriteLine($"Result: {result}"); // Result: 30
```

---

## Requirements

| Requirement | Value |
| --- | --- |
| Target framework | **.NET 10** (`net10.0`) |
| Language version | **`preview`** (NTypeForge uses C# 14 extension members) |

In every project that *consumes* NTypeForge, set the language version to preview:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <LangVersion>preview</LangVersion>
</PropertyGroup>
```

---

## Installation

NTypeForge is not published to NuGet yet. Reference both projects directly. Note
the **two** references: the runtime library plus the source generator wired in as an
analyzer.

```xml
<ItemGroup>
  <!-- Runtime helpers: IProxy<T>, Duck<T>(), Unbox<T>() -->
  <ProjectReference Include="path/to/src/NTypeForge/NTypeForge.csproj" />

  <!-- The source generator (referenced as an analyzer, no runtime assembly) -->
  <ProjectReference Include="path/to/src/NTypeForge.SourceGenerator/NTypeForge.SourceGenerator.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

> The `OutputItemType="Analyzer"` and `ReferenceOutputAssembly="false"` attributes
> are required — they tell the compiler to *run* the generator rather than link
> against it.

---

## Usage

### 1. Pass a structurally-matching type to a method

This is the headline feature shown above: just call the method. If the argument's
type structurally matches the expected interface, NTypeForge generates an overload
that accepts the concrete type. Works for instance **and** `static` methods:

```cs
var calculator = new AddCalculator();

// HandleCalculateStatic(ICalculator, ...) — passing the bare AddCalculator just works.
float result = CalculatorManager.HandleCalculateStatic(calculator, 2, 3);
```

### 2. Create a proxy explicitly with `Duck<T>()`

When you want the interface value itself (to store it, return it, or pass it
around), call `Duck<T>()`. It returns a proxy that *is* a `T`:

```cs
using NTypeForge;

var calculator = new AddCalculator();

ICalculator asInterface = calculator.Duck<ICalculator>();

float result = asInterface.Calculate(10, 20); // 30
```

If the type doesn't structurally match `T`, you get a clear compile-time error
([NTF001](#diagnostics)) instead of a runtime surprise.

### 3. Get the original instance back

Every generated proxy implements `IProxy<T>`, so you can always recover the wrapped
instance. Use `Unbox<T>()` / `TryUnbox<T>()` (they walk nested proxies safely):

```cs
using NTypeForge;

var math      = new MyMath();
IMath proxy   = math.Duck<IMath>();

MyMath? back  = proxy.Unbox<MyMath>();          // returns the original, or null
bool ok       = proxy.TryUnbox<MyMath>(out var m); // explicit success flag

// Or inspect directly:
if (proxy is IProxy<MyMath> p)
    MyMath original = p.Inner;
```

NTypeForge also **prevents double-wrapping**: if you pass an existing proxy where a
*different* interface is expected, it unwraps to the original instance and re-wraps
once, rather than stacking a proxy on top of a proxy.

---

## Advanced types

The generator preserves full signature fidelity, including value types and
parameter-passing modifiers.

- ✅ `class`, `struct`, and `record` arguments
- ✅ `ref`, `out`, and `in` parameters
- ✅ Custom types as parameters and return values
- ✅ Interfaces that inherit from other interfaces (all inherited methods are proxied)

```cs
public struct Point { public int X, Y; }

public interface IGeometry
{
    void Move(ref Point point, int dx, int dy);
    void SetOrigin(out Point point);
}

public class MyGeometry // does not implement IGeometry
{
    public void Move(ref Point point, int dx, int dy) { /* ... */ }
    public void SetOrigin(out Point point)            { point = default; }
}

public class GeometryManager
{
    public void Transform(IGeometry geometry) { /* ... */ }
}

var manager = new GeometryManager();
manager.Transform(new MyGeometry()); // ✅ ref/out semantics preserved
```

---

## How it works

The generator is an `IIncrementalGenerator`. It scans for two things:

1. **Method calls that fail to bind** because an argument's type isn't the expected
   interface (but structurally matches it).
2. **Explicit `Duck<T>()` calls.**

For each match it emits two pieces of code:

**A proxy struct** that implements the target interface by forwarding to the wrapped
instance, plus `IProxy<T>` so the instance can be recovered:

```cs
internal readonly struct AddCalculator_ICalculator_Proxy
    : ICalculator, IProxy<AddCalculator>
{
    private readonly AddCalculator _instance;

    public AddCalculator_ICalculator_Proxy(AddCalculator instance) => _instance = instance;

    public AddCalculator Inner    => _instance;     // from IProxy<AddCalculator>
    object IProxy.Unwrapped       => _instance;     // from IProxy

    public float Calculate(float a, float b) => _instance.Calculate(a, b);
}
```

**A C# 14 extension member** that accepts the concrete type and forwards into the
original method, wrapping the argument in the proxy:

```cs
public static class CalculatorManager_DuckTypingExtensions
{
    extension (CalculatorManager target)
    {
        public float HandleCalculate(AddCalculator handler, float a, float b)
            => target.HandleCalculate(new AddCalculator_ICalculator_Proxy(handler), a, b);
    }
}
```

The generated pipeline is fully cacheable: each call site is reduced to a
value-equatable model (strings/enums only — no symbols held), so edits that don't
change any duck-typing site skip regeneration.

---

## Performance & overhead

NTypeForge avoids the usual cost of dynamic duck typing — **no reflection, no
`DynamicObject`, no expression-tree compilation, no per-call dispatch setup.** A
forwarding call is a direct method call through a one-field struct.

It is **not** strictly zero-allocation, though. The proxy is a `struct`, but the
moment it's passed where the target interface is expected, it gets **boxed once**
(a single small heap allocation per call). The win is structural: you pay one box,
not a reflection pipeline — and you keep full compile-time type checking.

---

## Limitations

NTypeForge proxies **non-generic methods only**. The following interface members are
**not** supported and will prevent a proxy from being generated:

- ❌ Properties and indexers
- ❌ Events
- ❌ Generic methods

An explicit `Duck<T>()` against such an interface reports
[NTF002](#diagnostics); an implicit call is simply left as the original (still
failing) call so the compiler's own error stands.

Structural matching considers a type's **directly declared** methods — members it
inherits from a base *class* are not counted toward the match.

---

## Diagnostics

| ID | Severity | Meaning |
| --- | --- | --- |
| **NTF001** | Error | `Duck<T>()` was used but the type does not structurally implement every member required by `T`. |
| **NTF002** | Error | The target interface contains a member NTypeForge cannot proxy (a property, event, or generic method). |

---

## About

Experiments in zero-reflection, compile-time structural duck typing for C# 14.
See [`LICENSE`](LICENSE) for licensing.

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
- [Supported members](#supported-members)
- [How it works](#how-it-works)
- [Runtime overhead](#runtime-overhead)
- [Compile-time implications](#compile-time-implications)
- [Limitations](#limitations)
- [Diagnostics](#diagnostics)
- [Development](#development)
- [About](#about)

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

If a call needs *several* arguments bridged at once, they are all ducked together —
the generated overload replaces every duck-typed parameter, and each argument gets
its own proxy:

```cs
// HandleBoth(ICalculator calculator, ILogger logger, float a, float b)
manager.HandleBoth(new AddCalculator(), new ConsoleLogger(), 10, 20); // ✅ both arguments ducked
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

If the instance *already* satisfies `T` — nominally or through variance — no proxy is
generated at all: `Duck<T>()` hands back the same instance unchanged (so
`asInterface is IProxy<T>` is `false`). A proxy is created only when one is actually
needed to bridge the gap.

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
once, rather than stacking a proxy on top of a proxy. This applies when the proxy's
underlying concrete type is known to the current compilation (the common case — it was
ducked in the same assembly). A proxy created in *another* assembly may get
double-wrapped, but `Unbox<T>()` / `TryUnbox<T>()` still walk the whole chain back to
the original instance — only a single-level `IProxy<T>.Inner` would observe the
intermediate proxy.

---

## Supported members

The generator proxies the full range of interface members, preserving signature
fidelity including value types and parameter-passing modifiers.

| Member kind | Supported |
| --- | --- |
| Methods (incl. `ref`/`out`/`in` params, custom types) | ✅ |
| Properties (get/set, get-only, `init`-only, write-only) | ✅ |
| Indexers (single- and multi-parameter) | ✅ |
| Events | ✅ |
| Generic methods (incl. constraints, multiple type parameters) | ✅ |
| Interfaces inheriting from other interfaces (all inherited members proxied) | ✅ |
| `class`, `struct`, and `record` arguments | ✅ |

```cs
public struct Point { public int X, Y; }

public interface IWidget
{
    string Title { get; set; }            // property
    int this[int index] { get; }          // indexer
    event Action? Clicked;                // event
    void Move(ref Point point, int dx);   // ref/out/in preserved
    T Echo<T>(T value) where T : class;   // generic method
}

public class MyWidget // does not implement IWidget
{
    public string Title { get; set; } = "";
    public int this[int index] => index * 2;
    public event Action? Clicked;
    public void Move(ref Point point, int dx) { /* ... */ }
    public T Echo<T>(T value) where T : class => value;
}

IWidget widget = new MyWidget().Duck<IWidget>(); // ✅ every member is forwarded
```

**Accessor fidelity.** Each property/indexer accessor is matched independently and
forwarded with the interface's accessor kind. A `public int V { get; private set; }`
satisfies a `{ get; }` interface but not `{ get; set; }` (the private setter isn't
callable). An `init`-only interface property is implemented with `init` — and an
`init`-only *underlying* member is treated as get-only, since the proxy wraps an
already-constructed instance and could never assign it.

**Inherited members.** A concrete type satisfies a requirement with a matching public
instance member whether it *declares* that member or *inherits* it from a base
`class` — the proxy forwards inherited members just fine. (On the interface side,
likewise, a target interface requires every member it inherits from its base
interfaces.)

---

## How it works

The generator is an `IIncrementalGenerator`. It scans for two things:

1. **Method calls that fail to bind** because an argument's type isn't the expected
   interface (but structurally matches it).
2. **Explicit `Duck<T>()` calls.**

For each match it emits two pieces of code:

**A proxy class** that implements the target interface by forwarding to the wrapped
instance, plus `IProxy<T>` so the instance can be recovered. `IProxy` is implemented
*explicitly*, so it never collides with an interface member that happens to be named
`Inner` or `Unwrapped` (the real type name also carries a stable hash suffix for
uniqueness, elided here):

```cs
internal sealed class AddCalculator_ICalculator_Proxy
    : ICalculator, IProxy<AddCalculator>
{
    // Not readonly: a struct underlying must stay mutable so settable members work.
    private AddCalculator __ntf_instance;

    public AddCalculator_ICalculator_Proxy(AddCalculator instance) => __ntf_instance = instance;

    AddCalculator IProxy<AddCalculator>.Inner => __ntf_instance;  // from IProxy<AddCalculator>
    object IProxy.Unwrapped                   => __ntf_instance;  // from IProxy

    public float Calculate(float a, float b) => __ntf_instance.Calculate(a, b);
}
```

It's a `class`, not a `struct`: a struct proxy would be boxed the instant it's passed
as the interface (allocating anyway), and a *readonly* field would make a `struct`
underlying impossible to mutate — see [Runtime overhead](#runtime-overhead).

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

The names above are simplified: the real proxy and extension-class names carry a
stable hash suffix for uniqueness. And when the ducked argument's static type is an
*interface* (rather than a concrete type as here), the forwarding method first emits
`TryUnbox` branches to unwrap an existing proxy before re-wrapping it. When several
arguments are ducked in the same call, the one forwarding method replaces every
duck-typed parameter and wraps each argument in its own proxy.

The generated pipeline is fully cacheable: each call site is reduced to a
value-equatable model (strings/enums only — no symbols held), so edits that don't
change any duck-typing site skip regeneration.

---

## Runtime overhead

NTypeForge avoids the usual cost of dynamic duck typing — **no reflection, no
`DynamicObject`, no expression-tree compilation, no per-call dispatch setup.** A
forwarding call is a direct method call through a one-field proxy.

It is **not** zero-allocation, though. The proxy is a small `class` — an object header
plus a single field referencing the wrapped instance (for a `struct` underlying the
value is copied inline into that field). Each *duck conversion* therefore costs **one
heap allocation**: one per implicit-forwarding call site, or one per `Duck<T>()` call.
Calls made *on* an already-created proxy are allocation-free direct forwards. The win
is structural: you pay one tiny object, not a reflection pipeline — and you keep full
compile-time type checking. (An earlier design used a `struct` proxy, but it was boxed
the instant it was passed as the interface — so it allocated anyway, while making a
*struct* underlying impossible to proxy correctly.)

## Compile-time implications

NTypeForge does its work *during compilation*, so the cost it adds is a
**build-time** cost, not a runtime one. There are two parts: (1) running the
generator, and (2) the compiler then having to bind and emit the code it produced.

The table below compiles one fixed program three ways. The source is **identical**
across all rows — it uses `Duck<T>()`, which binds to the [runtime
fallback](src/NTypeForge/DuckExtensions.cs) when the generator is absent and to a
generated proxy when it is present — so the only variable is whether NTypeForge
participates. `Sites` is the number of distinct `Duck<T>()` call sites (each adds
one interface, one class, one generated proxy, and one generated extension).

| Phase | 10 sites | 50 sites | 100 sites |
| --- | ---: | ---: | ---: |
| Compile only — **NTypeForge OFF** (baseline) | 16 ms | 78 ms | 106 ms |
| Generator pass only (no emit) | 7 ms | 183 ms | 577 ms |
| Full compile — **NTypeForge ON** | 461 ms | 2,341 ms | 4,875 ms |

<sub>BenchmarkDotNet 0.14.0, .NET 10.0.4, Roslyn 5.0.0, in a 2-core (1 physical) Linux container — a deliberately
constrained box, so treat the **absolute** numbers as worst-case and the **shape** as the takeaway.
`ShortRun` (3 iterations); small-`Sites` rows are noisy. Reproduce with
`dotnet run -c Release --project bench/NTypeForge.Benchmarks`.</sub>

**What this shows:**

- **Running the generator is cheap** (~6 ms per site). It resolves each call site
  into a small value-equatable model and emits text; that is the small middle row.
- **Compiling the generated code dominates.** Most of the "ON" time is the compiler
  binding and emitting the generated proxies and — chiefly — the C# 14 `extension`
  blocks, which are a preview feature that isn't optimized yet. On this hardware
  that is roughly **40 ms of added compile time per duck-typing site**, scaling
  about **linearly** with the number of sites.
- So a handful of duck sites is negligible; **heavy, pervasive use** (hundreds of
  sites) adds noticeable seconds to a *clean* build. As the `extension`-member
  feature matures toward release, this cost should fall.

**Incrementality.** The generator pipeline is fully cacheable: each call site is
reduced to a value-equatable model holding no symbols, so on an edit that doesn't
change any duck-typing site the generator's transform is reused and the IDE stays
responsive. Codegen additionally compares those models *ignoring source location*,
so an edit that merely moves a duck site (new lines above it, reformatting)
re-reports its diagnostics at the fresh position without re-emitting a single
generated file. Caching reuses the generator *output*; it does not remove the
compiler's cost of binding that output on a rebuild.

**Cancellation.** In an IDE, Roslyn re-runs the generator as you type and *cancels*
the in-flight pass the moment a newer keystroke makes its result obsolete. .NET
cancellation is cooperative — a token does nothing unless the running code observes
it — so the pipeline threads the driver's `CancellationToken` through its semantic
queries (a `GetSymbolInfo`/`GetTypeInfo` bind aborts mid-flight) and checks it
between emitted files. A stale pass thus stops in microseconds instead of running to
completion and competing for the same threads that serve completion lists and
squiggles. Type `foo.B`, `foo.Ba`, `foo.Bar` quickly and the first two passes die
almost free. This costs nothing at build time — on a plain `dotnet build` the token
essentially never fires — and is invisible in the benchmarks above, which by
construction only measure runs that complete.

---

## Limitations

- **Public members only.** Structural matching counts a type's **public** members.
  A `private`/`protected`/`internal` member (or accessor) can't be forwarded by the
  proxy, so it never counts toward a match.
- **Some interface members can't be proxied.** A `static abstract` member can't be
  implemented by an instance proxy, so such an interface can't be proxied (a `static`
  member *with* a default implementation is fine — it's supplied by the interface
  itself). The same goes for a member that returns *by `ref`*, or whose signature
  involves a *pointer* / *function-pointer* type. Note this is the `ref`-**return**
  case: `ref`/`out`/`in` **parameters** are fully supported. On a `Duck<T>()` call any
  of these reports [NTF002](#diagnostics).
- **`init`-only underlying members are effectively read-only.** A proxy wraps an
  already-constructed instance, so it can satisfy a settable interface requirement
  only when the underlying setter is a regular `set`.
- **`struct` underlyings are wrapped by value.** A proxy over a struct holds a
  *copy* of it, so mutations made through the proxy are visible on the proxy but do
  **not** propagate back to the original variable (ordinary C# value semantics).
  State stays consistent for the proxy's own lifetime.
- **`ref struct` underlyings are not supported.** A `ref struct` can't be wrapped by
  the proxy (it can't be a field of the proxy, a type argument, or boxed), so such a
  site is left alone and the compiler's own error stands.

When `Duck<T>()` targets an interface with an unsupported member it reports
[NTF002](#diagnostics). An implicit (non-`Duck`) call is left as the original
(still-failing) call so the compiler's own error stands — except at a
high-confidence near-miss, where it emits the [NTF003](#diagnostics) warning to
explain why duck typing didn't kick in.

---

## Diagnostics

| ID | Severity | Meaning |
| --- | --- | --- |
| **NTF001** | Error | `Duck<T>()` was used but the type does not structurally implement every member required by `T`. |
| **NTF002** | Error | A `Duck<T>()` target interface contains a member NTypeForge cannot proxy (e.g. a `static abstract` member). |
| **NTF003** | Warning | An implicit call's argument structurally matches the parameter interface *except* for an unsupported member, so NTypeForge couldn't bridge it — surfaced to explain why the call still fails, without masking the compiler's own error. |

---

## Development

Build and test with the standard SDK commands against `NTypeForge.slnx`:

```bash
dotnet build
dotnet test
```

### Cognitive Complexity gate

The repo ships an **opt-in** [SonarSource Cognitive Complexity](https://www.sonarsource.com/docs/CognitiveComplexity.pdf)
check (rule `S3776`, per-method threshold **15**). Normal builds are unaffected; to
measure it locally:

```bash
dotnet build -p:MeasureCognitiveComplexity=true
```

This enables `SonarAnalyzer.CSharp` with only `S3776` (see
[`Directory.Build.props`](Directory.Build.props) and [`SonarLint.xml`](SonarLint.xml)).
CI runs it as a separate `cognitive-complexity` job that escalates the warning to an
error, so any method above 15 fails the build.

### Benchmarks

[`bench/NTypeForge.Benchmarks`](bench/NTypeForge.Benchmarks) measures the compile-time
cost reported above:

```bash
dotnet run -c Release --project bench/NTypeForge.Benchmarks
```

---

## About

Experiments in zero-reflection, compile-time structural duck typing for C# 14.
See [`LICENSE`](LICENSE) for licensing.

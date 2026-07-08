## 2025-06-09 - [Principle of Least Privilege] Generate Internal Extensions for Internal Types
**Vulnerability:** Source Generator `NTypeForge.SourceGenerator` was always emitting `public static class` for its duck-typing extensions. If the type being ducked (`target`) was `internal`, this created a `CS0051: Inconsistent accessibility` compiler error and essentially violated the principle of least privilege, potentially leaking internal types into public scope.
**Learning:** Source Generators creating types meant to be used alongside user code should dynamically match the accessibility of their targets (or default to `internal` if appropriate for their use-case) to prevent scope leakage.
**Prevention:** Check `DeclaredAccessibility == Accessibility.Public` (or effective accessibility for nested types) on the target type, and emit `internal static class` instead of `public static class` when the target is not public.

## 2026-07-08 - [Fail Securely] Validate public API inputs explicitly
**Vulnerability:** The public API `Duck<T>(this object instance)` did not validate its `instance` input for null. If null was passed, it would bypass the type check and throw a misleading `InvalidOperationException` intended for missing generator proxies. This obscures root causes and could potentially lead to unexpected application state.
**Learning:** Relying on downstream operations (or subsequent logic) to throw generic exceptions on null input is unsafe and confusing. Always validate input immediately at the entry point of a public API.
**Prevention:** Use `ArgumentNullException.ThrowIfNull()` at the very start of public-facing API methods to fail fast, securely, and with accurate error information.

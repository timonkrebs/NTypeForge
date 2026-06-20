; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
NTF001 | NTypeForge | Error | No structural match for Duck<T>.
NTF002 | NTypeForge | Error | Unsupported interface member for duck typing.
NTF003 | NTypeForge | Warning | Type structurally matches but cannot be implicitly duck-typed.
NTF004 | NTypeForge | Warning | Type structurally matches but cannot be implicitly duck-typed through a ref/out/in parameter.

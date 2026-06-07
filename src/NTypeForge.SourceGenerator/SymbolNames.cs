using Microsoft.CodeAnalysis;

namespace NTypeForge.SourceGenerator
{
    // Pure ITypeSymbol -> string naming helpers used across the analysis stage.
    internal static class SymbolNames
    {
        public static string Fq(ITypeSymbol type)
            => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        public static string MinimalName(ITypeSymbol type)
            => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        // The namespace a generated artifact for `type` is emitted into (global-namespace types go
        // to NTypeForge).
        public static string NamespaceOf(ITypeSymbol type)
            => type.ContainingNamespace.IsGlobalNamespace ? "NTypeForge" : type.ContainingNamespace.ToDisplayString();

        public static int BaseTypeDepth(ITypeSymbol type)
        {
            int depth = 0;
            for (var b = type.BaseType; b != null; b = b.BaseType) depth++;
            return depth;
        }
    }
}

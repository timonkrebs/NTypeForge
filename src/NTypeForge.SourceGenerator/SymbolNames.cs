using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace NTypeForge.SourceGenerator
{
    // Pure ITypeSymbol -> string naming helpers used across the analysis stage.
    internal static class SymbolNames
    {
        // Prefix a user-derived identifier with `@` when it is a reserved C# keyword (a member or
        // parameter literally named `int`, `event`, `return`, ...). Symbol.Name returns the bare
        // word, so the generated code would not parse without this.
        public static string Escape(string identifier)
            => SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ? "@" + identifier : identifier;

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

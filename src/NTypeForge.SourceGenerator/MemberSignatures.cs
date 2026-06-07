using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using NTypeForge.SourceGenerator.Models;

namespace NTypeForge.SourceGenerator
{
    // Converts Roslyn symbols into the render-ready *Sig carriers, including the canonical match
    // keys. The only place that knows how a symbol becomes a signature (and, for generic methods,
    // how type parameters are normalized in the keys).
    internal static class MemberSignatures
    {
        public static ParamSig ToParamSig(IParameterSymbol p)
            => new ParamSig(SymbolNames.Fq(p.Type), p.RefKind, p.Name);

        public static MethodSig ToMethodSig(IMethodSymbol m)
        {
            var parameters = m.Parameters.Select(ToParamSig).ToList();
            var arity = m.Arity;

            string dedupKey, compatKey;
            if (arity == 0)
            {
                // Non-generic: keys are byte-identical to the pre-generics behaviour (Fq-based).
                dedupKey = $"{m.Name}({ParamSig.Shape(parameters)})";
                compatKey = $"{SymbolNames.Fq(m.ReturnType)} {dedupKey}";
            }
            else
            {
                // Generic: normalize the method's own type parameters to positional tokens and fold
                // in constraints. Two structurally identical generic methods then match regardless of
                // type-parameter names, and a concrete whose constraints differ from the interface
                // does NOT match (its forwarding call `_instance.M<T>(...)` wouldn't compile).
                var paramKey = string.Join(",", m.Parameters.Select(p => $"{p.RefKind}:{NormalizeTypeKey(p.Type)}"));
                dedupKey = $"{m.Name}`{arity}({paramKey}){NormalizeConstraintKey(m.TypeParameters)}";
                compatKey = $"{NormalizeTypeKey(m.ReturnType)} {dedupKey}";
            }

            return new MethodSig(
                m.Name,
                SymbolNames.Fq(m.ReturnType),
                m.ReturnType.SpecialType == SpecialType.System_Void,
                parameters,
                arity,
                m.TypeParameters.Select(tp => tp.Name).ToList(),
                m.TypeParameters.Select(GetConstraints).ToList(),
                dedupKey,
                compatKey);
        }

        public static PropertySig ToPropertySig(IPropertySymbol p)
            => new PropertySig(p.Name, SymbolNames.Fq(p.Type), p.GetMethod != null, p.SetMethod != null, p.SetMethod is { IsInitOnly: true });

        public static IndexerSig ToIndexerSig(IPropertySymbol p)
            => new IndexerSig(SymbolNames.Fq(p.Type), p.Parameters.Select(ToParamSig).ToList(), p.GetMethod != null, p.SetMethod != null);

        public static EventSig ToEventSig(IEventSymbol e)
            => new EventSig(e.Name, SymbolNames.Fq(e.Type));

        private static readonly SymbolDisplayFormat FqNoGenerics =
            SymbolDisplayFormat.FullyQualifiedFormat.WithGenericsOptions(SymbolDisplayGenericsOptions.None);

        // A type rendered for use in a *match key*, with the enclosing method's own type parameters
        // replaced by positional tokens (``0, ``1, ...) so two generic methods that differ only by
        // type-parameter name produce the same key. Plain (non-method-type-parameter) types keep
        // their fully-qualified form, recursing through arrays and constructed generics.
        private static string NormalizeTypeKey(ITypeSymbol type)
        {
            if (type is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } tp)
                return $"``{tp.Ordinal}";
            if (type is IArrayTypeSymbol array)
                return $"{NormalizeTypeKey(array.ElementType)}[{new string(',', array.Rank - 1)}]";
            if (type is INamedTypeSymbol { IsGenericType: true } named)
                return $"{named.ToDisplayString(FqNoGenerics)}<{string.Join(",", named.TypeArguments.Select(NormalizeTypeKey))}>";
            return SymbolNames.Fq(type);
        }

        // The ordered constraint tokens for one type parameter. `formatType` controls how constraint
        // types are rendered: Fq for emitted `where` clauses, NormalizeTypeKey for match keys.
        private static List<string> ConstraintList(ITypeParameterSymbol tp, Func<ITypeSymbol, string> formatType)
        {
            var constraints = new List<string>();
            if (tp.HasReferenceTypeConstraint) constraints.Add("class");
            if (tp.HasValueTypeConstraint) constraints.Add("struct");
            if (tp.HasUnmanagedTypeConstraint) constraints.Add("unmanaged");
            if (tp.HasNotNullConstraint) constraints.Add("notnull");
            foreach (var t in tp.ConstraintTypes) constraints.Add(formatType(t));
            if (tp.HasConstructorConstraint) constraints.Add("new()");
            return constraints;
        }

        private static string GetConstraints(ITypeParameterSymbol tp)
        {
            var constraints = ConstraintList(tp, SymbolNames.Fq);
            return constraints.Count > 0 ? $"where {tp.Name} : {string.Join(", ", constraints)}" : "";
        }

        private static string NormalizeConstraintKey(IEnumerable<ITypeParameterSymbol> typeParameters)
        {
            var parts = new List<string>();
            foreach (var tp in typeParameters)
            {
                var constraints = ConstraintList(tp, NormalizeTypeKey);
                if (constraints.Count > 0) parts.Add($"`{tp.Ordinal}:{string.Join(",", constraints)}");
            }
            return parts.Count > 0 ? $"|{string.Join("|", parts)}" : "";
        }
    }
}

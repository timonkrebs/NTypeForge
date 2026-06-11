using System;
using System.Collections.Generic;
using System.Globalization;
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
            => new ParamSig(
                SymbolNames.Fq(p.Type),
                p.RefKind,
                p.Name,
                p.IsOptional,
                p.IsParams,
                p.IsOptional ? DefaultValueSource(p) : null);

        public static MethodSig ToMethodSig(IMethodSymbol m)
        {
            var parameters = m.Parameters.Select(ToParamSig).ToList();
            var dedupKey = MethodDedupKey(m, FqDirect);
            var compatKey = MethodCompatKey(m, dedupKey, FqDirect);

            return new MethodSig(
                m.Name,
                SymbolNames.Fq(m.ReturnType),
                m.ReturnType.SpecialType == SpecialType.System_Void,
                parameters,
                m.Arity,
                m.TypeParameters.Select(tp => SymbolNames.Escape(tp.Name)).ToList(),
                m.TypeParameters.Select(GetConstraints).ToList(),
                dedupKey,
                compatKey);
        }

        // Cached delegate for the un-memoized Fq path, so the key builders below don't allocate a
        // fresh method-group conversion per call.
        private static readonly Func<ITypeSymbol, string> FqDirect = SymbolNames.Fq;

        // Key-only fast path for the concrete-surface scan: the same CompatKey as
        // ToMethodSig(m).CompatKey, without materializing the render-ready MethodSig (parameter
        // carriers incl. default-value rendering, type-parameter and constraint lists - the
        // surface side only ever reads the key). `fq` lets the caller memoize ToDisplayString,
        // the dominant cost of a surface scan.
        public static string MethodCompatKey(IMethodSymbol m, Func<ITypeSymbol, string> fq)
            => MethodCompatKey(m, MethodDedupKey(m, fq), fq);

        private static string MethodCompatKey(IMethodSymbol m, string dedupKey, Func<ITypeSymbol, string> fq)
            => $"{(m.Arity == 0 ? fq(m.ReturnType) : NormalizeTypeKey(m.ReturnType))} {dedupKey}";

        private static string MethodDedupKey(IMethodSymbol m, Func<ITypeSymbol, string> fq)
        {
            if (m.Arity == 0)
            {
                // Non-generic: keys are byte-identical to the pre-generics behaviour (Fq-based).
                return $"{m.Name}({ParameterShape(m.Parameters, fq)})";
            }

            // Generic: normalize the method's own type parameters to positional tokens and fold
            // in constraints. Two structurally identical generic methods then match regardless of
            // type-parameter names, and a concrete whose constraints differ from the interface
            // does NOT match (its forwarding call `_instance.M<T>(...)` wouldn't compile).
            var paramKey = string.Join(",", m.Parameters.Select(p => $"{p.RefKind}:{NormalizeTypeKey(p.Type)}"));
            return $"{m.Name}`{m.Arity}({paramKey}){NormalizeConstraintKey(m.TypeParameters)}";
        }

        // The canonical parameter-shape encoding built straight from symbols; byte-identical to
        // ParamSig.Shape over the corresponding ToParamSig results.
        public static string ParameterShape(IEnumerable<IParameterSymbol> parameters, Func<ITypeSymbol, string> fq)
            => string.Join(",", parameters.Select(p => $"{p.RefKind}:{fq(p.Type)}"));

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
            if (tp.HasUnmanagedTypeConstraint) constraints.Add("unmanaged");
            else if (tp.HasValueTypeConstraint) constraints.Add("struct");
            if (tp.HasReferenceTypeConstraint) constraints.Add("class");
            if (tp.HasNotNullConstraint) constraints.Add("notnull");
            foreach (var t in tp.ConstraintTypes) constraints.Add(formatType(t));
            if (tp.HasConstructorConstraint) constraints.Add("new()");
            return constraints;
        }

        private static string GetConstraints(ITypeParameterSymbol tp)
        {
            var constraints = ConstraintList(tp, SymbolNames.Fq);
            return constraints.Count > 0 ? $"where {SymbolNames.Escape(tp.Name)} : {string.Join(", ", constraints)}" : "";
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

        private static string DefaultValueSource(IParameterSymbol p)
        {
            if (!p.HasExplicitDefaultValue) return "default";

            var value = p.ExplicitDefaultValue;
            if (value == null) return "null";
            if (p.Type.TypeKind == TypeKind.Enum)
            {
                return $"({SymbolNames.Fq(p.Type)}){EnumDefaultValue(value, (INamedTypeSymbol)p.Type)}";
            }

            switch (value)
            {
                case bool b: return b ? "true" : "false";
                case char c: return CharLiteral(c);
                case string s: return StringLiteral(s);
                case float f: return f.ToString("R", CultureInfo.InvariantCulture) + "f";
                case double d: return d.ToString("R", CultureInfo.InvariantCulture) + "d";
                case decimal m: return m.ToString(CultureInfo.InvariantCulture) + "m";
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "default";
                default:
                    return "default";
            }
        }

        private static string StringLiteral(string value)
        {
            var chars = value.Select(EscapeChar);
            return $"\"{string.Concat(chars)}\"";
        }

        private static string CharLiteral(char value)
            => $"'{EscapeChar(value)}'";

        private static string EscapeChar(char value)
            => value switch
            {
                '\'' => "\\'",
                '"' => "\\\"",
                '\\' => "\\\\",
                '\0' => "\\0",
                '\a' => "\\a",
                '\b' => "\\b",
                '\f' => "\\f",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                '\v' => "\\v",
                _ when char.IsControl(value) => "\\u" + ((int)value).ToString("x4", CultureInfo.InvariantCulture),
                _ => value.ToString()
            };

        private static string EnumDefaultValue(object value, INamedTypeSymbol enumType)
        {
            var underlying = enumType.EnumUnderlyingType?.SpecialType;
            return underlying == SpecialType.System_UInt64
                ? Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
                : Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }
    }
}

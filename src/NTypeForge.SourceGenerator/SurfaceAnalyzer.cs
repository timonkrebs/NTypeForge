using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using NTypeForge.SourceGenerator.Models;

namespace NTypeForge.SourceGenerator
{
    // Determines what a concrete type structurally provides (the concrete side of matching): the set
    // of requirement CompatKeys its directly-declared public members can satisfy.
    internal static class SurfaceAnalyzer
    {
        public static IReadOnlyList<string> BuildSurfaceCompatKeys(ITypeSymbol type)
        {
            // ToDisplayString dominates the cost of a surface scan and the same types recur
            // constantly across members (parameter, return, and property types), so memoize it
            // for the duration of one scan. The cache is method-local: no symbol outlives the
            // transform that requested the scan.
            var fqCache = new Dictionary<ITypeSymbol, string>(SymbolEqualityComparer.Default);
            Func<ITypeSymbol, string> fq = t =>
            {
                if (!fqCache.TryGetValue(t, out var name))
                {
                    name = SymbolNames.Fq(t);
                    fqCache[t] = name;
                }
                return name;
            };

            // Inline first-occurrence dedup (the same order Distinct() preserved before).
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            for (ITypeSymbol? current = type; current != null; current = current.BaseType)
            {
                foreach (var member in current.GetMembers())
                {
                    foreach (var key in SurfaceKeysForMember(member, fq))
                    {
                        if (seen.Add(key)) result.Add(key);
                    }
                }
            }
            return result;
        }

        // Every requirement key the member can satisfy on a concrete surface (a member may
        // satisfy several - e.g. a get/set property also satisfies a get-only requirement).
        // Only public members count: the proxy forwards `_instance.Member`, which would not
        // compile against a private/protected/internal member (CS0122/CS0272), so a non-public
        // member must never make the type appear to structurally match.
        // Methods read their key off ToMethodSig(method).CompatKey, so the method-key formula lives
        // in one place (it must normalize generic type parameters); properties, indexers, and events
        // enumerate keys via the static CompatKeyFor helpers, since one member can satisfy several
        // requirement keys at once (the get/set property above).
        private static IEnumerable<string> SurfaceKeysForMember(ISymbol member, Func<ITypeSymbol, string> fq)
        {
            // Static members are excluded: a proxy forwards `_instance.Member`, which does not
            // compile against a static member (CS0176). The requirement side excludes statics too,
            // so a static member must never make a type appear to structurally match.
            switch (member)
            {
                case IMethodSymbol { MethodKind: MethodKind.Ordinary, DeclaredAccessibility: Accessibility.Public, IsStatic: false } method:
                    return new[] { MemberSignatures.ToMethodSig(method).CompatKey };
                case IPropertySymbol { IsIndexer: true, IsStatic: false } indexer:
                    return IndexerSurfaceKeys(indexer, fq);
                case IPropertySymbol { IsStatic: false } prop:
                    return PropertySurfaceKeys(prop, fq);
                case IEventSymbol { DeclaredAccessibility: Accessibility.Public, IsStatic: false } evt:
                    return new[] { EventSig.CompatKeyFor(evt.Name, fq(evt.Type)) };
                default:
                    return Array.Empty<string>();
            }
        }

        private static IEnumerable<string> IndexerSurfaceKeys(IPropertySymbol indexer, Func<ITypeSymbol, string> fq)
        {
            var typeFq = fq(indexer.Type);
            var shape = ParamSig.Shape(indexer.Parameters.Select(MemberSignatures.ToParamSig).ToList());
            var canGet = indexer.GetMethod is { DeclaredAccessibility: Accessibility.Public };
            var canSet = indexer.SetMethod is { DeclaredAccessibility: Accessibility.Public };
            if (canGet) yield return IndexerSig.CompatKeyFor(typeFq, shape, true, false);
            if (canSet) yield return IndexerSig.CompatKeyFor(typeFq, shape, false, true);
            if (canGet && canSet) yield return IndexerSig.CompatKeyFor(typeFq, shape, true, true);
        }

        private static IEnumerable<string> PropertySurfaceKeys(IPropertySymbol prop, Func<ITypeSymbol, string> fq)
        {
            var name = prop.Name;
            var typeFq = fq(prop.Type);
            // Each accessor is judged independently: `public int V { get; private set; }` exposes a
            // public getter but no usable setter, so it satisfies a `{ get; }` requirement but not
            // `{ get; set; }`.
            var canGet = prop.GetMethod is { DeclaredAccessibility: Accessibility.Public };
            // A regular `set` can forward both `set` and `init` requirements; an init-only setter
            // can forward neither (the proxy wraps an already-constructed instance, so
            // `_instance.X = value` is illegal - CS8852). Advertise the writable capability only for
            // a public non-init setter, so an init-only or non-public underlying is treated as
            // effectively get-only and never matched to a requirement it cannot fulfill.
            var canSet = prop.SetMethod is { IsInitOnly: false, DeclaredAccessibility: Accessibility.Public };
            if (canGet) yield return PropertySig.CompatKeyFor(name, typeFq, true, false, false);
            if (canSet)
            {
                yield return PropertySig.CompatKeyFor(name, typeFq, false, true, false);
                yield return PropertySig.CompatKeyFor(name, typeFq, false, true, true);
            }
            if (canGet && canSet)
            {
                yield return PropertySig.CompatKeyFor(name, typeFq, true, true, false);
                yield return PropertySig.CompatKeyFor(name, typeFq, true, true, true);
            }
        }
    }
}

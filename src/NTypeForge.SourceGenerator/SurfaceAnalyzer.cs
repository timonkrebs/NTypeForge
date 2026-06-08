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
            var result = new List<string>();
            for (ITypeSymbol? current = type; current != null; current = current.BaseType)
            {
                foreach (var member in current.GetMembers())
                {
                    result.AddRange(SurfaceKeysForMember(member));
                }
            }
            return result.Distinct(StringComparer.Ordinal).ToList();
        }

        // Every requirement key the member can satisfy on a concrete surface (a member may
        // satisfy several - e.g. a get/set property also satisfies a get-only requirement).
        // Only public members count: the proxy forwards `_instance.Member`, which would not
        // compile against a private/protected/internal member (CS0122/CS0272), so a non-public
        // member must never make the type appear to structurally match.
        private static IEnumerable<string> SurfaceKeysForMember(ISymbol member)
        {
            // Static members are excluded: a proxy forwards `_instance.Member`, which does not
            // compile against a static member (CS0176). The requirement side excludes statics too,
            // so a static member must never make a type appear to structurally match.
            switch (member)
            {
                case IMethodSymbol { MethodKind: MethodKind.Ordinary, DeclaredAccessibility: Accessibility.Public, IsStatic: false } method:
                    return new[] { MemberSignatures.ToMethodSig(method).CompatKey };
                case IPropertySymbol { IsIndexer: true, IsStatic: false } indexer:
                    return IndexerSurfaceKeys(indexer);
                case IPropertySymbol { IsStatic: false } prop:
                    return PropertySurfaceKeys(prop);
                case IEventSymbol { DeclaredAccessibility: Accessibility.Public, IsStatic: false } evt:
                    return new[] { MemberSignatures.ToEventSig(evt).CompatKey };
                default:
                    return Array.Empty<string>();
            }
        }

        private static IEnumerable<string> IndexerSurfaceKeys(IPropertySymbol indexer)
        {
            var typeFq = SymbolNames.Fq(indexer.Type);
            var parameters = indexer.Parameters.Select(MemberSignatures.ToParamSig).ToList();
            var canGet = indexer.GetMethod is { DeclaredAccessibility: Accessibility.Public };
            var canSet = indexer.SetMethod is { DeclaredAccessibility: Accessibility.Public };
            if (canGet) yield return IndexerSig.CompatKeyFor(typeFq, parameters, true, false);
            if (canSet) yield return IndexerSig.CompatKeyFor(typeFq, parameters, false, true);
            if (canGet && canSet) yield return IndexerSig.CompatKeyFor(typeFq, parameters, true, true);
        }

        private static IEnumerable<string> PropertySurfaceKeys(IPropertySymbol prop)
        {
            var name = prop.Name;
            var typeFq = SymbolNames.Fq(prop.Type);
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

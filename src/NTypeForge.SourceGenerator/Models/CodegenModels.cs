using System;
using System.Collections.Generic;
using System.Linq;

namespace NTypeForge.SourceGenerator.Models
{
    // Structural matching: a type satisfies a set of interface requirements when every requirement's
    // CompatKey is present in the type's surface key set. Shared by the self-match check
    // (CandidateAnalyzer) and the cross-type match map (DuckTypingGenerator) so the two cannot drift.
    internal static class StructuralMatch
    {
        public static bool IsSatisfiedBy(
            IReadOnlyList<MethodSig> methods,
            IReadOnlyList<PropertySig> properties,
            IReadOnlyList<IndexerSig> indexers,
            IReadOnlyList<EventSig> events,
            ISet<string> surfaceKeys)
            => methods.All(r => surfaceKeys.Contains(r.CompatKey))
               && properties.All(r => surfaceKeys.Contains(r.CompatKey))
               && indexers.All(r => surfaceKeys.Contains(r.CompatKey))
               && events.All(r => surfaceKeys.Contains(r.CompatKey));
    }

    // The structural requirements of one interface in render-ready primitive form. Built once per
    // interface and consumed by both structural matching (ConcreteSatisfies) and proxy emission.
    internal sealed class InterfaceInfo
    {
        public string Fq = "";
        public string MinimalName = "";
        public IReadOnlyList<MethodSig> MethodRequirements = Array.Empty<MethodSig>();
        public IReadOnlyList<PropertySig> PropertyRequirements = Array.Empty<PropertySig>();
        public IReadOnlyList<IndexerSig> IndexerRequirements = Array.Empty<IndexerSig>();
        public IReadOnlyList<EventSig> EventRequirements = Array.Empty<EventSig>();
    }

    // A concrete type that can back a proxy, with the surface keys used to match it against
    // interface requirements.
    internal sealed class ConcreteInfo
    {
        public string Fq = "";
        public string Namespace = "";
        public string MinimalName = "";
        public int BaseDepth;
        public HashSet<string> SurfaceKeys = new HashSet<string>(StringComparer.Ordinal);
    }

    // One proxy struct to emit: an underlying type adapted to an interface, plus the members to
    // forward.
    internal readonly struct ProxyDecl
    {
        public readonly string UnderlyingFq;
        public readonly string UnderlyingNamespace;
        public readonly string UnderlyingMinimalName;
        public readonly string InterfaceFq;
        public readonly string InterfaceMinimalName;
        public readonly IReadOnlyList<MethodSig> MethodRequirements;
        public readonly IReadOnlyList<PropertySig> PropertyRequirements;
        public readonly IReadOnlyList<IndexerSig> IndexerRequirements;
        public readonly IReadOnlyList<EventSig> EventRequirements;

        public ProxyDecl(string uFq, string uNs, string uMin, string iFq, string iMin,
            IReadOnlyList<MethodSig> mReqs,
            IReadOnlyList<PropertySig> pReqs,
            IReadOnlyList<IndexerSig> iReqs,
            IReadOnlyList<EventSig> eReqs)
        {
            UnderlyingFq = uFq; UnderlyingNamespace = uNs; UnderlyingMinimalName = uMin;
            InterfaceFq = iFq; InterfaceMinimalName = iMin;
            MethodRequirements = mReqs;
            PropertyRequirements = pReqs;
            IndexerRequirements = iReqs;
            EventRequirements = eReqs;
        }
    }
}

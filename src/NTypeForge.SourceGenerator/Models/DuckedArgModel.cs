using System.Collections.Generic;
using System.Linq;

namespace NTypeForge.SourceGenerator.Models
{
    // One ducked argument of a candidate site, in the same primitives-only form as CandidateModel:
    // the argument's static type, the type the proxy wraps, the interface the proxy presents, and
    // the structural-match verdict. A Duck<T>() site always has exactly one; a method-argument site
    // has one per parameter that needs a proxy for the forwarded call to bind.
    internal sealed class DuckedArgModel
    {
        // Index into CandidateModel.OriginalParameters (receiver-adjusted for extension methods).
        // Zero (and meaningless) for Duck<T> calls.
        public int ArgumentIndex { get; }

        // Static type of the ducked argument.
        public bool ArgumentIsInterface { get; }
        public string ArgumentFq { get; }

        // The type actually wrapped by the proxy (the concrete being ducked, or - when an
        // existing proxy is re-ducked - the interface it currently presents).
        public string UnderlyingFq { get; }
        public string UnderlyingNamespace { get; }
        public string UnderlyingMinimalName { get; }
        public bool UnderlyingIsInterface { get; }
        public int UnderlyingBaseDepth { get; }

        // The interface the generated proxy implements.
        public string InterfaceFq { get; }
        public string InterfaceMinimalName { get; }

        // Members the proxy must implement (interface + inherited, deduped). Also used as the
        // structural requirement when matching this interface against other concrete types.
        public IReadOnlyList<MethodSig> MethodRequirements { get; }
        public IReadOnlyList<PropertySig> PropertyRequirements { get; }
        public IReadOnlyList<IndexerSig> IndexerRequirements { get; }
        public IReadOnlyList<EventSig> EventRequirements { get; }

        // CompatKeys of the underlying type's directly-declared methods, for matching it against
        // other interfaces' requirements.
        public IReadOnlyList<string> UnderlyingSurfaceCompatKeys { get; }
        // True when the underlying type structurally satisfies the interface.
        public bool IsSelfMatch { get; }
        // Non-null when the interface has a member NTypeForge can't proxy (e.g. static abstract);
        // names the offending member for the NTF002/NTF003 diagnostics.
        public string? UnsupportedMemberName { get; }

        // Set only for a ref/out/in near-miss (NTF004): the argument structurally matches the
        // interface, but the parameter is by-reference, so a generated proxy can't be passed. Holds
        // the offending parameter's ref kind ("ref"/"out"/"in") and name for the diagnostic message.
        public string? RefKindBlocker { get; }
        public string? BlockedParameterName { get; }

        // Canonical key folded into CandidateModel.Key. Includes every field that affects the
        // generated output or a reported diagnostic.
        public string Key { get; }

        public DuckedArgModel(
            int argumentIndex, bool argumentIsInterface, string argumentFq,
            string underlyingFq, string underlyingNamespace, string underlyingMinimalName,
            bool underlyingIsInterface, int underlyingBaseDepth,
            string interfaceFq, string interfaceMinimalName,
            IReadOnlyList<MethodSig> methodRequirements,
            IReadOnlyList<PropertySig> propertyRequirements,
            IReadOnlyList<IndexerSig> indexerRequirements,
            IReadOnlyList<EventSig> eventRequirements,
            IReadOnlyList<string> underlyingSurfaceCompatKeys,
            bool isSelfMatch, string? unsupportedMemberName,
            string? refKindBlocker = null, string? blockedParameterName = null)
        {
            ArgumentIndex = argumentIndex;
            ArgumentIsInterface = argumentIsInterface;
            ArgumentFq = argumentFq;
            UnderlyingFq = underlyingFq;
            UnderlyingNamespace = underlyingNamespace;
            UnderlyingMinimalName = underlyingMinimalName;
            UnderlyingIsInterface = underlyingIsInterface;
            UnderlyingBaseDepth = underlyingBaseDepth;
            InterfaceFq = interfaceFq;
            InterfaceMinimalName = interfaceMinimalName;
            MethodRequirements = methodRequirements;
            PropertyRequirements = propertyRequirements;
            IndexerRequirements = indexerRequirements;
            EventRequirements = eventRequirements;
            UnderlyingSurfaceCompatKeys = underlyingSurfaceCompatKeys;
            IsSelfMatch = isSelfMatch;
            UnsupportedMemberName = unsupportedMemberName;
            RefKindBlocker = refKindBlocker;
            BlockedParameterName = blockedParameterName;
            Key = BuildKey();
        }

        private string BuildKey()
        {
            var reqs = string.Join(",", MethodRequirements.Select(m => m.CompatKey));
            var props = string.Join(",", PropertyRequirements.Select(p => p.CompatKey));
            var idxs = string.Join(",", IndexerRequirements.Select(i => i.CompatKey));
            var evts = string.Join(",", EventRequirements.Select(e => e.CompatKey));
            var surface = string.Join(",", UnderlyingSurfaceCompatKeys);
            return string.Join("|",
                ArgumentIndex, ArgumentFq, ArgumentIsInterface,
                UnderlyingFq, UnderlyingIsInterface, UnderlyingBaseDepth,
                InterfaceFq, reqs, props, idxs, evts, surface,
                IsSelfMatch, UnsupportedMemberName ?? "",
                RefKindBlocker ?? "", BlockedParameterName ?? "");
        }
    }
}

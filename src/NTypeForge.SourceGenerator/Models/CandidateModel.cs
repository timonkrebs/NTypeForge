using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace NTypeForge.SourceGenerator.Models
{
    // A fully-resolved, value-equatable description of one duck-typing site. Everything the
    // source-output stage needs is captured here as primitives (strings/enums/spans); no
    // ISymbol or SyntaxNode is retained. This keeps the incremental pipeline from rooting the
    // compilation and lets Roslyn cache the collected array, so edits that don't change any
    // candidate skip regeneration entirely.
    internal sealed class CandidateModel : IEquatable<CandidateModel>
    {
        // The type whose generated extension class hosts the forwarding/Duck methods.
        public string TargetFq { get; }
        public string TargetNamespace { get; }
        public string TargetMinimalName { get; }
        public bool TargetIsInterface { get; }

        // Static type of the ducked argument (only meaningful for method-argument ducking).
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

        public int ArgumentIndex { get; }
        public bool IsStatic { get; }
        public bool IsDuckCall { get; }

        // The original (failing) method whose duck-typed parameter we forward through a proxy.
        // Unused for Duck<T> calls.
        public string OriginalMethodName { get; }
        public string OriginalContainingTypeFq { get; }
        public bool OriginalIsExtensionMethod { get; }
        public string OriginalReturnTypeFq { get; }
        public bool OriginalReturnsVoid { get; }
        public IReadOnlyList<ParamSig> OriginalParameters { get; }
        public int OriginalArity { get; }
        public IReadOnlyList<string> OriginalTypeParameters { get; }
        public IReadOnlyList<string> OriginalConstraints { get; }

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
        // Non-null when the interface has a member NTypeForge can't proxy (property/event/generic
        // method); names the offending member for the NTF002 diagnostic.
        public string? UnsupportedMemberName { get; }

        // Diagnostic location, decomposed so it stays equatable and roots nothing.
        public string? DiagFilePath { get; }
        public TextSpan DiagSpan { get; }
        public LinePositionSpan DiagLineSpan { get; }

        // Single canonical key driving value equality. Includes every field that affects the
        // generated output or a reported diagnostic, so equal keys are safe to dedupe/cache.
        public string Key { get; }

        public CandidateModel(
            string targetFq, string targetNamespace, string targetMinimalName, bool targetIsInterface,
            bool argumentIsInterface, string argumentFq,
            string underlyingFq, string underlyingNamespace, string underlyingMinimalName, bool underlyingIsInterface, int underlyingBaseDepth,
            string interfaceFq, string interfaceMinimalName,
            int argumentIndex, bool isStatic, bool isDuckCall,
            string originalMethodName, string originalContainingTypeFq, bool originalIsExtensionMethod,
            string originalReturnTypeFq, bool originalReturnsVoid, IReadOnlyList<ParamSig> originalParameters,
            int originalArity, IReadOnlyList<string> originalTypeParameters, IReadOnlyList<string> originalConstraints,
            IReadOnlyList<MethodSig> methodRequirements,
            IReadOnlyList<PropertySig> propertyRequirements,
            IReadOnlyList<IndexerSig> indexerRequirements,
            IReadOnlyList<EventSig> eventRequirements,
            IReadOnlyList<string> underlyingSurfaceCompatKeys,
            bool isSelfMatch, string? unsupportedMemberName,
            string? diagFilePath, TextSpan diagSpan, LinePositionSpan diagLineSpan)
        {
            TargetFq = targetFq;
            TargetNamespace = targetNamespace;
            TargetMinimalName = targetMinimalName;
            TargetIsInterface = targetIsInterface;
            ArgumentIsInterface = argumentIsInterface;
            ArgumentFq = argumentFq;
            UnderlyingFq = underlyingFq;
            UnderlyingNamespace = underlyingNamespace;
            UnderlyingMinimalName = underlyingMinimalName;
            UnderlyingIsInterface = underlyingIsInterface;
            UnderlyingBaseDepth = underlyingBaseDepth;
            InterfaceFq = interfaceFq;
            InterfaceMinimalName = interfaceMinimalName;
            ArgumentIndex = argumentIndex;
            IsStatic = isStatic;
            IsDuckCall = isDuckCall;
            OriginalMethodName = originalMethodName;
            OriginalContainingTypeFq = originalContainingTypeFq;
            OriginalIsExtensionMethod = originalIsExtensionMethod;
            OriginalReturnTypeFq = originalReturnTypeFq;
            OriginalReturnsVoid = originalReturnsVoid;
            OriginalParameters = originalParameters;
            OriginalArity = originalArity;
            OriginalTypeParameters = originalTypeParameters;
            OriginalConstraints = originalConstraints;
            MethodRequirements = methodRequirements;
            PropertyRequirements = propertyRequirements;
            IndexerRequirements = indexerRequirements;
            EventRequirements = eventRequirements;
            UnderlyingSurfaceCompatKeys = underlyingSurfaceCompatKeys;
            IsSelfMatch = isSelfMatch;
            UnsupportedMemberName = unsupportedMemberName;
            DiagFilePath = diagFilePath;
            DiagSpan = diagSpan;
            DiagLineSpan = diagLineSpan;
            Key = BuildKey();
        }

        private string BuildKey()
        {
            var prms = string.Join(",", OriginalParameters.Select(p => p.Key));
            var tps = string.Join(",", OriginalTypeParameters);
            var constraints = string.Join(",", OriginalConstraints);
            var reqs = string.Join(",", MethodRequirements.Select(m => m.CompatKey));
            var props = string.Join(",", PropertyRequirements.Select(p => p.CompatKey));
            var idxs = string.Join(",", IndexerRequirements.Select(i => i.CompatKey));
            var evts = string.Join(",", EventRequirements.Select(e => e.CompatKey));
            var surface = string.Join(",", UnderlyingSurfaceCompatKeys);
            return string.Join("|",
                TargetFq, TargetIsInterface, ArgumentFq, ArgumentIsInterface,
                UnderlyingFq, UnderlyingIsInterface, UnderlyingBaseDepth,
                InterfaceFq, ArgumentIndex, IsStatic, IsDuckCall,
                OriginalMethodName, OriginalContainingTypeFq, OriginalIsExtensionMethod,
                OriginalReturnTypeFq, OriginalReturnsVoid, prms,
                OriginalArity, tps, constraints,
                reqs, props, idxs, evts, surface, IsSelfMatch, UnsupportedMemberName ?? "",
                DiagFilePath ?? "", DiagSpan.Start, DiagSpan.Length);
        }

        public Location ToLocation()
            => DiagFilePath == null
                ? Location.None
                : Location.Create(DiagFilePath, DiagSpan, DiagLineSpan);

        public bool Equals(CandidateModel? other) => other != null && Key == other.Key;
        public override bool Equals(object? obj) => Equals(obj as CandidateModel);
        public override int GetHashCode() => Key.GetHashCode();
    }
}

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
        public bool TargetIsPublic { get; }

        // The arguments this site ducks, ordered by ArgumentIndex. A Duck<T> call has exactly
        // one; a method-argument site has one per parameter that needs a proxy.
        public IReadOnlyList<DuckedArgModel> DuckedArgs { get; }

        public bool IsStatic { get; }
        public bool IsDuckCall { get; }

        // The original (failing) method whose duck-typed parameters we forward through proxies.
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

        // Diagnostic location, decomposed so it stays equatable and roots nothing.
        public string? DiagFilePath { get; }
        public TextSpan DiagSpan { get; }
        public LinePositionSpan DiagLineSpan { get; }

        // Single canonical key driving value equality. Includes every field that affects the
        // generated output or a reported diagnostic, so equal keys are safe to dedupe/cache.
        public string Key { get; }

        public CandidateModel(
            string targetFq, string targetNamespace, string targetMinimalName, bool targetIsInterface, bool targetIsPublic,
            IReadOnlyList<DuckedArgModel> duckedArgs,
            bool isStatic, bool isDuckCall,
            string originalMethodName, string originalContainingTypeFq, bool originalIsExtensionMethod,
            string originalReturnTypeFq, bool originalReturnsVoid, IReadOnlyList<ParamSig> originalParameters,
            int originalArity, IReadOnlyList<string> originalTypeParameters, IReadOnlyList<string> originalConstraints,
            string? diagFilePath, TextSpan diagSpan, LinePositionSpan diagLineSpan)
        {
            TargetFq = targetFq;
            TargetNamespace = targetNamespace;
            TargetMinimalName = targetMinimalName;
            TargetIsInterface = targetIsInterface;
            TargetIsPublic = targetIsPublic;
            DuckedArgs = duckedArgs;
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
            // Arg keys are joined with a distinct separator so arg boundaries cannot blur into
            // the field separator used inside each key.
            var args = string.Join(";;", DuckedArgs.Select(a => a.Key));
            return string.Join("|",
                TargetFq, TargetIsInterface, TargetIsPublic,
                args, IsStatic, IsDuckCall,
                OriginalMethodName, OriginalContainingTypeFq, OriginalIsExtensionMethod,
                OriginalReturnTypeFq, OriginalReturnsVoid, prms,
                OriginalArity, tps, constraints,
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

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace NTypeForge.SourceGenerator.Models
{
    // Plain, immutable carriers for the bits of a method/parameter the generator renders. Identity
    // and deduplication never flow through struct equality on these types; the pipeline compares the
    // precomputed string keys instead (ParamSig.Key, MethodSig.DedupKey/CompatKey, and ultimately
    // CandidateModel.Key), so there are deliberately no Equals/GetHashCode overrides here.

    internal readonly struct ParamSig
    {
        public string TypeFq { get; }
        public RefKind RefKind { get; }
        public string Name { get; }

        // Canonical identity of a parameter, including its name (which affects generated signatures).
        public string Key { get; }

        public ParamSig(string typeFq, RefKind refKind, string name)
        {
            TypeFq = typeFq;
            RefKind = refKind;
            Name = name;
            Key = $"{refKind}:{typeFq}:{name}";
        }
    }

    // A method reduced to render-ready primitives plus two canonical keys:
    //   DedupKey  - name + parameter shape (refkind:type), excluding the return type. Collapses
    //               methods re-abstracted across base interfaces that differ only by return type.
    //   CompatKey - DedupKey plus the return type. Mirrors the old AreMethodsCompatible check
    //               (same return type, parameter types and ref kinds) for structural matching.
    internal sealed class MethodSig
    {
        public string Name { get; }
        public string ReturnTypeFq { get; }
        public bool ReturnsVoid { get; }
        public IReadOnlyList<ParamSig> Parameters { get; }
        public string DedupKey { get; }
        public string CompatKey { get; }

        public MethodSig(string name, string returnTypeFq, bool returnsVoid, IReadOnlyList<ParamSig> parameters)
        {
            Name = name;
            ReturnTypeFq = returnTypeFq;
            ReturnsVoid = returnsVoid;
            Parameters = parameters;
            var p = string.Join(",", parameters.Select(x => $"{x.RefKind}:{x.TypeFq}"));
            DedupKey = $"{name}({p})";
            CompatKey = $"{returnTypeFq} {DedupKey}";
        }
    }
}

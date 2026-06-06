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
        public int Arity { get; }
        public IReadOnlyList<string> TypeParameters { get; }
        public IReadOnlyList<string> Constraints { get; }
        public string DedupKey { get; }
        public string CompatKey { get; }

        public MethodSig(string name, string returnTypeFq, bool returnsVoid, IReadOnlyList<ParamSig> parameters,
                         int arity, IReadOnlyList<string> typeParameters, IReadOnlyList<string> constraints)
        {
            Name = name;
            ReturnTypeFq = returnTypeFq;
            ReturnsVoid = returnsVoid;
            Parameters = parameters;
            Arity = arity;
            TypeParameters = typeParameters;
            Constraints = constraints;
            var p = string.Join(",", parameters.Select(x => $"{x.RefKind}:{x.TypeFq}"));
            var generic = arity > 0 ? $"`{arity}" : "";
            DedupKey = $"{name}{generic}({p})";
            CompatKey = $"{returnTypeFq} {DedupKey}";
        }
    }

    internal sealed class PropertySig
    {
        public string Name { get; }
        public string TypeFq { get; }
        public bool HasGet { get; }
        public bool HasSet { get; }
        public string CompatKey { get; }

        public PropertySig(string name, string typeFq, bool hasGet, bool hasSet)
        {
            Name = name;
            TypeFq = typeFq;
            HasGet = hasGet;
            HasSet = hasSet;
            CompatKey = $"Property:{typeFq}:{name}:{(hasGet ? "G" : "")}:{(hasSet ? "S" : "")}";
        }
    }

    internal sealed class IndexerSig
    {
        public string TypeFq { get; }
        public IReadOnlyList<ParamSig> Parameters { get; }
        public bool HasGet { get; }
        public bool HasSet { get; }
        public string CompatKey { get; }

        public IndexerSig(string typeFq, IReadOnlyList<ParamSig> parameters, bool hasGet, bool hasSet)
        {
            TypeFq = typeFq;
            Parameters = parameters;
            HasGet = hasGet;
            HasSet = hasSet;
            var p = string.Join(",", parameters.Select(x => $"{x.RefKind}:{x.TypeFq}"));
            CompatKey = $"Indexer:{typeFq}:[{p}]:{(hasGet ? "G" : "")}:{(hasSet ? "S" : "")}";
        }
    }

    internal sealed class EventSig
    {
        public string Name { get; }
        public string TypeFq { get; }
        public string CompatKey { get; }

        public EventSig(string name, string typeFq)
        {
            Name = name;
            TypeFq = typeFq;
            CompatKey = $"Event:{typeFq}:{name}";
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace NTypeForge.SourceGenerator.Models
{
    // Plain, immutable carriers for the bits of a method/parameter the generator renders. Identity
    // and deduplication never flow through struct equality on these types; the pipeline compares the
    // precomputed string keys instead (ParamSig.Key, MemberSig.DedupKey/CompatKey, and ultimately
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

    internal abstract class MemberSig
    {
        public string Name { get; }
        public string DedupKey { get; }
        public string CompatKey { get; }

        protected MemberSig(string name, string dedupKey, string compatKey)
        {
            Name = name;
            DedupKey = dedupKey;
            CompatKey = compatKey;
        }
    }

    internal sealed class MethodSig : MemberSig
    {
        public string ReturnTypeFq { get; }
        public bool ReturnsVoid { get; }
        public IReadOnlyList<ParamSig> Parameters { get; }
        public IReadOnlyList<string> TypeParameters { get; }
        public string ConstraintsString { get; }

        public MethodSig(
            string name,
            string returnTypeFq,
            bool returnsVoid,
            IReadOnlyList<ParamSig> parameters,
            IReadOnlyList<string>? typeParameters = null,
            string? constraintsString = null)
            : base(
                  name,
                  $"{name}<{string.Join(",", typeParameters ?? new List<string>())}>({string.Join(",", parameters.Select(x => $"{x.RefKind}:{x.TypeFq}"))})",
                  $"{returnTypeFq} {name}<{string.Join(",", typeParameters ?? new List<string>())}>({string.Join(",", parameters.Select(x => $"{x.RefKind}:{x.TypeFq}"))}){constraintsString}"
            )
        {
            ReturnTypeFq = returnTypeFq;
            ReturnsVoid = returnsVoid;
            Parameters = parameters;
            TypeParameters = typeParameters ?? new List<string>();
            ConstraintsString = constraintsString ?? "";
        }
    }

    internal sealed class PropertySig : MemberSig
    {
        public string TypeFq { get; }
        public bool HasGet { get; }
        public bool HasSet { get; }
        public bool IsIndexer { get; }
        public IReadOnlyList<ParamSig> Parameters { get; }

        public PropertySig(
            string name,
            string typeFq,
            bool hasGet,
            bool hasSet,
            bool isIndexer,
            IReadOnlyList<ParamSig>? parameters = null)
            : base(
                  name,
                  isIndexer ? $"this[{string.Join(",", (parameters ?? new List<ParamSig>()).Select(x => $"{x.RefKind}:{x.TypeFq}"))}]" : name,
                  $"{typeFq} {(isIndexer ? $"this[{string.Join(",", (parameters ?? new List<ParamSig>()).Select(x => $"{x.RefKind}:{x.TypeFq}"))}]" : name)} {{ {(hasGet ? "get; " : "")}{(hasSet ? "set; " : "")}}}"
            )
        {
            TypeFq = typeFq;
            HasGet = hasGet;
            HasSet = hasSet;
            IsIndexer = isIndexer;
            Parameters = parameters ?? new List<ParamSig>();
        }
    }

    internal sealed class EventSig : MemberSig
    {
        public string TypeFq { get; }

        public EventSig(string name, string typeFq)
            : base(name, name, $"event {typeFq} {name}")
        {
            TypeFq = typeFq;
        }
    }
}

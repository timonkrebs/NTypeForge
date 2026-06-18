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
        public bool IsOptional { get; }
        public bool IsParams { get; }
        public string? DefaultValueSource { get; }

        // Canonical identity of a parameter, including its name (which affects generated signatures).
        public string Key { get; }

        public ParamSig(string typeFq, RefKind refKind, string name, bool isOptional = false, bool isParams = false, string? defaultValueSource = null)
        {
            TypeFq = typeFq;
            RefKind = refKind;
            Name = name;
            IsOptional = isOptional;
            IsParams = isParams;
            DefaultValueSource = defaultValueSource;
            Key = $"{refKind}:{typeFq}:{name}:{isOptional}:{isParams}:{defaultValueSource ?? ""}";
        }

        // The canonical parameter-shape encoding (ref kind + type, no names) shared by every key
        // that identifies a member by its parameter list.
        public static string Shape(IReadOnlyList<ParamSig> parameters)
            => string.Join(",", parameters.Select(x => $"{x.RefKind}:{x.TypeFq}"));
    }

    // A method reduced to render-ready primitives plus two canonical keys (supplied by the caller,
    // which is the only place that has the symbol needed to normalize generic methods):
    //   DedupKey  - name + arity + parameter shape (+ constraints for generics), excluding the
    //               return type. Collapses methods re-abstracted across base interfaces that differ
    //               only by return type.
    //   CompatKey - DedupKey plus the return type, for structural matching.
    // For a generic method, the method's own type parameters are normalized to positional tokens in
    // both keys, so two structurally identical generic methods match regardless of type-parameter
    // names (see CandidateAnalyzer.NormalizeTypeKey).
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
                         int arity, IReadOnlyList<string> typeParameters, IReadOnlyList<string> constraints,
                         string dedupKey, string compatKey)
        {
            Name = name;
            ReturnTypeFq = returnTypeFq;
            ReturnsVoid = returnsVoid;
            Parameters = parameters;
            Arity = arity;
            TypeParameters = typeParameters;
            Constraints = constraints;
            DedupKey = dedupKey;
            CompatKey = compatKey;
        }
    }

    internal sealed class PropertySig
    {
        public string Name { get; }
        public string TypeFq { get; }
        public bool HasGet { get; }
        public bool HasSet { get; }
        // True when the setter is init-only (`init` accessor). A proxy must declare the matching
        // accessor kind (`init`, not `set`) or it fails CS8854, and it can only forward to an
        // underlying member whose own setter is a regular `set` - see DuckTypingGenerator.
        public bool IsInit { get; }
        public string CompatKey { get; }

        public PropertySig(string name, string typeFq, bool hasGet, bool hasSet, bool isInit)
        {
            Name = name;
            TypeFq = typeFq;
            HasGet = hasGet;
            HasSet = hasSet;
            IsInit = isInit;
            CompatKey = CompatKeyFor(name, typeFq, hasGet, hasSet, isInit);
        }

        // The setter slot distinguishes none / `set` / `init` so an init-only requirement never
        // matches a key advertising a plain setter (and vice versa). Exposed statically so the
        // surface scan can enumerate the keys a member satisfies without allocating a PropertySig.
        public static string CompatKeyFor(string name, string typeFq, bool hasGet, bool hasSet, bool isInit)
        {
            var setMarker = isInit ? "I" : (hasSet ? "S" : "");
            return $"Property:{typeFq}:{name}:{(hasGet ? "G" : "")}:{setMarker}";
        }
    }

    internal sealed class IndexerSig
    {
        public string TypeFq { get; }
        public IReadOnlyList<ParamSig> Parameters { get; }
        public bool HasGet { get; }
        public bool HasSet { get; }
        // Parameter shape only (indexers are identified by their argument list, like a method's
        // DedupKey). Indexers have no `init` accessor in C#, so the setter is always a plain `set`.
        public string DedupKey { get; }
        public string CompatKey { get; }

        public IndexerSig(string typeFq, IReadOnlyList<ParamSig> parameters, bool hasGet, bool hasSet)
        {
            TypeFq = typeFq;
            Parameters = parameters;
            HasGet = hasGet;
            HasSet = hasSet;
            DedupKey = $"[{ParamSig.Shape(parameters)}]";
            CompatKey = CompatKeyFor(typeFq, parameters, hasGet, hasSet);
        }

        // Exposed statically so the surface scan can enumerate the keys a member satisfies without
        // allocating an IndexerSig.
        public static string CompatKeyFor(string typeFq, IReadOnlyList<ParamSig> parameters, bool hasGet, bool hasSet)
            => CompatKeyFor(typeFq, ParamSig.Shape(parameters), hasGet, hasSet);

        // Shape-string core, shared by the constructor and the surface scan (which passes
        // ParamSig.Shape of the indexer's parameters).
        public static string CompatKeyFor(string typeFq, string parameterShape, bool hasGet, bool hasSet)
            => $"Indexer:{typeFq}:[{parameterShape}]:{(hasGet ? "G" : "")}:{(hasSet ? "S" : "")}";
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
            CompatKey = CompatKeyFor(name, typeFq);
        }

        // Exposed statically so the surface scan can build the key without allocating an EventSig.
        public static string CompatKeyFor(string name, string typeFq)
            => $"Event:{typeFq}:{name}";
    }
}

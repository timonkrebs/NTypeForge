using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using NTypeForge.SourceGenerator.Models;

namespace NTypeForge.SourceGenerator
{
    // The proxyable members an interface (plus its bases) requires, and the first member that makes
    // it unproxyable (a static abstract member), if any.
    internal readonly struct InterfaceRequirements
    {
        public InterfaceRequirements(
            IReadOnlyList<MethodSig> methods, IReadOnlyList<PropertySig> properties,
            IReadOnlyList<IndexerSig> indexers, IReadOnlyList<EventSig> events, string? unsupported)
        {
            Methods = methods;
            Properties = properties;
            Indexers = indexers;
            Events = events;
            Unsupported = unsupported;
        }

        public IReadOnlyList<MethodSig> Methods { get; }
        public IReadOnlyList<PropertySig> Properties { get; }
        public IReadOnlyList<IndexerSig> Indexers { get; }
        public IReadOnlyList<EventSig> Events { get; }
        public string? Unsupported { get; }
    }

    // Determines what a proxy for an interface must implement (the interface side of structural
    // matching): a single traversal of the interface and all its base interfaces, bucketing members
    // by kind.
    internal static class InterfaceRequirementsAnalyzer
    {
        public static InterfaceRequirements Analyze(ITypeSymbol interfaceType)
        {
            var builder = new RequirementsBuilder();
            foreach (var iface in new[] { interfaceType }.Concat(interfaceType.AllInterfaces))
            {
                foreach (var member in iface.GetMembers())
                {
                    builder.Observe(member);
                }
            }
            return builder.Build();
        }

        private static bool IsAccessorMethod(ISymbol member)
            => member is IMethodSymbol m &&
               (m.MethodKind == MethodKind.PropertyGet || m.MethodKind == MethodKind.PropertySet ||
                m.MethodKind == MethodKind.EventAdd || m.MethodKind == MethodKind.EventRemove);

        // A member NTypeForge cannot proxy: a static abstract member (an instance proxy can't
        // implement it); a by-ref return (the forwarding proxy would have to re-ref); or a member
        // whose signature involves a pointer / function-pointer type (the generated proxy is not
        // `unsafe`). Accessor methods are judged via their owning property/event.
        private static bool IsUnproxyable(ISymbol member)
        {
            if (member.IsStatic) return member.IsAbstract;
            return ReturnsByRef(member) || InvolvesPointer(member);
        }

        private static bool ReturnsByRef(ISymbol member)
            => (member is IMethodSymbol m && m.RefKind != RefKind.None)
               || (member is IPropertySymbol p && p.RefKind != RefKind.None);

        private static bool InvolvesPointer(ISymbol member)
        {
            switch (member)
            {
                case IMethodSymbol m: return IsPointer(m.ReturnType) || m.Parameters.Any(x => IsPointer(x.Type));
                case IPropertySymbol p: return IsPointer(p.Type) || p.Parameters.Any(x => IsPointer(x.Type));
                case IEventSymbol e: return IsPointer(e.Type);
                default: return false;
            }
        }

        private static bool IsPointer(ITypeSymbol type)
            => type.TypeKind == TypeKind.Pointer || type.TypeKind == TypeKind.FunctionPointer;

        // Accumulates the requirement buckets in a single traversal of the interface's members.
        // Static members are never proxied (a proxy is an instance object): a static *abstract*
        // member makes the interface unproxyable (-> Unsupported / NTF002), while a static member
        // with a default implementation is supplied by the interface itself, so the concrete need
        // not have it.
        private sealed class RequirementsBuilder
        {
            private readonly List<MethodSig> _methods = new List<MethodSig>();
            private readonly List<PropertySig> _properties = new List<PropertySig>();
            private readonly List<IndexerSig> _indexers = new List<IndexerSig>();
            private readonly List<EventSig> _events = new List<EventSig>();
            private readonly HashSet<string> _seenMethods = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> _seenEvents = new HashSet<string>(StringComparer.Ordinal);
            private readonly Dictionary<string, int> _propertyIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<string, int> _indexerIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            private string? _unsupported;

            public void Observe(ISymbol member)
            {
                TrackUnsupported(member);
                if (!member.IsStatic) Bucket(member);
            }

            public InterfaceRequirements Build()
                => new InterfaceRequirements(_methods, _properties, _indexers, _events, _unsupported);

            private void TrackUnsupported(ISymbol member)
            {
                if (_unsupported != null) return;
                // Accessor methods (get_/set_/add_/remove_) are reported via their owning member.
                if (IsAccessorMethod(member)) return;
                if (IsUnproxyable(member)) _unsupported = member.Name;
            }

            private void Bucket(ISymbol member)
            {
                switch (member)
                {
                    case IMethodSymbol { MethodKind: MethodKind.Ordinary } method:
                        var m = MemberSignatures.ToMethodSig(method);
                        if (_seenMethods.Add(m.DedupKey)) _methods.Add(m);
                        break;
                    case IPropertySymbol { IsIndexer: true } indexer:
                        AddOrMergeIndexer(MemberSignatures.ToIndexerSig(indexer));
                        break;
                    case IPropertySymbol prop:
                        AddOrMergeProperty(MemberSignatures.ToPropertySig(prop));
                        break;
                    case IEventSymbol evt:
                        var e = MemberSignatures.ToEventSig(evt);
                        if (_seenEvents.Add(e.CompatKey)) _events.Add(e);
                        break;
                }
            }

            // A property name can appear across several base interfaces with different accessor sets
            // (e.g. IGet declares `{ get; }`, IGetSet declares `{ get; set; }`). The interface requires
            // the UNION of accessors, so merge rather than keep-first - otherwise the proxy omits an
            // accessor and fails CS0535. Only declarations with the same name AND type are the same
            // structural slot; same-named inherited properties with different types are distinct
            // interface requirements and must both be preserved.
            private void AddOrMergeProperty(PropertySig sig)
            {
                var key = $"{sig.Name}:{sig.TypeFq}";
                if (_propertyIndex.TryGetValue(key, out var i))
                {
                    _properties[i] = MergeProperty(_properties[i], sig);
                }
                else
                {
                    _propertyIndex[key] = _properties.Count;
                    _properties.Add(sig);
                }
            }

            private void AddOrMergeIndexer(IndexerSig sig)
            {
                // Indexers with the same parameter shape but different return types are distinct
                // inherited interface slots. Merge accessors only when both shape and type match.
                var key = $"{sig.TypeFq}:{sig.DedupKey}";
                if (_indexerIndex.TryGetValue(key, out var i))
                {
                    _indexers[i] = MergeIndexer(_indexers[i], sig);
                }
                else
                {
                    _indexerIndex[key] = _indexers.Count;
                    _indexers.Add(sig);
                }
            }

            private static PropertySig MergeProperty(PropertySig a, PropertySig b)
            {
                var hasSet = a.HasSet || b.HasSet;
                // The merged setter is init-only only if every contributing setter is init-only; a
                // plain `set` in any declaration means the proxy must expose a plain `set`.
                var hasPlainSet = (a.HasSet && !a.IsInit) || (b.HasSet && !b.IsInit);
                return new PropertySig(a.Name, a.TypeFq, a.HasGet || b.HasGet, hasSet, hasSet && !hasPlainSet);
            }

            private static IndexerSig MergeIndexer(IndexerSig a, IndexerSig b)
                => new IndexerSig(a.TypeFq, a.Parameters, a.HasGet || b.HasGet, a.HasSet || b.HasSet);
        }
    }
}

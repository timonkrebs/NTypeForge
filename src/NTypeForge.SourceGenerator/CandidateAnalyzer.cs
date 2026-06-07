using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NTypeForge.SourceGenerator.Models;

namespace NTypeForge.SourceGenerator
{
    // Transform stage (symbol-aware): resolve one invocation into a value-equatable CandidateModel
    // built entirely from primitives (strings/enums/spans). No ISymbol or SyntaxNode is retained, so
    // the incremental pipeline does not root the compilation. Returns null for invocations that are
    // not duck-typing sites.
    internal static class CandidateAnalyzer
    {
        public static CandidateModel? GetCandidate(GeneratorSyntaxContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            var semanticModel = context.SemanticModel;
            var symbolInfo = semanticModel.GetSymbolInfo(invocation);

            var duckCall = TryGetDuckCall(invocation, semanticModel, symbolInfo);
            if (duckCall != null) return duckCall;

            // A call that bound successfully needs no duck typing; only a failed overload
            // resolution (Symbol == null, with candidates) can be rescued by a generated proxy.
            if (symbolInfo.Symbol != null || symbolInfo.CandidateSymbols.Length == 0) return null;

            return TryGetMethodArgumentDuck(invocation, semanticModel, symbolInfo);
        }

        // Matches the top-level `NTypeForge` namespace only, so a user's unrelated
        // `Foo.NTypeForge` namespace is not mistaken for the library's.
        private static bool IsTopLevelNTypeForgeNamespace(INamespaceSymbol? ns)
            => ns != null && ns.Name == "NTypeForge" && (ns.ContainingNamespace == null || ns.ContainingNamespace.IsGlobalNamespace);

        // The underlying type kinds NTypeForge can build a proxy around.
        private static bool IsProxyableKind(ITypeSymbol type)
            => type.TypeKind == TypeKind.Class || type.TypeKind == TypeKind.Struct || type.TypeKind == TypeKind.Interface;

        private static ITypeSymbol GetUnderlyingType(ITypeSymbol type)
        {
            bool IsProxyInterface(ITypeSymbol t)
            {
                if (t is INamedTypeSymbol nt && nt.IsGenericType && nt.Name == "IProxy" && nt.TypeArguments.Length == 1)
                {
                    return IsTopLevelNTypeForgeNamespace(nt.ContainingNamespace);
                }
                return false;
            }

            if (IsProxyInterface(type))
            {
                return ((INamedTypeSymbol)type).TypeArguments[0];
            }

            foreach (var iface in type.AllInterfaces)
            {
                if (IsProxyInterface(iface))
                {
                    return ((INamedTypeSymbol)iface).TypeArguments[0];
                }
            }
            return type;
        }

        // An explicit `instance.Duck<T>()` (or `Duck<T>(instance)`) call.
        private static CandidateModel? TryGetDuckCall(
            InvocationExpressionSyntax invocation, SemanticModel semanticModel, SymbolInfo symbolInfo)
        {
            if (!(symbolInfo.Symbol is IMethodSymbol resolved) || resolved.Name != "Duck" ||
                !IsTopLevelNTypeForgeNamespace(resolved.ContainingNamespace) || resolved.TypeArguments.Length != 1)
                return null;

            var instanceExpr = GetDuckInstanceExpression(invocation);
            if (instanceExpr == null) return null;

            var argType = semanticModel.GetTypeInfo(instanceExpr).Type;
            if (argType == null) return null;

            var targetInterface = resolved.TypeArguments[0];
            var underlyingType = GetUnderlyingType(argType);
            if (targetInterface.TypeKind != TypeKind.Interface || !IsProxyableKind(underlyingType)) return null;

            return BuildModel(
                invocation, target: argType, argType: argType, underlyingType: underlyingType,
                interfaceType: targetInterface, argumentIndex: 0, isStatic: false, isDuckCall: true,
                originalMethod: null, isUnambiguousDuckSite: true);
        }

        private static ExpressionSyntax? GetDuckInstanceExpression(InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess) return memberAccess.Expression;
            if (invocation.ArgumentList.Arguments.Count == 1) return invocation.ArgumentList.Arguments[0].Expression;
            return null;
        }

        // A failed call whose argument could implicitly become an interface parameter via a proxy.
        // All duckable (overload, argument) sites are collected so we can tell whether the call has
        // exactly one such interpretation: that "unambiguous" flag gates the NTF003 near-miss
        // warning, keeping it off failed calls that merely happen to have an interface overload.
        private static CandidateModel? TryGetMethodArgumentDuck(
            InvocationExpressionSyntax invocation, SemanticModel semanticModel, SymbolInfo symbolInfo)
        {
            var sites = CollectDuckableArgumentSites(invocation, semanticModel, symbolInfo);
            if (sites.Count == 0) return null;

            var (candidate, argIndex) = sites[0];
            var argType = semanticModel.GetTypeInfo(invocation.ArgumentList.Arguments[argIndex].Expression).Type!;
            return BuildModel(
                invocation, target: candidate.ContainingType!, argType: argType,
                underlyingType: GetUnderlyingType(argType), interfaceType: candidate.Parameters[argIndex].Type,
                argumentIndex: argIndex, isStatic: candidate.IsStatic, isDuckCall: false,
                originalMethod: candidate, isUnambiguousDuckSite: sites.Count == 1);
        }

        private static List<(IMethodSymbol Candidate, int ArgIndex)> CollectDuckableArgumentSites(
            InvocationExpressionSyntax invocation, SemanticModel semanticModel, SymbolInfo symbolInfo)
        {
            var sites = new List<(IMethodSymbol, int)>();
            foreach (var candidate in symbolInfo.CandidateSymbols.OfType<IMethodSymbol>())
            {
                if (candidate.ContainingType == null) continue;
                var arguments = invocation.ArgumentList.Arguments;
                if (arguments.Count != candidate.Parameters.Length) continue;

                AddDuckableArgIndices(semanticModel, candidate, arguments, sites);
            }
            return sites;
        }

        private static void AddDuckableArgIndices(
            SemanticModel semanticModel, IMethodSymbol candidate,
            SeparatedSyntaxList<ArgumentSyntax> arguments, List<(IMethodSymbol, int)> sites)
        {
            for (int i = 0; i < arguments.Count; i++)
            {
                if (IsDuckableArgument(semanticModel, candidate, arguments, i)) sites.Add((candidate, i));
            }
        }

        private static bool IsDuckableArgument(
            SemanticModel semanticModel, IMethodSymbol candidate, SeparatedSyntaxList<ArgumentSyntax> arguments, int i)
        {
            var arg = arguments[i];
            var argType = semanticModel.GetTypeInfo(arg.Expression).Type;
            var paramType = candidate.Parameters[i].Type;

            if (argType == null || paramType == null || paramType.TypeKind != TypeKind.Interface) return false;

            var conversion = semanticModel.ClassifyConversion(arg.Expression, paramType);
            if (conversion.Exists && conversion.IsImplicit) return false;

            return IsProxyableKind(GetUnderlyingType(argType));
        }

        private static CandidateModel BuildModel(
            InvocationExpressionSyntax invocation,
            ITypeSymbol target,
            ITypeSymbol argType,
            ITypeSymbol underlyingType,
            ITypeSymbol interfaceType,
            int argumentIndex,
            bool isStatic,
            bool isDuckCall,
            IMethodSymbol? originalMethod,
            bool isUnambiguousDuckSite)
        {
            var requirements = AnalyzeRequirements(interfaceType);

            var surface = BuildSurfaceCompatKeys(underlyingType);
            var surfaceSet = new HashSet<string>(surface, StringComparer.Ordinal);

            bool isSelfMatch = requirements.Methods.All(r => surfaceSet.Contains(r.CompatKey)) &&
                               requirements.Properties.All(r => surfaceSet.Contains(r.CompatKey)) &&
                               requirements.Indexers.All(r => surfaceSet.Contains(r.CompatKey)) &&
                               requirements.Events.All(r => surfaceSet.Contains(r.CompatKey));

            var originalParams = originalMethod == null
                ? (IReadOnlyList<ParamSig>)Array.Empty<ParamSig>()
                : originalMethod.Parameters.Select(ToParamSig).ToList();

            var loc = invocation.GetLocation();

            return new CandidateModel(
                targetFq: Fq(target),
                targetNamespace: NamespaceOf(target),
                targetMinimalName: MinimalName(target),
                targetIsInterface: target.TypeKind == TypeKind.Interface,
                argumentIsInterface: argType.TypeKind == TypeKind.Interface,
                argumentFq: Fq(argType),
                underlyingFq: Fq(underlyingType),
                underlyingNamespace: NamespaceOf(underlyingType),
                underlyingMinimalName: MinimalName(underlyingType),
                underlyingIsInterface: underlyingType.TypeKind == TypeKind.Interface,
                underlyingBaseDepth: BaseTypeDepth(underlyingType),
                interfaceFq: Fq(interfaceType),
                interfaceMinimalName: MinimalName(interfaceType),
                argumentIndex: argumentIndex,
                isStatic: isStatic,
                isDuckCall: isDuckCall,
                originalMethodName: originalMethod?.Name ?? "",
                originalReturnTypeFq: originalMethod == null ? "" : Fq(originalMethod.ReturnType),
                originalReturnsVoid: originalMethod != null && originalMethod.ReturnType.SpecialType == SpecialType.System_Void,
                originalParameters: originalParams,
                methodRequirements: requirements.Methods,
                propertyRequirements: requirements.Properties,
                indexerRequirements: requirements.Indexers,
                eventRequirements: requirements.Events,
                underlyingSurfaceCompatKeys: surface,
                isSelfMatch: isSelfMatch,
                unsupportedMemberName: requirements.Unsupported,
                isUnambiguousDuckSite: isUnambiguousDuckSite,
                diagFilePath: loc.SourceTree?.FilePath,
                diagSpan: loc.SourceSpan,
                diagLineSpan: loc.GetLineSpan().Span);
        }

        private static ParamSig ToParamSig(IParameterSymbol p)
            => new ParamSig(Fq(p.Type), p.RefKind, p.Name);

        private static MethodSig ToMethodSig(IMethodSymbol m)
            => new MethodSig(
                m.Name,
                Fq(m.ReturnType),
                m.ReturnType.SpecialType == SpecialType.System_Void,
                m.Parameters.Select(ToParamSig).ToList(),
                m.Arity,
                m.TypeParameters.Select(tp => tp.Name).ToList(),
                m.TypeParameters.Select(GetConstraints).ToList()
            );

        private static string GetConstraints(ITypeParameterSymbol tp)
        {
            var constraints = new List<string>();
            if (tp.HasReferenceTypeConstraint) constraints.Add("class");
            if (tp.HasValueTypeConstraint) constraints.Add("struct");
            if (tp.HasUnmanagedTypeConstraint) constraints.Add("unmanaged");
            if (tp.HasNotNullConstraint) constraints.Add("notnull");
            foreach (var t in tp.ConstraintTypes)
            {
                constraints.Add(Fq(t));
            }
            if (tp.HasConstructorConstraint) constraints.Add("new()");

            return constraints.Count > 0
                ? $"where {tp.Name} : {string.Join(", ", constraints)}"
                : "";
        }

        private static PropertySig ToPropertySig(IPropertySymbol p)
            => new PropertySig(p.Name, Fq(p.Type), p.GetMethod != null, p.SetMethod != null, p.SetMethod is { IsInitOnly: true });

        private static IndexerSig ToIndexerSig(IPropertySymbol p)
            => new IndexerSig(Fq(p.Type), p.Parameters.Select(ToParamSig).ToList(), p.GetMethod != null, p.SetMethod != null);

        private static EventSig ToEventSig(IEventSymbol e)
            => new EventSig(e.Name, Fq(e.Type));

        // Walks the interface and all its base interfaces once, bucketing members into the four
        // proxyable requirement kinds (deduped per kind) and noting the first unsupported member.
        private static InterfaceRequirements AnalyzeRequirements(ITypeSymbol interfaceType)
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

        // The proxyable members an interface (plus its bases) requires, and the first member that
        // makes it unproxyable (a static abstract member), if any.
        private readonly struct InterfaceRequirements
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

        // Accumulates the requirement buckets in a single traversal of the interface's members.
        // Static members are never proxied (a proxy is an instance struct): a static *abstract*
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
            private readonly HashSet<string> _seenProperties = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> _seenIndexers = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> _seenEvents = new HashSet<string>(StringComparer.Ordinal);
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
                if (member.IsStatic && member.IsAbstract &&
                    (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } || member is IPropertySymbol || member is IEventSymbol))
                {
                    _unsupported = member.Name;
                }
            }

            private void Bucket(ISymbol member)
            {
                switch (member)
                {
                    case IMethodSymbol { MethodKind: MethodKind.Ordinary } method:
                        var m = ToMethodSig(method);
                        Dedup(_seenMethods, m.DedupKey, _methods, m);
                        break;
                    case IPropertySymbol { IsIndexer: true } indexer:
                        var ix = ToIndexerSig(indexer);
                        Dedup(_seenIndexers, ix.DedupKey, _indexers, ix);
                        break;
                    case IPropertySymbol prop:
                        var p = ToPropertySig(prop);
                        Dedup(_seenProperties, p.Name, _properties, p);
                        break;
                    case IEventSymbol evt:
                        var e = ToEventSig(evt);
                        Dedup(_seenEvents, e.Name, _events, e);
                        break;
                }
            }

            private static void Dedup<T>(HashSet<string> seen, string key, List<T> list, T sig)
            {
                if (seen.Add(key)) list.Add(sig);
            }
        }

        // CompatKeys of the type's directly-declared members.
        private static IReadOnlyList<string> BuildSurfaceCompatKeys(ITypeSymbol type)
        {
            var result = new List<string>();
            foreach (var member in type.GetMembers())
            {
                result.AddRange(SurfaceKeysForMember(member));
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
            switch (member)
            {
                case IMethodSymbol { MethodKind: MethodKind.Ordinary, DeclaredAccessibility: Accessibility.Public } method:
                    return new[] { ToMethodSig(method).CompatKey };
                case IPropertySymbol { IsIndexer: true } indexer:
                    return IndexerSurfaceKeys(indexer);
                case IPropertySymbol prop:
                    return PropertySurfaceKeys(prop);
                case IEventSymbol { DeclaredAccessibility: Accessibility.Public } evt:
                    return new[] { ToEventSig(evt).CompatKey };
                default:
                    return Array.Empty<string>();
            }
        }

        private static IEnumerable<string> IndexerSurfaceKeys(IPropertySymbol indexer)
        {
            var typeFq = Fq(indexer.Type);
            var parameters = indexer.Parameters.Select(ToParamSig).ToList();
            var canGet = indexer.GetMethod is { DeclaredAccessibility: Accessibility.Public };
            var canSet = indexer.SetMethod is { DeclaredAccessibility: Accessibility.Public };
            if (canGet) yield return IndexerSig.CompatKeyFor(typeFq, parameters, true, false);
            if (canSet) yield return IndexerSig.CompatKeyFor(typeFq, parameters, false, true);
            if (canGet && canSet) yield return IndexerSig.CompatKeyFor(typeFq, parameters, true, true);
        }

        private static IEnumerable<string> PropertySurfaceKeys(IPropertySymbol prop)
        {
            var name = prop.Name;
            var typeFq = Fq(prop.Type);
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

        // ---------------------------------------------------------------------------------------
        // Symbol display helpers
        // ---------------------------------------------------------------------------------------

        private static string Fq(ITypeSymbol type)
            => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        private static string MinimalName(ITypeSymbol type)
            => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        // The namespace a generated artifact for `type` is emitted into (global-namespace types go
        // to NTypeForge).
        private static string NamespaceOf(ITypeSymbol type)
            => type.ContainingNamespace.IsGlobalNamespace ? "NTypeForge" : type.ContainingNamespace.ToDisplayString();

        private static int BaseTypeDepth(ITypeSymbol type)
        {
            int depth = 0;
            for (var b = type.BaseType; b != null; b = b.BaseType) depth++;
            return depth;
        }
    }
}

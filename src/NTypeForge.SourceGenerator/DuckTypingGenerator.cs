using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NTypeForge.SourceGenerator.Models;

namespace NTypeForge.SourceGenerator
{
    [Generator]
    public class DuckTypingGenerator : IIncrementalGenerator
    {
        private static readonly DiagnosticDescriptor NoStructuralMatch = new DiagnosticDescriptor(
            id: "NTF001",
            title: "No structural match for Duck<T>",
            messageFormat: "Type '{0}' cannot be duck-typed to '{1}': it does not structurally implement all required members",
            category: "NTypeForge",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor UnsupportedInterfaceMember = new DiagnosticDescriptor(
            id: "NTF002",
            title: "Unsupported interface member for duck typing",
            messageFormat: "Interface '{0}' cannot be duck-typed: member '{1}' is not a supported member",
            category: "NTypeForge",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // The transform resolves every duck-typing site into a value-equatable CandidateModel
            // (strings/enums/spans only - no ISymbol or SyntaxNode). That keeps symbols out of the
            // cached pipeline, so the compilation is not rooted and edits that don't change any
            // candidate skip regeneration. Execute consumes only these primitives.
            var candidates = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (s, _) => s is InvocationExpressionSyntax,
                    transform: static (ctx, _) => GetCandidate(ctx))
                .Where(static c => c != null)
                .Select(static (c, _) => c!);

            context.RegisterSourceOutput(candidates.Collect(), static (spc, models) => Execute(spc, models));
        }

        // ---------------------------------------------------------------------------------------
        // Transform stage (symbol-aware): build the equatable model
        // ---------------------------------------------------------------------------------------

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

        private static CandidateModel? GetCandidate(GeneratorSyntaxContext context)
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
                interfaceType: targetInterface, argumentIndex: 0, isStatic: false, isDuckCall: true, originalMethod: null);
        }

        private static ExpressionSyntax? GetDuckInstanceExpression(InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess) return memberAccess.Expression;
            if (invocation.ArgumentList.Arguments.Count == 1) return invocation.ArgumentList.Arguments[0].Expression;
            return null;
        }

        // A failed call whose argument could implicitly become an interface parameter via a proxy.
        private static CandidateModel? TryGetMethodArgumentDuck(
            InvocationExpressionSyntax invocation, SemanticModel semanticModel, SymbolInfo symbolInfo)
        {
            foreach (var candidate in symbolInfo.CandidateSymbols.OfType<IMethodSymbol>())
            {
                if (candidate.ContainingType == null) continue;
                var arguments = invocation.ArgumentList.Arguments;
                if (arguments.Count != candidate.Parameters.Length) continue;

                var model = TryDuckArgument(invocation, semanticModel, candidate, arguments);
                if (model != null) return model;
            }
            return null;
        }

        private static CandidateModel? TryDuckArgument(
            InvocationExpressionSyntax invocation, SemanticModel semanticModel,
            IMethodSymbol candidate, SeparatedSyntaxList<ArgumentSyntax> arguments)
        {
            for (int i = 0; i < arguments.Count; i++)
            {
                var arg = arguments[i];
                var argType = semanticModel.GetTypeInfo(arg.Expression).Type;
                var paramType = candidate.Parameters[i].Type;

                if (argType == null || paramType == null || paramType.TypeKind != TypeKind.Interface) continue;

                var conversion = semanticModel.ClassifyConversion(arg.Expression, paramType);
                if (conversion.Exists && conversion.IsImplicit) continue;

                var underlyingType = GetUnderlyingType(argType);
                if (!IsProxyableKind(underlyingType)) continue;

                return BuildModel(
                    invocation, target: candidate.ContainingType, argType: argType, underlyingType: underlyingType,
                    interfaceType: paramType, argumentIndex: i, isStatic: candidate.IsStatic, isDuckCall: false,
                    originalMethod: candidate);
            }
            return null;
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
            IMethodSymbol? originalMethod)
        {
            var methodRequirements = BuildMethodRequirements(interfaceType);
            var propertyRequirements = BuildPropertyRequirements(interfaceType);
            var indexerRequirements = BuildIndexerRequirements(interfaceType);
            var eventRequirements = BuildEventRequirements(interfaceType);

            var surface = BuildSurfaceCompatKeys(underlyingType);
            var surfaceSet = new HashSet<string>(surface, StringComparer.Ordinal);

            bool isSelfMatch = methodRequirements.All(r => surfaceSet.Contains(r.CompatKey)) &&
                               propertyRequirements.All(r => surfaceSet.Contains(r.CompatKey)) &&
                               indexerRequirements.All(r => surfaceSet.Contains(r.CompatKey)) &&
                               eventRequirements.All(r => surfaceSet.Contains(r.CompatKey));

            var unsupported = FindUnsupportedInterfaceMemberName(interfaceType);

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
                methodRequirements: methodRequirements,
                propertyRequirements: propertyRequirements,
                indexerRequirements: indexerRequirements,
                eventRequirements: eventRequirements,
                underlyingSurfaceCompatKeys: surface,
                isSelfMatch: isSelfMatch,
                unsupportedMemberName: unsupported,
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

        private static IReadOnlyList<MethodSig> BuildMethodRequirements(ITypeSymbol interfaceType)
        {
            var result = new List<MethodSig>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var method in new[] { interfaceType }.Concat(interfaceType.AllInterfaces)
                         .SelectMany(i => i.GetMembers())
                         .OfType<IMethodSymbol>()
                         .Where(m => m.MethodKind == MethodKind.Ordinary))
            {
                var sig = ToMethodSig(method);
                if (seen.Add(sig.DedupKey))
                {
                    result.Add(sig);
                }
            }

            return result;
        }

        private static IReadOnlyList<PropertySig> BuildPropertyRequirements(ITypeSymbol interfaceType)
        {
            var result = new List<PropertySig>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var prop in new[] { interfaceType }.Concat(interfaceType.AllInterfaces)
                         .SelectMany(i => i.GetMembers())
                         .OfType<IPropertySymbol>()
                         .Where(p => !p.IsIndexer))
            {
                var sig = ToPropertySig(prop);
                if (seen.Add(sig.Name))
                {
                    result.Add(sig);
                }
            }

            return result;
        }

        private static IReadOnlyList<IndexerSig> BuildIndexerRequirements(ITypeSymbol interfaceType)
        {
            var result = new List<IndexerSig>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var prop in new[] { interfaceType }.Concat(interfaceType.AllInterfaces)
                         .SelectMany(i => i.GetMembers())
                         .OfType<IPropertySymbol>()
                         .Where(p => p.IsIndexer))
            {
                var sig = ToIndexerSig(prop);
                // Simple DedupKey for indexer by parameters
                var pKey = string.Join(",", prop.Parameters.Select(x => $"{x.RefKind}:{Fq(x.Type)}"));
                if (seen.Add(pKey))
                {
                    result.Add(sig);
                }
            }

            return result;
        }

        private static IReadOnlyList<EventSig> BuildEventRequirements(ITypeSymbol interfaceType)
        {
            var result = new List<EventSig>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var evt in new[] { interfaceType }.Concat(interfaceType.AllInterfaces)
                         .SelectMany(i => i.GetMembers())
                         .OfType<IEventSymbol>())
            {
                var sig = ToEventSig(evt);
                if (seen.Add(sig.Name))
                {
                    result.Add(sig);
                }
            }

            return result;
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
            var sig = ToIndexerSig(indexer);
            var canGet = indexer.GetMethod is { DeclaredAccessibility: Accessibility.Public };
            var canSet = indexer.SetMethod is { DeclaredAccessibility: Accessibility.Public };
            if (canGet) yield return new IndexerSig(sig.TypeFq, sig.Parameters, true, false).CompatKey;
            if (canSet) yield return new IndexerSig(sig.TypeFq, sig.Parameters, false, true).CompatKey;
            if (canGet && canSet) yield return sig.CompatKey;
        }

        private static IEnumerable<string> PropertySurfaceKeys(IPropertySymbol prop)
        {
            var sig = ToPropertySig(prop);
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
            if (canGet) yield return new PropertySig(sig.Name, sig.TypeFq, true, false, false).CompatKey;
            if (canSet)
            {
                yield return new PropertySig(sig.Name, sig.TypeFq, false, true, false).CompatKey;
                yield return new PropertySig(sig.Name, sig.TypeFq, false, true, true).CompatKey;
            }
            if (canGet && canSet)
            {
                yield return new PropertySig(sig.Name, sig.TypeFq, true, true, false).CompatKey;
                yield return new PropertySig(sig.Name, sig.TypeFq, true, true, true).CompatKey;
            }
        }

        // Returns the first interface member NTypeForge cannot proxy.
        private static string? FindUnsupportedInterfaceMemberName(ITypeSymbol interfaceType)
        {
            foreach (var iface in new[] { interfaceType }.Concat(interfaceType.AllInterfaces))
            {
                foreach (var member in iface.GetMembers())
                {
                    // Instance ducking cannot proxy static members that require implementation.
                    // While DIMs are allowed, any required static member must be flagged.
                    if (member.IsStatic && member.IsAbstract && (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } ||
                                           member is IPropertySymbol ||
                                           member is IEventSymbol))
                    {
                        return member.Name;
                    }
                }
            }
            return null;
        }

        // ---------------------------------------------------------------------------------------
        // Source-output stage (symbol-free): render from the equatable models
        // ---------------------------------------------------------------------------------------

        private sealed class InterfaceInfo
        {
            public string Fq = "";
            public string MinimalName = "";
            public IReadOnlyList<MethodSig> MethodRequirements = Array.Empty<MethodSig>();
            public IReadOnlyList<PropertySig> PropertyRequirements = Array.Empty<PropertySig>();
            public IReadOnlyList<IndexerSig> IndexerRequirements = Array.Empty<IndexerSig>();
            public IReadOnlyList<EventSig> EventRequirements = Array.Empty<EventSig>();
        }

        private sealed class ConcreteInfo
        {
            public string Fq = "";
            public string Namespace = "";
            public string MinimalName = "";
            public int BaseDepth;
            public HashSet<string> SurfaceKeys = new HashSet<string>(StringComparer.Ordinal);
        }

        private readonly struct ProxyDecl
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

        private static void Execute(SourceProductionContext context, ImmutableArray<CandidateModel> candidates)
        {
            if (candidates.IsDefaultOrEmpty) return;

            var allExtensions = new List<CandidateModel>();
            var interfaceInfo = new Dictionary<string, InterfaceInfo>(StringComparer.Ordinal);
            var concreteInfo = new Dictionary<string, ConcreteInfo>(StringComparer.Ordinal);

            foreach (var candidate in candidates)
            {
                ProcessCandidate(context, candidate, allExtensions, interfaceInfo, concreteInfo);
            }

            if (allExtensions.Count == 0) return;

            var possibleMatches = ComputeMatches(interfaceInfo, concreteInfo);

            EmitProxies(context, allExtensions, possibleMatches, interfaceInfo);
            EmitExtensions(context, allExtensions, possibleMatches, interfaceInfo);
        }

        // Sort one candidate into the emit set (structural self-match), a diagnostic (an
        // unmatched Duck<T> or an unsupported member), or silent drop (an unmatched implicit
        // method-argument duck, where the original call error already stands).
        private static void ProcessCandidate(
            SourceProductionContext context, CandidateModel candidate,
            List<CandidateModel> allExtensions,
            Dictionary<string, InterfaceInfo> interfaceInfo,
            Dictionary<string, ConcreteInfo> concreteInfo)
        {
            if (candidate.UnsupportedMemberName != null)
            {
                if (candidate.IsDuckCall)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        UnsupportedInterfaceMember, candidate.ToLocation(),
                        candidate.InterfaceFq, candidate.UnsupportedMemberName));
                }
                return;
            }

            if (!candidate.IsSelfMatch)
            {
                if (candidate.IsDuckCall)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        NoStructuralMatch, candidate.ToLocation(),
                        candidate.UnderlyingFq, candidate.InterfaceFq));
                }
                return;
            }

            allExtensions.Add(candidate);
            RegisterInterface(interfaceInfo, candidate);
            RegisterConcrete(concreteInfo, candidate);
        }

        private static void RegisterInterface(Dictionary<string, InterfaceInfo> interfaceInfo, CandidateModel candidate)
        {
            if (interfaceInfo.ContainsKey(candidate.InterfaceFq)) return;
            interfaceInfo[candidate.InterfaceFq] = new InterfaceInfo
            {
                Fq = candidate.InterfaceFq,
                MinimalName = candidate.InterfaceMinimalName,
                MethodRequirements = candidate.MethodRequirements,
                PropertyRequirements = candidate.PropertyRequirements,
                IndexerRequirements = candidate.IndexerRequirements,
                EventRequirements = candidate.EventRequirements,
            };
        }

        private static void RegisterConcrete(Dictionary<string, ConcreteInfo> concreteInfo, CandidateModel candidate)
        {
            if (candidate.UnderlyingIsInterface || concreteInfo.ContainsKey(candidate.UnderlyingFq)) return;
            concreteInfo[candidate.UnderlyingFq] = new ConcreteInfo
            {
                Fq = candidate.UnderlyingFq,
                Namespace = candidate.UnderlyingNamespace,
                MinimalName = candidate.UnderlyingMinimalName,
                BaseDepth = candidate.UnderlyingBaseDepth,
                SurfaceKeys = new HashSet<string>(candidate.UnderlyingSurfaceCompatKeys, StringComparer.Ordinal),
            };
        }

        private static Dictionary<string, List<ConcreteInfo>> ComputeMatches(
            Dictionary<string, InterfaceInfo> interfaceInfo,
            Dictionary<string, ConcreteInfo> concreteInfo)
        {
            // For each interface, the concrete types that structurally match it, most-derived
            // first: the generated unwrap branches test `TryUnbox<C>` (an `is C` check), which is
            // also true for subtypes of C, so a derived type must win its own branch ahead of its
            // base. Ties broken by fully-qualified name for deterministic output.
            var possibleMatches = new Dictionary<string, List<ConcreteInfo>>(StringComparer.Ordinal);
            foreach (var iface in interfaceInfo.Values.OrderBy(i => i.Fq, StringComparer.Ordinal))
            {
                possibleMatches[iface.Fq] = concreteInfo.Values
                    .OrderByDescending(c => c.BaseDepth)
                    .ThenBy(c => c.Fq, StringComparer.Ordinal)
                    .Where(c => ConcreteSatisfies(iface, c))
                    .ToList();
            }
            return possibleMatches;
        }

        private static bool ConcreteSatisfies(InterfaceInfo iface, ConcreteInfo concrete)
            => iface.MethodRequirements.All(r => concrete.SurfaceKeys.Contains(r.CompatKey)) &&
               iface.PropertyRequirements.All(r => concrete.SurfaceKeys.Contains(r.CompatKey)) &&
               iface.IndexerRequirements.All(r => concrete.SurfaceKeys.Contains(r.CompatKey)) &&
               iface.EventRequirements.All(r => concrete.SurfaceKeys.Contains(r.CompatKey));

        private static void EmitProxies(
            SourceProductionContext context,
            List<CandidateModel> allExtensions,
            Dictionary<string, List<ConcreteInfo>> possibleMatches,
            Dictionary<string, InterfaceInfo> interfaceInfo)
        {
            var proxiesByNamespace = new Dictionary<string, List<ProxyDecl>>(StringComparer.Ordinal);

            void AddProxy(string uFq, string uNs, string uMin, string iFq, string iMin,
                IReadOnlyList<MethodSig> mReqs,
                IReadOnlyList<PropertySig> pReqs,
                IReadOnlyList<IndexerSig> iReqs,
                IReadOnlyList<EventSig> eReqs)
            {
                if (!proxiesByNamespace.TryGetValue(uNs, out var list))
                {
                    list = new List<ProxyDecl>();
                    proxiesByNamespace[uNs] = list;
                }
                if (!list.Any(x => x.UnderlyingFq == uFq && x.InterfaceFq == iFq))
                {
                    list.Add(new ProxyDecl(uFq, uNs, uMin, iFq, iMin, mReqs, pReqs, iReqs, eReqs));
                }
            }

            foreach (var item in allExtensions)
            {
                AddProxy(item.UnderlyingFq, item.UnderlyingNamespace, item.UnderlyingMinimalName,
                    item.InterfaceFq, item.InterfaceMinimalName,
                    item.MethodRequirements, item.PropertyRequirements, item.IndexerRequirements, item.EventRequirements);
            }
            foreach (var kvp in possibleMatches)
            {
                var iface = interfaceInfo[kvp.Key];
                foreach (var concrete in kvp.Value)
                {
                    AddProxy(concrete.Fq, concrete.Namespace, concrete.MinimalName,
                        iface.Fq, iface.MinimalName,
                        iface.MethodRequirements, iface.PropertyRequirements, iface.IndexerRequirements, iface.EventRequirements);
                }
            }

            foreach (var kvp in proxiesByNamespace.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var sb = new StringBuilder();
                sb.AppendLine("// <auto-generated/>");
                sb.AppendLine("using System;");
                sb.AppendLine("using NTypeForge;");
                sb.AppendLine();
                sb.AppendLine($"namespace {kvp.Key}");
                sb.AppendLine("{");
                foreach (var proxy in kvp.Value.OrderBy(x => x.UnderlyingFq + "|" + x.InterfaceFq, StringComparer.Ordinal))
                {
                    GenerateProxyStruct(sb, proxy);
                }
                sb.AppendLine("}");
                context.AddSource($"{kvp.Key.Replace(".", "_")}_Proxies.g.cs", sb.ToString());
            }
        }

        private static void EmitExtensions(
            SourceProductionContext context,
            List<CandidateModel> allExtensions,
            Dictionary<string, List<ConcreteInfo>> possibleMatches,
            Dictionary<string, InterfaceInfo> interfaceInfo)
        {
            var extensionsByTarget = new Dictionary<string, List<CandidateModel>>(StringComparer.Ordinal);
            foreach (var item in allExtensions)
            {
                if (!extensionsByTarget.TryGetValue(item.TargetFq, out var list))
                {
                    list = new List<CandidateModel>();
                    extensionsByTarget[item.TargetFq] = list;
                }
                list.Add(item);
            }

            foreach (var kvp in extensionsByTarget.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                // Order by a location-independent content key so the emitted Duck<T> branches and
                // forwarding methods don't depend on the order the duck sites appear in source.
                var items = kvp.Value.OrderBy(EmitOrderKey, StringComparer.Ordinal).ToList();
                var first = items[0];
                var targetNamespace = first.TargetNamespace;
                var targetFullName = first.TargetFq;
                var extensionClassName = $"{Sanitize(first.TargetMinimalName)}_DuckTypingExtensions";

                var sb = new StringBuilder();
                sb.AppendLine("// <auto-generated/>");
                sb.AppendLine("using System;");
                sb.AppendLine("using NTypeForge;");
                sb.AppendLine();
                sb.AppendLine($"namespace {targetNamespace}");
                sb.AppendLine("{");
                sb.AppendLine($"    public static class {extensionClassName}");
                sb.AppendLine("    {");
                sb.AppendLine($"        extension ({targetFullName} target)");
                sb.AppendLine("        {");

                EmitDuckMethod(sb, items, possibleMatches);
                EmitForwardingMethods(sb, items, targetFullName, possibleMatches, interfaceInfo);

                sb.AppendLine("        }");
                sb.AppendLine("    }");
                sb.AppendLine("}");

                // Qualify the hint with the target namespace: two target types with the same simple
                // name in different namespaces would otherwise produce the same hint name and crash
                // the generator (duplicate hintName).
                context.AddSource($"{targetNamespace.Replace(".", "_")}_{extensionClassName}.g.cs", sb.ToString());
            }
        }

        // Stable, location-independent ordering key for a candidate's emitted contribution: the
        // forwarded method name + parameter shape, then the interface and underlying types. Duck
        // calls (empty method name / parameters) collapse to ordering by interface.
        private static string EmitOrderKey(CandidateModel c)
            => string.Join("|",
                c.OriginalMethodName,
                string.Join(",", c.OriginalParameters.Select(p => p.Key)),
                c.ArgumentFq,
                c.InterfaceFq,
                c.UnderlyingFq);

        // Emit a single Duck<T>() per target type that dispatches on typeof(T). One method per
        // interface would share the identical Duck<T>() signature (return type and generic
        // constraints don't participate in overloading) and collide with CS0111 when a type is
        // ducked to more than one interface.
        private static void EmitDuckMethod(
            StringBuilder sb,
            List<CandidateModel> items,
            Dictionary<string, List<ConcreteInfo>> possibleMatches)
        {
            var duckCandidates = new List<CandidateModel>();
            var seenDuckInterfaces = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (item.IsDuckCall && seenDuckInterfaces.Add(item.InterfaceFq))
                    duckCandidates.Add(item);
            }

            if (duckCandidates.Count == 0) return;

            sb.AppendLine("            public T Duck<T>() where T : class");
            sb.AppendLine("            {");
            foreach (var candidate in duckCandidates)
            {
                var iface = candidate.InterfaceFq;
                sb.AppendLine($"                if (typeof(T) == typeof({iface}))");
                sb.AppendLine("                {");
                // Unwrap check: only when target is an interface can it actually be a proxy that
                // needs re-wrapping. For a concrete target the unwrap branch is always trivially
                // taken, so we skip it and wrap directly.
                if (candidate.TargetIsInterface && possibleMatches.TryGetValue(iface, out var matches))
                {
                    int ui = 0;
                    foreach (var m in matches)
                    {
                        var local = $"c_{ui++}";
                        sb.AppendLine($"                    if (target.TryUnbox<{m.Fq}>(out var {local})) return (T)(object)new {ProxyFullName(m.Namespace, m.MinimalName, candidate.InterfaceMinimalName)}({local});");
                    }
                }
                // Direct wrap. Unreachable for a proxy whose concrete type was ducked in this
                // compilation (its TryUnbox branch above wins). It only fires for a non-proxy or
                // for a proxy whose concrete is unknown here (e.g. created in another assembly); in
                // the latter case this double-wraps, but TryUnbox/Unbox still walk the full chain
                // back to the original instance, so only single-level IProxy<T>.Inner is affected.
                sb.AppendLine($"                    return (T)(object)new {ProxyFullName(candidate.UnderlyingNamespace, candidate.UnderlyingMinimalName, candidate.InterfaceMinimalName)}(({candidate.UnderlyingFq})target);");
                sb.AppendLine("                }");
            }
            sb.AppendLine("                throw new global::System.InvalidOperationException(\"NTypeForge: no proxy was generated for \" + typeof(T));");
            sb.AppendLine("            }");
        }

        private static void EmitForwardingMethods(
            StringBuilder sb,
            List<CandidateModel> items,
            string targetFullName,
            Dictionary<string, List<ConcreteInfo>> possibleMatches,
            Dictionary<string, InterfaceInfo> interfaceInfo)
        {
            var generatedMethods = new HashSet<string>(StringComparer.Ordinal);

            foreach (var candidate in items)
            {
                if (candidate.IsDuckCall) continue;
                EmitForwardingMethod(sb, candidate, targetFullName, possibleMatches, generatedMethods);
            }
        }

        private static void EmitForwardingMethod(
            StringBuilder sb, CandidateModel candidate, string targetFullName,
            Dictionary<string, List<ConcreteInfo>> possibleMatches, HashSet<string> generatedMethods)
        {
            var argIndex = candidate.ArgumentIndex;
            var parameters = candidate.OriginalParameters;
            var argName = parameters[argIndex].Name;
            var receiver = candidate.IsStatic ? targetFullName : "target";
            var methodName = candidate.OriginalMethodName;

            // The forwarding call's argument list, with the duck-typed argument replaced by
            // `argReplacement` and every other parameter passed through verbatim.
            string CallArgs(string argReplacement) => string.Join(", ", parameters.Select((p, idx) =>
                idx == argIndex ? argReplacement : $"{RefPrefix(p.RefKind)}{p.Name}"));

            var methodParams = string.Join(", ", parameters.Select((p, idx) =>
                $"{RefPrefix(p.RefKind)}{(idx == argIndex ? candidate.ArgumentFq : p.TypeFq)} {p.Name}"));

            var methodSig = $"{methodName}({methodParams})";
            if (!generatedMethods.Add(methodSig)) return;

            var isStatic = candidate.IsStatic ? "static " : "";
            sb.AppendLine($"            public {isStatic}{candidate.OriginalReturnTypeFq} {methodName}({methodParams})");
            sb.AppendLine("            {");

            EmitForwardingUnwrapBranches(sb, candidate, argName, receiver, methodName, CallArgs, possibleMatches);

            // Direct wrap fallback; see the note in EmitDuckMethod for the cross-assembly
            // double-wrap boundary (recoverable via TryUnbox/Unbox).
            var directProxy = ProxyFullName(candidate.UnderlyingNamespace, candidate.UnderlyingMinimalName, candidate.InterfaceMinimalName);
            var directCall = $"{receiver}.{methodName}({CallArgs($"new {directProxy}(({candidate.UnderlyingFq}){argName})")})";
            sb.AppendLine($"                {ReturnStatement(candidate.OriginalReturnsVoid, directCall)}");
            sb.AppendLine("            }");
        }

        private static void EmitForwardingUnwrapBranches(
            StringBuilder sb, CandidateModel candidate, string argName, string receiver, string methodName,
            Func<string, string> callArgs, Dictionary<string, List<ConcreteInfo>> possibleMatches)
        {
            // Unwrap branches only make sense when the incoming value can actually be a proxy, i.e.
            // when its static type is an interface. For a concrete argument type they are dead
            // branches and force a needless box, so we skip straight to the direct wrap.
            if (!candidate.ArgumentIsInterface || !possibleMatches.TryGetValue(candidate.InterfaceFq, out var matches))
                return;

            int ui = 0;
            foreach (var m in matches)
            {
                var local = $"c_{ui++}";
                var proxy = ProxyFullName(m.Namespace, m.MinimalName, candidate.InterfaceMinimalName);
                var call = $"{receiver}.{methodName}({callArgs($"new {proxy}({local})")})";
                sb.AppendLine($"                if ({argName}.TryUnbox<{m.Fq}>(out var {local})) {{");
                sb.AppendLine($"                    {(candidate.OriginalReturnsVoid ? $"{call}; return;" : $"return {call};")}");
                sb.AppendLine("                }");
            }
        }

        private static void GenerateProxyStruct(StringBuilder sb, ProxyDecl proxy)
        {
            var structName = GetProxyStructName(proxy.UnderlyingMinimalName, proxy.InterfaceMinimalName);
            var interfaceFullName = proxy.InterfaceFq;
            var underlyingFullName = proxy.UnderlyingFq;

            sb.AppendLine($"    internal readonly struct {structName} : {interfaceFullName}, IProxy<{underlyingFullName}>");
            sb.AppendLine("    {");
            sb.AppendLine($"        private readonly {underlyingFullName} _instance;");
            sb.AppendLine();
            sb.AppendLine($"        public {structName}({underlyingFullName} instance)");
            sb.AppendLine("        {");
            sb.AppendLine("            _instance = instance;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        public {underlyingFullName} Inner => _instance;");
            sb.AppendLine($"        object IProxy.Unwrapped => _instance;");
            sb.AppendLine();

            foreach (var method in proxy.MethodRequirements) EmitProxyMethod(sb, method);
            foreach (var prop in proxy.PropertyRequirements) EmitProxyProperty(sb, prop);
            foreach (var idx in proxy.IndexerRequirements) EmitProxyIndexer(sb, idx);
            foreach (var evt in proxy.EventRequirements) EmitProxyEvent(sb, evt);

            sb.AppendLine("    }");
            sb.AppendLine();
        }

        private static void EmitProxyMethod(StringBuilder sb, MethodSig method)
        {
            var typeParams = method.Arity > 0 ? $"<{string.Join(", ", method.TypeParameters)}>" : "";
            var parametersStr = string.Join(", ", method.Parameters.Select(p => $"{RefPrefix(p.RefKind)}{p.TypeFq} {p.Name}"));
            var argsStr = string.Join(", ", method.Parameters.Select(p => $"{RefPrefix(p.RefKind)}{p.Name}"));

            sb.AppendLine($"        public {method.ReturnTypeFq} {method.Name}{typeParams}({parametersStr})");
            foreach (var constraint in method.Constraints)
            {
                if (!string.IsNullOrEmpty(constraint)) sb.AppendLine($"            {constraint}");
            }
            sb.AppendLine("        {");
            sb.AppendLine($"            {ReturnStatement(method.ReturnsVoid, $"_instance.{method.Name}{typeParams}({argsStr})")}");
            sb.AppendLine("        }");
        }

        private static void EmitProxyProperty(StringBuilder sb, PropertySig prop)
        {
            sb.AppendLine($"        public {prop.TypeFq} {prop.Name}");
            sb.AppendLine("        {");
            if (prop.HasGet) sb.AppendLine($"            get => _instance.{prop.Name};");
            // Match the interface's accessor kind: an `init`-only property must be implemented with
            // `init` (CS8854 otherwise). Structural matching guarantees the underlying has a regular
            // `set` here, so forwarding `_instance.X = value` is legal.
            if (prop.HasSet) sb.AppendLine($"            {(prop.IsInit ? "init" : "set")} => _instance.{prop.Name} = value;");
            sb.AppendLine("        }");
        }

        private static void EmitProxyIndexer(StringBuilder sb, IndexerSig idx)
        {
            var parametersStr = string.Join(", ", idx.Parameters.Select(p => $"{RefPrefix(p.RefKind)}{p.TypeFq} {p.Name}"));
            var argsStr = string.Join(", ", idx.Parameters.Select(p => $"{RefPrefix(p.RefKind)}{p.Name}"));
            sb.AppendLine($"        public {idx.TypeFq} this[{parametersStr}]");
            sb.AppendLine("        {");
            if (idx.HasGet) sb.AppendLine($"            get => _instance[{argsStr}];");
            if (idx.HasSet) sb.AppendLine($"            set => _instance[{argsStr}] = value;");
            sb.AppendLine("        }");
        }

        private static void EmitProxyEvent(StringBuilder sb, EventSig evt)
        {
            sb.AppendLine($"        public event {evt.TypeFq} {evt.Name}");
            sb.AppendLine("        {");
            sb.AppendLine($"            add => _instance.{evt.Name} += value;");
            sb.AppendLine($"            remove => _instance.{evt.Name} -= value;");
            sb.AppendLine("        }");
        }

        // ---------------------------------------------------------------------------------------
        // Pure string helpers
        // ---------------------------------------------------------------------------------------

        private static string Fq(ITypeSymbol type)
            => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        private static string MinimalName(ITypeSymbol type)
            => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        // The namespace a generated artifact for `type` is emitted into (global-namespace types go
        // to NTypeForge).
        private static string NamespaceOf(ITypeSymbol type)
            => type.ContainingNamespace.IsGlobalNamespace ? "NTypeForge" : type.ContainingNamespace.ToDisplayString();

        // global::-qualified so a proxy reference can never be mistaken for a member of a like-named
        // namespace, type, or (e.g.) the surrounding Duck<T> type parameter.
        private static string ProxyFullName(string underlyingNamespace, string underlyingMinimalName, string interfaceMinimalName)
            => $"global::{underlyingNamespace}.{GetProxyStructName(underlyingMinimalName, interfaceMinimalName)}";

        private static string RefPrefix(Microsoft.CodeAnalysis.RefKind refKind)
             => refKind switch { Microsoft.CodeAnalysis.RefKind.Ref => "ref ", Microsoft.CodeAnalysis.RefKind.Out => "out ", Microsoft.CodeAnalysis.RefKind.In => "in ", _ => "" };

        private static string ReturnStatement(bool returnsVoid, string call)
            => returnsVoid ? $"{call};" : $"return {call};";

        // Maps an arbitrary type display name to a valid C# identifier fragment: every character
        // that isn't a letter, digit, or underscore becomes '_', and a leading digit is prefixed.
        // This keeps generic/array/nested type names (e.g. `Holder<int, string>`) from producing
        // illegal struct names.
        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "_";
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
            }
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }

        private static int BaseTypeDepth(ITypeSymbol type)
        {
            int depth = 0;
            for (var b = type.BaseType; b != null; b = b.BaseType) depth++;
            return depth;
        }

        private static string GetProxyStructName(string underlyingMinimalName, string interfaceMinimalName)
            => $"{Sanitize(underlyingMinimalName)}_{Sanitize(interfaceMinimalName)}_Proxy";
    }
}

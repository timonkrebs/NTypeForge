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
            messageFormat: "Interface '{0}' cannot be duck-typed: member '{1}' is not a supported member. NTypeForge only supports non-generic methods.",
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

            // Handle Duck<T> calls
            if (symbolInfo.Symbol is IMethodSymbol resolvedMethod && resolvedMethod.Name == "Duck" &&
                IsTopLevelNTypeForgeNamespace(resolvedMethod.ContainingNamespace) && resolvedMethod.TypeArguments.Length == 1)
            {
                var targetInterface = resolvedMethod.TypeArguments[0];
                ExpressionSyntax? instanceExpr = null;
                if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    instanceExpr = memberAccess.Expression;
                }
                else if (invocation.ArgumentList.Arguments.Count == 1)
                {
                    instanceExpr = invocation.ArgumentList.Arguments[0].Expression;
                }

                if (instanceExpr != null)
                {
                    var argType = semanticModel.GetTypeInfo(instanceExpr).Type;
                    if (argType != null)
                    {
                        var underlyingType = GetUnderlyingType(argType);
                        if (targetInterface.TypeKind == TypeKind.Interface && IsProxyableKind(underlyingType))
                        {
                            return BuildModel(
                                invocation,
                                target: argType,
                                argType: argType,
                                underlyingType: underlyingType,
                                interfaceType: targetInterface,
                                argumentIndex: 0,
                                isStatic: false,
                                isDuckCall: true,
                                originalMethod: null);
                        }
                    }
                }
            }

            if (symbolInfo.Symbol != null) return null;
            if (symbolInfo.CandidateSymbols.Length == 0) return null;

            foreach (var candidate in symbolInfo.CandidateSymbols.OfType<IMethodSymbol>())
            {
                ITypeSymbol? targetType = candidate.ContainingType;
                if (targetType == null) continue;

                var arguments = invocation.ArgumentList.Arguments;
                if (arguments.Count != candidate.Parameters.Length) continue;

                for (int i = 0; i < arguments.Count; i++)
                {
                    var arg = arguments[i];
                    var argType = semanticModel.GetTypeInfo(arg.Expression).Type;
                    var param = candidate.Parameters[i];
                    var paramType = param.Type;

                    if (argType == null || paramType == null) continue;

                    var underlyingType = GetUnderlyingType(argType);

                    var conversion = semanticModel.ClassifyConversion(arg.Expression, paramType);
                    if (!conversion.Exists || !conversion.IsImplicit)
                    {
                        if (paramType.TypeKind == TypeKind.Interface && IsProxyableKind(underlyingType))
                        {
                            return BuildModel(
                                invocation,
                                target: targetType,
                                argType: argType,
                                underlyingType: underlyingType,
                                interfaceType: paramType,
                                argumentIndex: i,
                                isStatic: candidate.IsStatic,
                                isDuckCall: false,
                                originalMethod: candidate);
                        }
                    }
                }
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
            var requirements = BuildInterfaceRequirements(interfaceType);
            var surface = BuildSurfaceCompatKeys(underlyingType);
            var surfaceSet = new HashSet<string>(surface, StringComparer.Ordinal);
            var isSelfMatch = requirements.All(r => surfaceSet.Contains(r.CompatKey));
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
                interfaceRequirements: requirements,
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
        {
            var typeParameters = m.TypeParameters.Select(t => t.Name).ToList();

            var constraintsString = string.Empty;
            if (m.TypeParameters.Any())
            {
                var parts = new List<string>();
                foreach (var tp in m.TypeParameters)
                {
                    var constraints = new List<string>();
                    if (tp.HasReferenceTypeConstraint) constraints.Add("class");
                    if (tp.HasValueTypeConstraint) constraints.Add("struct");
                    if (tp.HasNotNullConstraint) constraints.Add("notnull");
                    if (tp.HasUnmanagedTypeConstraint) constraints.Add("unmanaged");

                    foreach (var constraintType in tp.ConstraintTypes)
                    {
                        constraints.Add(Fq(constraintType));
                    }
                    if (tp.HasConstructorConstraint) constraints.Add("new()");

                    if (constraints.Count > 0)
                    {
                        parts.Add($"where {tp.Name} : {string.Join(", ", constraints)}");
                    }
                }
                if (parts.Count > 0)
                {
                    constraintsString = " " + string.Join(" ", parts);
                }
            }

            return new MethodSig(
                m.Name,
                Fq(m.ReturnType),
                m.ReturnType.SpecialType == SpecialType.System_Void,
                m.Parameters.Select(ToParamSig).ToList(),
                typeParameters,
                constraintsString);
        }

        // Methods the proxy must implement: the interface's own methods plus everything inherited
        // from base interfaces (or the generated struct fails CS0535). The direct interface is
        // scanned first so a re-declared (shadowing) member wins over an inherited one of the same
        // signature; methods that differ only by return type collapse via DedupKey.
        private static IReadOnlyList<MemberSig> BuildInterfaceRequirements(ITypeSymbol interfaceType)
        {
            var result = new List<MemberSig>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var member in new[] { interfaceType }.Concat(interfaceType.AllInterfaces)
                         .SelectMany(i => i.GetMembers()))
            {
                MemberSig? sig = null;
                if (member is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary)
                {
                    sig = ToMethodSig(method);
                }
                else if (member is IPropertySymbol property)
                {
                    sig = ToPropertySig(property);
                }
                else if (member is IEventSymbol evt)
                {
                    sig = ToEventSig(evt);
                }

                if (sig != null && seen.Add(sig.DedupKey))
                {
                    result.Add(sig);
                }
            }

            return result;
        }

        // CompatKeys of the type's directly-declared ordinary members. Matches the historical
        // behavior of structural matching against `GetMembers` (inherited members on the concrete
        // type are intentionally not considered).
        private static IReadOnlyList<string> BuildSurfaceCompatKeys(ITypeSymbol type)
        {
            var keys = new List<string>();
            foreach (var member in type.GetMembers())
            {
                if (member is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary)
                {
                    keys.Add(ToMethodSig(method).CompatKey);
                }
                else if (member is IPropertySymbol property)
                {
                    keys.Add(ToPropertySig(property).CompatKey);
                }
                else if (member is IEventSymbol evt)
                {
                    keys.Add(ToEventSig(evt).CompatKey);
                }
            }
            return keys.Distinct(StringComparer.Ordinal).ToList();
        }

        // Returns the first interface member NTypeForge cannot proxy.
        // With generic methods, properties, and events supported, this is mostly checking for unsupported complex scenarios, but returning null for now as most are supported.
        private static string? FindUnsupportedInterfaceMemberName(ITypeSymbol interfaceType)
        {
            return null;
        }

        private static PropertySig ToPropertySig(IPropertySymbol p)
            => new PropertySig(
                p.Name,
                Fq(p.Type),
                p.GetMethod != null,
                p.SetMethod != null,
                p.IsIndexer,
                p.Parameters.Select(ToParamSig).ToList());

        private static EventSig ToEventSig(IEventSymbol e)
            => new EventSig(e.Name, Fq(e.Type));

        // ---------------------------------------------------------------------------------------
        // Source-output stage (symbol-free): render from the equatable models
        // ---------------------------------------------------------------------------------------

        private sealed class InterfaceInfo
        {
            public string Fq = "";
            public string MinimalName = "";
            public IReadOnlyList<MemberSig> Requirements = Array.Empty<MemberSig>();
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
            public readonly IReadOnlyList<MemberSig> Requirements;

            public ProxyDecl(string uFq, string uNs, string uMin, string iFq, string iMin, IReadOnlyList<MemberSig> reqs)
            {
                UnderlyingFq = uFq; UnderlyingNamespace = uNs; UnderlyingMinimalName = uMin;
                InterfaceFq = iFq; InterfaceMinimalName = iMin; Requirements = reqs;
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
                // Properties/events/generic methods can't be proxied; generating anyway would
                // produce a proxy that fails to compile. Skip it (the original call error stands),
                // and for an explicit Duck<T> call surface a clear diagnostic.
                if (candidate.UnsupportedMemberName != null)
                {
                    if (candidate.IsDuckCall)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            UnsupportedInterfaceMember,
                            candidate.ToLocation(),
                            candidate.InterfaceFq,
                            candidate.UnsupportedMemberName));
                    }
                    continue;
                }

                if (candidate.IsSelfMatch)
                {
                    allExtensions.Add(candidate);

                    if (!interfaceInfo.ContainsKey(candidate.InterfaceFq))
                    {
                        interfaceInfo[candidate.InterfaceFq] = new InterfaceInfo
                        {
                            Fq = candidate.InterfaceFq,
                            MinimalName = candidate.InterfaceMinimalName,
                            Requirements = candidate.InterfaceRequirements,
                        };
                    }

                    if (!candidate.UnderlyingIsInterface && !concreteInfo.ContainsKey(candidate.UnderlyingFq))
                    {
                        concreteInfo[candidate.UnderlyingFq] = new ConcreteInfo
                        {
                            Fq = candidate.UnderlyingFq,
                            Namespace = candidate.UnderlyingNamespace,
                            MinimalName = candidate.UnderlyingMinimalName,
                            BaseDepth = candidate.UnderlyingBaseDepth,
                            SurfaceKeys = new HashSet<string>(candidate.UnderlyingSurfaceCompatKeys, StringComparer.Ordinal),
                        };
                    }
                }
                else if (candidate.IsDuckCall)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        NoStructuralMatch,
                        candidate.ToLocation(),
                        candidate.UnderlyingFq,
                        candidate.InterfaceFq));
                }
            }

            if (allExtensions.Count == 0) return;

            // For each interface, the concrete types that structurally match it, most-derived
            // first: the generated unwrap branches test `TryUnbox<C>` (an `is C` check), which is
            // also true for subtypes of C, so a derived type must win its own branch ahead of its
            // base. Ties broken by fully-qualified name for deterministic output.
            var possibleMatches = new Dictionary<string, List<ConcreteInfo>>(StringComparer.Ordinal);
            foreach (var iface in interfaceInfo.Values.OrderBy(i => i.Fq, StringComparer.Ordinal))
            {
                var list = new List<ConcreteInfo>();
                foreach (var concrete in concreteInfo.Values
                             .OrderByDescending(c => c.BaseDepth)
                             .ThenBy(c => c.Fq, StringComparer.Ordinal))
                {
                    if (iface.Requirements.All(r => concrete.SurfaceKeys.Contains(r.CompatKey)))
                    {
                        list.Add(concrete);
                    }
                }
                possibleMatches[iface.Fq] = list;
            }

            EmitProxies(context, allExtensions, possibleMatches, interfaceInfo);
            EmitExtensions(context, allExtensions, possibleMatches, interfaceInfo);
        }

        private static void EmitProxies(
            SourceProductionContext context,
            List<CandidateModel> allExtensions,
            Dictionary<string, List<ConcreteInfo>> possibleMatches,
            Dictionary<string, InterfaceInfo> interfaceInfo)
        {
            var proxiesByNamespace = new Dictionary<string, List<ProxyDecl>>(StringComparer.Ordinal);

            void AddProxy(string uFq, string uNs, string uMin, string iFq, string iMin, IReadOnlyList<MemberSig> reqs)
            {
                if (!proxiesByNamespace.TryGetValue(uNs, out var list))
                {
                    list = new List<ProxyDecl>();
                    proxiesByNamespace[uNs] = list;
                }
                if (!list.Any(x => x.UnderlyingFq == uFq && x.InterfaceFq == iFq))
                {
                    list.Add(new ProxyDecl(uFq, uNs, uMin, iFq, iMin, reqs));
                }
            }

            foreach (var item in allExtensions)
            {
                AddProxy(item.UnderlyingFq, item.UnderlyingNamespace, item.UnderlyingMinimalName,
                    item.InterfaceFq, item.InterfaceMinimalName, item.InterfaceRequirements);
            }
            foreach (var kvp in possibleMatches)
            {
                var iface = interfaceInfo[kvp.Key];
                foreach (var concrete in kvp.Value)
                {
                    AddProxy(concrete.Fq, concrete.Namespace, concrete.MinimalName,
                        iface.Fq, iface.MinimalName, iface.Requirements);
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
                if (!generatedMethods.Add(methodSig)) continue;

                var isStatic = candidate.IsStatic ? "static " : "";
                sb.AppendLine($"            public {isStatic}{candidate.OriginalReturnTypeFq} {methodName}({methodParams})");
                sb.AppendLine("            {");

                // Unwrap branches only make sense when the incoming value can actually be a proxy,
                // i.e. when its static type is an interface. For a concrete argument type they are
                // dead branches and force a needless box, so we skip straight to the direct wrap.
                if (candidate.ArgumentIsInterface && possibleMatches.TryGetValue(candidate.InterfaceFq, out var matches))
                {
                    int ui = 0;
                    foreach (var m in matches)
                    {
                        var local = $"c_{ui++}";
                        var proxy = ProxyFullName(m.Namespace, m.MinimalName, candidate.InterfaceMinimalName);
                        var call = $"{receiver}.{methodName}({CallArgs($"new {proxy}({local})")})";
                        sb.AppendLine($"                if ({argName}.TryUnbox<{m.Fq}>(out var {local})) {{");
                        sb.AppendLine($"                    {(candidate.OriginalReturnsVoid ? $"{call}; return;" : $"return {call};")}");
                        sb.AppendLine("                }");
                    }
                }

                // Direct wrap fallback; see the note in EmitDuckMethod for the cross-assembly
                // double-wrap boundary (recoverable via TryUnbox/Unbox).
                var directProxy = ProxyFullName(candidate.UnderlyingNamespace, candidate.UnderlyingMinimalName, candidate.InterfaceMinimalName);
                var directCall = $"{receiver}.{methodName}({CallArgs($"new {directProxy}(({candidate.UnderlyingFq}){argName})")})";
                sb.AppendLine($"                {ReturnStatement(candidate.OriginalReturnsVoid, directCall)}");
                sb.AppendLine("            }");
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

            foreach (var member in proxy.Requirements)
            {
                if (member is MethodSig method)
                {
                    var parametersStr = string.Join(", ", method.Parameters.Select(p => $"{RefPrefix(p.RefKind)}{p.TypeFq} {p.Name}"));
                    var argsStr = string.Join(", ", method.Parameters.Select(p => $"{RefPrefix(p.RefKind)}{p.Name}"));
                    var typeParamsStr = method.TypeParameters.Count > 0 ? $"<{string.Join(", ", method.TypeParameters)}>" : "";

                    sb.AppendLine($"        public {method.ReturnTypeFq} {method.Name}{typeParamsStr}({parametersStr}){method.ConstraintsString}");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            {ReturnStatement(method.ReturnsVoid, $"_instance.{method.Name}{typeParamsStr}({argsStr})")}");
                    sb.AppendLine("        }");
                }
                else if (member is PropertySig property)
                {
                    if (property.IsIndexer)
                    {
                        var parametersStr = string.Join(", ", property.Parameters.Select(p => $"{RefPrefix(p.RefKind)}{p.TypeFq} {p.Name}"));
                        var argsStr = string.Join(", ", property.Parameters.Select(p => $"{RefPrefix(p.RefKind)}{p.Name}"));

                        sb.AppendLine($"        public {property.TypeFq} this[{parametersStr}]");
                        sb.AppendLine("        {");
                        if (property.HasGet)
                        {
                            sb.AppendLine($"            get => _instance[{argsStr}];");
                        }
                        if (property.HasSet)
                        {
                            sb.AppendLine($"            set => _instance[{argsStr}] = value;");
                        }
                        sb.AppendLine("        }");
                    }
                    else
                    {
                        sb.AppendLine($"        public {property.TypeFq} {property.Name}");
                        sb.AppendLine("        {");
                        if (property.HasGet)
                        {
                            sb.AppendLine($"            get => _instance.{property.Name};");
                        }
                        if (property.HasSet)
                        {
                            sb.AppendLine($"            set => _instance.{property.Name} = value;");
                        }
                        sb.AppendLine("        }");
                    }
                }
                else if (member is EventSig evt)
                {
                    sb.AppendLine($"        public event {evt.TypeFq} {evt.Name}");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            add => _instance.{evt.Name} += value;");
                    sb.AppendLine($"            remove => _instance.{evt.Name} -= value;");
                    sb.AppendLine("        }");
                }
            }
            sb.AppendLine("    }");
            sb.AppendLine();
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

        private static string RefPrefix(RefKind refKind)
            => refKind switch { RefKind.Ref => "ref ", RefKind.Out => "out ", RefKind.In => "in ", _ => "" };

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

using System;
using System.Collections.Generic;
using System.Linq;
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
            messageFormat: "Interface '{0}' cannot be duck-typed: member '{1}' is not a method. NTypeForge only supports method members.",
            category: "NTypeForge",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // TODO(perf/caching): CandidateInvocation carries ITypeSymbol/syntax nodes, which
            // are not value-equatable and root the compilation, so the incremental cache never
            // hits and Execute re-runs on every edit. Follow-up: project an equatable primitive
            // model in the transform stage and keep symbols out of the cached pipeline.
            var candidateInvocations = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (s, _) => s is InvocationExpressionSyntax,
                    transform: static (ctx, _) => GetCandidateInvocation(ctx))
                .Where(static c => c != null)
                .Select(static (c, _) => c!.Value);

            // Execute only needs the candidates (each already carries the symbols it requires);
            // it never reads the Compilation, so we don't Combine with CompilationProvider.
            context.RegisterSourceOutput(candidateInvocations.Collect(), static (spc, candidates) => Execute(spc, candidates));
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

        private static CandidateInvocation? GetCandidateInvocation(GeneratorSyntaxContext context)
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
                            return new CandidateInvocation(
                                invocation,
                                argType,
                                argType,
                                underlyingType,
                                targetInterface,
                                0,
                                resolvedMethod,
                                false,
                                true
                            );
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
                            return new CandidateInvocation(
                                invocation,
                                targetType,
                                argType,
                                underlyingType,
                                paramType,
                                i,
                                candidate,
                                candidate.IsStatic,
                                false
                            );
                        }
                    }
                }
            }

            return null;
        }

        private struct ExtensionItem
        {
            public CandidateInvocation Candidate;
            public StructuralMatchResult MatchResult;
        }

        private static void Execute(SourceProductionContext context, System.Collections.Immutable.ImmutableArray<CandidateInvocation> candidates)
        {
            if (candidates.IsDefaultOrEmpty) return;

            var allExtensions = new List<ExtensionItem>();
            var concreteTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            var interfaces = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

            foreach (var candidate in candidates)
            {
                // Properties/events/indexers can't be proxied; generating anyway would
                // produce a proxy that fails CS0535. Skip it (the original call error
                // stands), and for an explicit Duck<T> call surface a clear diagnostic.
                var unsupported = FindUnsupportedInterfaceMember(candidate.ExpectedInterfaceType);
                if (unsupported != null)
                {
                    if (candidate.IsDuckCall)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            UnsupportedInterfaceMember,
                            candidate.Invocation.GetLocation(),
                            candidate.ExpectedInterfaceType.ToDisplayString(),
                            unsupported.Name));
                    }
                    continue;
                }

                var matchResult = CheckStructuralMatch(candidate.UnderlyingType, candidate.ExpectedInterfaceType);
                if (matchResult.IsMatch)
                {
                    allExtensions.Add(new ExtensionItem { Candidate = candidate, MatchResult = matchResult });
                    interfaces.Add(candidate.ExpectedInterfaceType);
                    if (candidate.UnderlyingType.TypeKind != TypeKind.Interface)
                    {
                        concreteTypes.Add(candidate.UnderlyingType);
                    }
                }
                else if (candidate.IsDuckCall)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        NoStructuralMatch,
                        candidate.Invocation.GetLocation(),
                        candidate.UnderlyingType.ToDisplayString(),
                        candidate.ExpectedInterfaceType.ToDisplayString()));
                }
            }

            if (allExtensions.Count == 0) return;

            // Collect all possible concrete-to-interface matches among involved types
            var possibleMatches = new Dictionary<ITypeSymbol, List<(ITypeSymbol Concrete, StructuralMatchResult Match)>>(SymbolEqualityComparer.Default);
            foreach (var iface in interfaces.OrderBy(StableKey, StringComparer.Ordinal))
            {
                var list = new List<(ITypeSymbol, StructuralMatchResult)>();
                // Most-derived first: the generated unwrap branches test `TryUnbox<C>` (an
                // `is C` check), which is also true for subtypes of C. Ordering a derived type
                // ahead of its base ensures the exact concrete type wins its own branch.
                foreach (var concrete in concreteTypes.OrderByDescending(BaseTypeDepth).ThenBy(StableKey, StringComparer.Ordinal))
                {
                    var match = CheckStructuralMatch(concrete, iface);
                    if (match.IsMatch)
                    {
                        list.Add((concrete, match));
                    }
                }
                possibleMatches[iface] = list;
            }

            // Generate proxies for all required matches
            var proxiesByNamespace = new Dictionary<string, List<(ITypeSymbol Underlying, ITypeSymbol Interface, StructuralMatchResult Match)>>();

            void AddProxy(ITypeSymbol u, ITypeSymbol i, StructuralMatchResult m) {
                var ns = NamespaceOf(u);
                if (!proxiesByNamespace.TryGetValue(ns, out var list)) {
                    list = new List<(ITypeSymbol, ITypeSymbol, StructuralMatchResult)>();
                    proxiesByNamespace[ns] = list;
                }
                if (!list.Any(x => SymbolEqualityComparer.Default.Equals(x.Underlying, u) && SymbolEqualityComparer.Default.Equals(x.Interface, i))) {
                    list.Add((u, i, m));
                }
            }

            foreach (var item in allExtensions) AddProxy(item.Candidate.UnderlyingType, item.Candidate.ExpectedInterfaceType, item.MatchResult);
            foreach (var kvp in possibleMatches) foreach (var m in kvp.Value) AddProxy(m.Concrete, kvp.Key, m.Match);

            foreach (var kvp in proxiesByNamespace.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("// <auto-generated/>");
                sb.AppendLine("using System;");
                sb.AppendLine("using NTypeForge;");
                sb.AppendLine();
                sb.AppendLine($"namespace {kvp.Key}");
                sb.AppendLine("{");
                foreach (var item in kvp.Value.OrderBy(x => StableKey(x.Underlying) + "|" + StableKey(x.Interface), StringComparer.Ordinal))
                {
                    GenerateProxyStruct(sb, item.Underlying, item.Interface, item.Match, GetProxyStructName(item.Underlying, item.Interface));
                }
                sb.AppendLine("}");
                context.AddSource($"{kvp.Key.Replace(".", "_")}_Proxies.g.cs", sb.ToString());
            }

            // Generate extensions
            var extensionsByTarget = new Dictionary<ITypeSymbol, List<ExtensionItem>>(SymbolEqualityComparer.Default);
            foreach (var item in allExtensions)
            {
                if (!extensionsByTarget.TryGetValue(item.Candidate.TargetType, out var list)) {
                    list = new List<ExtensionItem>();
                    extensionsByTarget[item.Candidate.TargetType] = list;
                }
                list.Add(item);
            }

            foreach (var kvp in extensionsByTarget.OrderBy(k => StableKey(k.Key), StringComparer.Ordinal))
            {
                var targetType = kvp.Key;
                var targetNamespace = NamespaceOf(targetType);
                var targetFullName = Fq(targetType);
                var extensionClassName = $"{Sanitize(targetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))}_DuckTypingExtensions";

                var sb = new System.Text.StringBuilder();
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

                var generatedMethods = new HashSet<string>();

                // Emit a single Duck<T>() per target type that dispatches on typeof(T).
                // One method per interface would share the identical Duck<T>() signature
                // (return type and generic constraints don't participate in overloading)
                // and collide with CS0111 when a type is ducked to more than one interface.
                var duckCandidates = new List<CandidateInvocation>();
                var seenDuckInterfaces = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                foreach (var item in kvp.Value)
                {
                    if (item.Candidate.IsDuckCall && seenDuckInterfaces.Add(item.Candidate.ExpectedInterfaceType))
                        duckCandidates.Add(item.Candidate);
                }

                if (duckCandidates.Count > 0)
                {
                    sb.AppendLine("            public T Duck<T>() where T : class");
                    sb.AppendLine("            {");
                    foreach (var candidate in duckCandidates)
                    {
                        var iface = candidate.ExpectedInterfaceType;
                        sb.AppendLine($"                if (typeof(T) == typeof({Fq(iface)}))");
                        sb.AppendLine("                {");
                        // Unwrap check: only when target is an interface can it actually be a
                        // proxy that needs rewrapping. For a concrete target the unwrap branch
                        // is always taken trivially, so we skip it and wrap directly.
                        if (targetType.TypeKind == TypeKind.Interface &&
                            possibleMatches.TryGetValue(iface, out var matches)) {
                            int ui = 0;
                            foreach (var m in matches) {
                                var local = $"c_{ui++}";
                                sb.AppendLine($"                    if (target.TryUnbox<{Fq(m.Concrete)}>(out var {local})) return (T)(object)new {ProxyFullName(m.Concrete, iface)}({local});");
                            }
                        }
                        sb.AppendLine($"                    return (T)(object)new {ProxyFullName(candidate.UnderlyingType, iface)}(({Fq(candidate.UnderlyingType)})target);");
                        sb.AppendLine("                }");
                    }
                    sb.AppendLine("                throw new global::System.InvalidOperationException(\"NTypeForge: no proxy was generated for \" + typeof(T));");
                    sb.AppendLine("            }");
                }

                foreach (var item in kvp.Value)
                {
                    var candidate = item.Candidate;
                    if (candidate.IsDuckCall) continue;

                    var originalMethod = candidate.OriginalMethod;
                    var argIndex = candidate.ArgumentIndex;
                    var argName = originalMethod.Parameters[argIndex].Name;
                    var receiver = candidate.IsStatic ? targetFullName : "target";

                    // The forwarding call's argument list, with the duck-typed argument replaced
                    // by `argReplacement` and every other parameter passed through verbatim.
                    string CallArgs(string argReplacement) => string.Join(", ", originalMethod.Parameters.Select((p, idx) =>
                        idx == argIndex ? argReplacement : $"{RefPrefix(p)}{p.Name}"));

                    var methodParams = string.Join(", ", originalMethod.Parameters.Select((p, idx) =>
                        $"{RefPrefix(p)}{Fq(idx == argIndex ? candidate.ArgumentType : p.Type)} {p.Name}"));

                    var methodSig = $"{originalMethod.Name}({methodParams})";
                    if (!generatedMethods.Add(methodSig)) continue;

                    var isStatic = candidate.IsStatic ? "static " : "";
                    sb.AppendLine($"            public {isStatic}{Fq(originalMethod.ReturnType)} {originalMethod.Name}({methodParams})");
                    sb.AppendLine("            {");

                    // Unwrap branches only make sense when the incoming value can actually
                    // be a proxy, i.e. when its static type is an interface. For a concrete
                    // argument type they are dead branches and force a needless box (TryUnbox
                    // is an extension on object), so we skip straight to the direct wrap.
                    if (candidate.ArgumentType.TypeKind == TypeKind.Interface &&
                        possibleMatches.TryGetValue(candidate.ExpectedInterfaceType, out var matches)) {
                        int ui = 0;
                        foreach (var m in matches) {
                            var local = $"c_{ui++}";
                            var call = $"{receiver}.{originalMethod.Name}({CallArgs($"new {ProxyFullName(m.Concrete, candidate.ExpectedInterfaceType)}({local})")})";
                            sb.AppendLine($"                if ({argName}.TryUnbox<{Fq(m.Concrete)}>(out var {local})) {{");
                            sb.AppendLine($"                    {(originalMethod.ReturnType.SpecialType == SpecialType.System_Void ? $"{call}; return;" : $"return {call};")}");
                            sb.AppendLine("                }");
                        }
                    }

                    var directCall = $"{receiver}.{originalMethod.Name}({CallArgs($"new {ProxyFullName(candidate.UnderlyingType, candidate.ExpectedInterfaceType)}(({Fq(candidate.UnderlyingType)}){argName})")})";
                    sb.AppendLine($"                {ReturnStatement(originalMethod.ReturnType, directCall)}");
                    sb.AppendLine("            }");
                }
                sb.AppendLine("        }");
                sb.AppendLine("    }");
                sb.AppendLine("}");
                // Qualify the hint with the target namespace: two target types with the same
                // simple name in different namespaces (e.g. A.Box and B.Box) would otherwise
                // produce the same hint name and crash the generator (duplicate hintName).
                context.AddSource($"{targetNamespace.Replace(".", "_")}_{extensionClassName}.g.cs", sb.ToString());
            }
        }

        private static void GenerateProxyStruct(System.Text.StringBuilder sb, ITypeSymbol underlyingType, ITypeSymbol interfaceType, StructuralMatchResult matchResult, string proxyStructName)
        {
            var interfaceFullName = Fq(interfaceType);
            var underlyingFullName = Fq(underlyingType);

            sb.AppendLine($"    internal readonly struct {proxyStructName} : {interfaceFullName}, IProxy<{underlyingFullName}>");
            sb.AppendLine("    {");
            sb.AppendLine($"        private readonly {underlyingFullName} _instance;");
            sb.AppendLine();
            sb.AppendLine($"        public {proxyStructName}({underlyingFullName} instance)");
            sb.AppendLine("        {");
            sb.AppendLine("            _instance = instance;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        public {underlyingFullName} Inner => _instance;");
            sb.AppendLine($"        object IProxy.Unwrapped => _instance;");
            sb.AppendLine();

            foreach (var method in matchResult.MatchedMethods)
            {
                var parametersStr = string.Join(", ", method.Parameters.Select(p => $"{RefPrefix(p)}{Fq(p.Type)} {p.Name}"));
                var argsStr = string.Join(", ", method.Parameters.Select(p => $"{RefPrefix(p)}{p.Name}"));

                sb.AppendLine($"        public {Fq(method.ReturnType)} {method.Name}({parametersStr})");
                sb.AppendLine("        {");
                sb.AppendLine($"            {ReturnStatement(method.ReturnType, $"_instance.{method.Name}({argsStr})")}");
                sb.AppendLine("        }");
            }
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // Returns the first interface member that cannot be proxied (property, event,
        // or indexer). Accessor methods are reported via their owning property/event.
        private static ISymbol? FindUnsupportedInterfaceMember(ITypeSymbol interfaceType)
        {
            // A proxy declared to implement `interfaceType` must satisfy members inherited
            // from base interfaces too, so scan the full transitive set.
            foreach (var iface in new[] { interfaceType }.Concat(interfaceType.AllInterfaces))
            {
                foreach (var member in iface.GetMembers())
                {
                    if (member is IPropertySymbol || member is IEventSymbol)
                    {
                        return member;
                    }
                }
            }
            return null;
        }

        private static StructuralMatchResult CheckStructuralMatch(ITypeSymbol sourceType, ITypeSymbol interfaceType)
        {
            var matchedMethods = new List<ProxyMethodInfo>();
            var seenSignatures = new HashSet<string>(StringComparer.Ordinal);

            // Include methods inherited from base interfaces: the proxy must implement every
            // member `interfaceType` transitively requires, or the generated struct fails
            // CS0535. The direct interface is scanned first so a re-declared (shadowing)
            // member wins over the inherited one of the same signature.
            var interfaceMethods = new[] { interfaceType }.Concat(interfaceType.AllInterfaces)
                .SelectMany(i => i.GetMembers())
                .OfType<IMethodSymbol>()
                .Where(m => m.MethodKind == MethodKind.Ordinary);

            foreach (var expectedMethod in interfaceMethods)
            {
                if (!seenSignatures.Add(MethodSignatureKey(expectedMethod)))
                {
                    continue;
                }

                var matchingMethod = sourceType.GetMembers(expectedMethod.Name)
                    .OfType<IMethodSymbol>()
                    .FirstOrDefault(m => AreMethodsCompatible(expectedMethod, m));

                if (matchingMethod == null)
                {
                    return new StructuralMatchResult(false, new List<ProxyMethodInfo>());
                }

                matchedMethods.Add(new ProxyMethodInfo(expectedMethod.Name, expectedMethod.ReturnType, expectedMethod.Parameters));
            }

            return new StructuralMatchResult(true, matchedMethods);
        }

        private static bool AreMethodsCompatible(IMethodSymbol expected, IMethodSymbol actual)
        {
            if (!SymbolEqualityComparer.Default.Equals(expected.ReturnType, actual.ReturnType)) return false;
            if (expected.Parameters.Length != actual.Parameters.Length) return false;

            for (int i = 0; i < expected.Parameters.Length; i++)
            {
                if (!SymbolEqualityComparer.Default.Equals(expected.Parameters[i].Type, actual.Parameters[i].Type)) return false;
                if (expected.Parameters[i].RefKind != actual.Parameters[i].RefKind) return false;
            }

            return true;
        }

        // Fully-qualified display string (global::Ns.Type). Used throughout the emitted
        // code and as a stable, content-based ordering key, so generated output does not
        // depend on the iteration order of symbol-keyed collections.
        private static string Fq(ITypeSymbol type)
            => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        private static string StableKey(ITypeSymbol type) => Fq(type);

        // The namespace a generated artifact for `type` is emitted into (global-namespace
        // types go to NTypeForge).
        private static string NamespaceOf(ITypeSymbol type)
            => type.ContainingNamespace.IsGlobalNamespace ? "NTypeForge" : type.ContainingNamespace.ToDisplayString();

        // Fully-qualified name of the proxy struct that wraps `underlying` as `iface`.
        private static string ProxyFullName(ITypeSymbol underlying, ITypeSymbol iface)
            => $"{NamespaceOf(underlying)}.{GetProxyStructName(underlying, iface)}";

        // The `ref`/`out`/`in ` prefix (with trailing space) for a parameter, or "".
        private static string RefPrefix(IParameterSymbol p)
            => p.RefKind switch { RefKind.Ref => "ref ", RefKind.Out => "out ", RefKind.In => "in ", _ => "" };

        // `expr;` for a void return, `return expr;` otherwise.
        private static string ReturnStatement(ITypeSymbol returnType, string call)
            => returnType.SpecialType == SpecialType.System_Void ? $"{call};" : $"return {call};";

        // Replaces the characters that are illegal in a C# identifier (for type names
        // embedded in generated struct/class names).
        private static string Sanitize(string name)
            => name.Replace(".", "_").Replace("<", "_").Replace(">", "_");

        // Number of base types, used to order more-derived types ahead of their bases.
        private static int BaseTypeDepth(ITypeSymbol type)
        {
            int depth = 0;
            for (var b = type.BaseType; b != null; b = b.BaseType) depth++;
            return depth;
        }

        // Name + parameter shape, excluding return type, so overloads that differ only by
        // return type (re-abstraction across base interfaces) collapse to one entry.
        private static string MethodSignatureKey(IMethodSymbol method)
        {
            var parameters = string.Join(",", method.Parameters.Select(p => $"{p.RefKind}:{Fq(p.Type)}"));
            return $"{method.Name}({parameters})";
        }

        private static string GetProxyStructName(ITypeSymbol sourceType, ITypeSymbol interfaceType)
        {
            var sName = Sanitize(sourceType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            var iName = Sanitize(interfaceType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            return $"{sName}_{iName}_Proxy";
        }
    }
}

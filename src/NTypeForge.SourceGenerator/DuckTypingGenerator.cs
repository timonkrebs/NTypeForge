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
            // TODO(perf/caching): CandidateInvocation carries ITypeSymbol/syntax nodes, and
            // we Combine with the whole CompilationProvider below. Roslyn symbols are not
            // value-equatable and root the compilation, so the incremental cache never hits
            // and Execute re-runs on every edit. Follow-up: project an equatable primitive
            // model in the transform stage and keep symbols out of the cached pipeline.
            var candidateInvocations = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (s, _) => s is InvocationExpressionSyntax,
                    transform: static (ctx, _) => GetCandidateInvocation(ctx))
                .Where(static c => c != null)
                .Select(static (c, _) => c!.Value);

            var compilationAndCandidates = context.CompilationProvider.Combine(candidateInvocations.Collect());

            context.RegisterSourceOutput(compilationAndCandidates, static (spc, source) => Execute(spc, source.Left, source.Right));
        }

        private static ITypeSymbol GetUnderlyingType(ITypeSymbol type)
        {
            bool IsProxyInterface(ITypeSymbol t)
            {
                if (t is INamedTypeSymbol nt && nt.IsGenericType && nt.Name == "IProxy" && nt.TypeArguments.Length == 1)
                {
                    var ns = nt.ContainingNamespace;
                    return ns != null && ns.Name == "NTypeForge" && (ns.ContainingNamespace == null || ns.ContainingNamespace.IsGlobalNamespace);
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
                resolvedMethod.ContainingNamespace.Name == "NTypeForge" && resolvedMethod.TypeArguments.Length == 1)
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
                        if (targetInterface.TypeKind == TypeKind.Interface &&
                            (underlyingType.TypeKind == TypeKind.Class || underlyingType.TypeKind == TypeKind.Struct || underlyingType.TypeKind == TypeKind.Interface))
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
                        if (paramType.TypeKind == TypeKind.Interface &&
                            (underlyingType.TypeKind == TypeKind.Class || underlyingType.TypeKind == TypeKind.Struct || underlyingType.TypeKind == TypeKind.Interface))
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

        private class TypePairComparer : IEqualityComparer<(ITypeSymbol, ITypeSymbol)>
        {
            public bool Equals((ITypeSymbol, ITypeSymbol) x, (ITypeSymbol, ITypeSymbol) y)
            {
                return SymbolEqualityComparer.Default.Equals(x.Item1, y.Item1) && SymbolEqualityComparer.Default.Equals(x.Item2, y.Item2);
            }
            public int GetHashCode((ITypeSymbol, ITypeSymbol) obj)
            {
                return (SymbolEqualityComparer.Default.GetHashCode(obj.Item1) * 397) ^ SymbolEqualityComparer.Default.GetHashCode(obj.Item2);
            }
        }

        private static void Execute(SourceProductionContext context, Compilation compilation, System.Collections.Immutable.ImmutableArray<CandidateInvocation> candidates)
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
                foreach (var concrete in concreteTypes.OrderBy(StableKey, StringComparer.Ordinal))
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
                var ns = u.ContainingNamespace.IsGlobalNamespace ? "NTypeForge" : u.ContainingNamespace.ToDisplayString();
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
                var targetNamespace = targetType.ContainingNamespace.IsGlobalNamespace ? "NTypeForge" : targetType.ContainingNamespace.ToDisplayString();
                var targetFullName = targetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var extensionClassName = $"{targetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).Replace(".", "_").Replace("<", "_").Replace(">", "_")}_DuckTypingExtensions";

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
                        var ifaceFullName = candidate.ExpectedInterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        var proxyStructName = GetProxyStructName(candidate.UnderlyingType, candidate.ExpectedInterfaceType);
                        var proxyNamespace = candidate.UnderlyingType.ContainingNamespace.IsGlobalNamespace ? "NTypeForge" : candidate.UnderlyingType.ContainingNamespace.ToDisplayString();
                        var proxyFullName = $"{proxyNamespace}.{proxyStructName}";

                        sb.AppendLine($"                if (typeof(T) == typeof({ifaceFullName}))");
                        sb.AppendLine("                {");
                        // Unwrap check: only when target is an interface can it actually be a
                        // proxy that needs rewrapping. For a concrete target the unwrap branch
                        // is always taken trivially, so we skip it and wrap directly.
                        if (targetType.TypeKind == TypeKind.Interface &&
                            possibleMatches.TryGetValue(candidate.ExpectedInterfaceType, out var matches)) {
                            foreach (var m in matches) {
                                var cName = m.Concrete.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                                var pName = $"{(m.Concrete.ContainingNamespace.IsGlobalNamespace ? "NTypeForge" : m.Concrete.ContainingNamespace.ToDisplayString())}.{GetProxyStructName(m.Concrete, candidate.ExpectedInterfaceType)}";
                                sb.AppendLine($"                    if (target.TryUnbox<{cName}>(out var c_{m.Concrete.Name})) return (T)(object)new {pName}(c_{m.Concrete.Name});");
                            }
                        }
                        sb.AppendLine($"                    return (T)(object)new {proxyFullName}(({candidate.UnderlyingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})target);");
                        sb.AppendLine("                }");
                    }
                    sb.AppendLine("                throw new global::System.InvalidOperationException(\"NTypeForge: no proxy was generated for \" + typeof(T));");
                    sb.AppendLine("            }");
                }

                foreach (var item in kvp.Value)
                {
                    var candidate = item.Candidate;
                    if (candidate.IsDuckCall) continue;

                    var proxyStructName = GetProxyStructName(candidate.UnderlyingType, candidate.ExpectedInterfaceType);
                    var proxyNamespace = candidate.UnderlyingType.ContainingNamespace.IsGlobalNamespace ? "NTypeForge" : candidate.UnderlyingType.ContainingNamespace.ToDisplayString();
                    var proxyFullName = $"{proxyNamespace}.{proxyStructName}";

                    {
                        var originalMethod = candidate.OriginalMethod;
                        var methodParams = string.Join(", ", originalMethod.Parameters.Select((p, idx) => {
                            var refKind = p.RefKind switch { RefKind.Ref => "ref ", RefKind.Out => "out ", RefKind.In => "in ", _ => "" };
                            var typeStr = (idx == candidate.ArgumentIndex) ? candidate.ArgumentType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                            return $"{refKind}{typeStr} {p.Name}";
                        }));

                        var methodSig = $"{originalMethod.Name}({methodParams})";
                        if (generatedMethods.Add(methodSig))
                        {
                            var isStatic = candidate.IsStatic ? "static " : "";
                            sb.AppendLine($"            public {isStatic}{originalMethod.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {originalMethod.Name}({methodParams})");
                            sb.AppendLine("            {");

                            var argName = originalMethod.Parameters[candidate.ArgumentIndex].Name;
                            var argType = candidate.ExpectedInterfaceType;

                            if (possibleMatches.TryGetValue(argType, out var matches)) {
                                foreach (var m in matches) {
                                    var cName = m.Concrete.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                                    var pName = $"{(m.Concrete.ContainingNamespace.IsGlobalNamespace ? "NTypeForge" : m.Concrete.ContainingNamespace.ToDisplayString())}.{GetProxyStructName(m.Concrete, argType)}";

                                    var callArgsUnboxed = string.Join(", ", originalMethod.Parameters.Select((p, idx) => {
                                        var refKind = p.RefKind switch { RefKind.Ref => "ref ", RefKind.Out => "out ", RefKind.In => "in ", _ => "" };
                                        if (idx == candidate.ArgumentIndex) return $"new {pName}(c_{m.Concrete.Name})";
                                        return $"{refKind}{p.Name}";
                                    }));
                                    var receiver = candidate.IsStatic ? targetFullName : "target";
                                    sb.AppendLine($"                if ({argName}.TryUnbox<{cName}>(out var c_{m.Concrete.Name})) {{");
                                    if (originalMethod.ReturnType.SpecialType == SpecialType.System_Void) sb.AppendLine($"                    {receiver}.{originalMethod.Name}({callArgsUnboxed}); return;");
                                    else sb.AppendLine($"                    return {receiver}.{originalMethod.Name}({callArgsUnboxed});");
                                    sb.AppendLine("                }");
                                }
                            }

                            var callArgs = string.Join(", ", originalMethod.Parameters.Select((p, idx) => {
                                var refKind = p.RefKind switch { RefKind.Ref => "ref ", RefKind.Out => "out ", RefKind.In => "in ", _ => "" };
                                if (idx == candidate.ArgumentIndex) return $"new {proxyFullName}(({candidate.UnderlyingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){argName})";
                                return $"{refKind}{p.Name}";
                            }));
                            var receiverFinal = candidate.IsStatic ? targetFullName : "target";
                            if (originalMethod.ReturnType.SpecialType == SpecialType.System_Void)
                                sb.AppendLine($"                {receiverFinal}.{originalMethod.Name}({callArgs});");
                            else
                                sb.AppendLine($"                return {receiverFinal}.{originalMethod.Name}({callArgs});");
                            sb.AppendLine("            }");
                        }
                    }
                }
                sb.AppendLine("        }");
                sb.AppendLine("    }");
                sb.AppendLine("}");
                context.AddSource(extensionClassName + ".g.cs", sb.ToString());
            }
        }

        private static void GenerateProxyStruct(System.Text.StringBuilder sb, ITypeSymbol underlyingType, ITypeSymbol interfaceType, StructuralMatchResult matchResult, string proxyStructName)
        {
            var interfaceFullName = interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var underlyingFullName = underlyingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

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
                var returnTypeStr = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var parametersStr = string.Join(", ", method.Parameters.Select(p => {
                    var refKind = p.RefKind switch { RefKind.Ref => "ref ", RefKind.Out => "out ", RefKind.In => "in ", _ => "" };
                    return $"{refKind}{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}";
                }));
                var argsStr = string.Join(", ", method.Parameters.Select(p => {
                    var refKind = p.RefKind switch { RefKind.Ref => "ref ", RefKind.Out => "out ", RefKind.In => "in ", _ => "" };
                    return $"{refKind}{p.Name}";
                }));

                sb.AppendLine($"        public {returnTypeStr} {method.Name}({parametersStr})");
                sb.AppendLine("        {");
                if (method.ReturnType.SpecialType == SpecialType.System_Void) sb.AppendLine($"            _instance.{method.Name}({argsStr});");
                else sb.AppendLine($"            return _instance.{method.Name}({argsStr});");
                sb.AppendLine("        }");
            }
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // Returns the first interface member that cannot be proxied (property, event,
        // or indexer). Accessor methods are reported via their owning property/event.
        private static ISymbol? FindUnsupportedInterfaceMember(ITypeSymbol interfaceType)
        {
            foreach (var member in interfaceType.GetMembers())
            {
                if (member is IPropertySymbol || member is IEventSymbol)
                {
                    return member;
                }
            }
            return null;
        }

        private static StructuralMatchResult CheckStructuralMatch(ITypeSymbol sourceType, ITypeSymbol interfaceType)
        {
            var matchedMethods = new List<ProxyMethodInfo>();
            var interfaceMethods = interfaceType.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary);

            foreach (var expectedMethod in interfaceMethods)
            {
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

        // Stable, content-based ordering key so generated output does not depend on
        // the (unspecified) iteration order of symbol-keyed hash sets/dictionaries.
        private static string StableKey(ITypeSymbol type)
            => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        private static string GetProxyStructName(ITypeSymbol sourceType, ITypeSymbol interfaceType)
        {
            var sName = sourceType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).Replace(".", "_").Replace("<", "_").Replace(">", "_");
            var iName = interfaceType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).Replace(".", "_").Replace("<", "_").Replace(">", "_");
            return $"{sName}_{iName}_Proxy";
        }
    }
}

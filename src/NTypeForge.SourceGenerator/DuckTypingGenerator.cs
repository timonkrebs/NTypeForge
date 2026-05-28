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
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var candidateInvocations = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (s, _) => s is InvocationExpressionSyntax,
                    transform: static (ctx, _) => GetCandidateInvocation(ctx))
                .Where(static c => c != null)
                .Select(static (c, _) => c!.Value);

            var compilationAndCandidates = context.CompilationProvider.Combine(candidateInvocations.Collect());

            context.RegisterSourceOutput(compilationAndCandidates, static (spc, source) => Execute(spc, source.Left, source.Right));
        }

        private static ITypeSymbol GetUnderlyingType(ITypeSymbol type, out bool isProxy)
        {
            isProxy = false;

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
                isProxy = true;
                return ((INamedTypeSymbol)type).TypeArguments[0];
            }

            foreach (var iface in type.AllInterfaces)
            {
                if (IsProxyInterface(iface))
                {
                    isProxy = true;
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
                        var underlyingType = GetUnderlyingType(argType, out bool isProxy);
                        if (targetInterface.TypeKind == TypeKind.Interface &&
                            (underlyingType.TypeKind == TypeKind.Class || underlyingType.TypeKind == TypeKind.Struct || underlyingType.TypeKind == TypeKind.Interface))
                        {
                            return new CandidateInvocation(
                                invocation,
                                "Duck",
                                argType,
                                argType,
                                underlyingType,
                                targetInterface,
                                0,
                                resolvedMethod,
                                false,
                                isProxy,
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

                    var underlyingType = GetUnderlyingType(argType, out bool isProxy);

                    var conversion = semanticModel.ClassifyConversion(arg.Expression, paramType);
                    if (!conversion.Exists || !conversion.IsImplicit)
                    {
                        if (paramType.TypeKind == TypeKind.Interface &&
                            (underlyingType.TypeKind == TypeKind.Class || underlyingType.TypeKind == TypeKind.Struct || underlyingType.TypeKind == TypeKind.Interface))
                        {
                            return new CandidateInvocation(
                                invocation,
                                candidate.Name,
                                targetType,
                                argType,
                                underlyingType,
                                paramType,
                                i,
                                candidate,
                                candidate.IsStatic,
                                isProxy,
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
            }

            if (allExtensions.Count == 0) return;

            // Collect all possible concrete-to-interface matches among involved types
            var possibleMatches = new Dictionary<ITypeSymbol, List<(ITypeSymbol Concrete, StructuralMatchResult Match)>>(SymbolEqualityComparer.Default);
            foreach (var iface in interfaces)
            {
                var list = new List<(ITypeSymbol, StructuralMatchResult)>();
                foreach (var concrete in concreteTypes)
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

            foreach (var kvp in proxiesByNamespace)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("// <auto-generated/>");
                sb.AppendLine("using System;");
                sb.AppendLine("using NTypeForge;");
                sb.AppendLine();
                sb.AppendLine($"namespace {kvp.Key}");
                sb.AppendLine("{");
                foreach (var item in kvp.Value)
                {
                    GenerateProxyStruct(sb, item.Underlying, item.Interface, item.Match, GetProxyStructName(item.Underlying, item.Interface));
                }
                sb.AppendLine("}");
                context.AddSource($"{kvp.Key.Replace(".", "_")}_Proxies.g.cs", sb.ToString());
            }

            // Generate extensions
            var extensionsByTarget = new Dictionary<ITypeSymbol?, List<ExtensionItem>>(SymbolEqualityComparer.Default);
            foreach (var item in allExtensions)
            {
                if (!extensionsByTarget.TryGetValue(item.Candidate.TargetType, out var list)) {
                    list = new List<ExtensionItem>();
                    extensionsByTarget[item.Candidate.TargetType] = list;
                }
                list.Add(item);
            }

            foreach (var kvp in extensionsByTarget)
            {
                var targetType = kvp.Key;
                var targetNamespace = (targetType?.ContainingNamespace.IsGlobalNamespace ?? true) ? "NTypeForge" : targetType.ContainingNamespace.ToDisplayString();
                var targetFullName = targetType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "object";
                var extensionClassName = targetType != null ? $"{targetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).Replace(".", "_").Replace("<", "_").Replace(">", "_")}_DuckTypingExtensions" : "Global_DuckTypingExtensions";

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
                foreach (var item in kvp.Value)
                {
                    var candidate = item.Candidate;
                    var proxyStructName = GetProxyStructName(candidate.UnderlyingType, candidate.ExpectedInterfaceType);
                    var proxyNamespace = candidate.UnderlyingType.ContainingNamespace.IsGlobalNamespace ? "NTypeForge" : candidate.UnderlyingType.ContainingNamespace.ToDisplayString();
                    var proxyFullName = $"{proxyNamespace}.{proxyStructName}";

                    if (candidate.IsDuckCall)
                    {
                        var methodSig = $"public {candidate.ExpectedInterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} Duck<T>() where T : {candidate.ExpectedInterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}";
                        if (generatedMethods.Add(methodSig))
                        {
                            sb.AppendLine($"            public {candidate.ExpectedInterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} Duck<T>() where T : {candidate.ExpectedInterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}");
                            sb.AppendLine("            {");
                            // Unbox checks
                            if (possibleMatches.TryGetValue(candidate.ExpectedInterfaceType, out var matches)) {
                                foreach (var m in matches) {
                                    var cName = m.Concrete.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                                    var pName = $"{(m.Concrete.ContainingNamespace.IsGlobalNamespace ? "NTypeForge" : m.Concrete.ContainingNamespace.ToDisplayString())}.{GetProxyStructName(m.Concrete, candidate.ExpectedInterfaceType)}";
                                    sb.AppendLine($"                if (target.Unbox<{cName}>() is {cName} c_{m.Concrete.Name}) return new {pName}(c_{m.Concrete.Name});");
                                }
                            }
                            sb.AppendLine($"                return new {proxyFullName}(({candidate.UnderlyingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})target);");
                            sb.AppendLine("            }");
                        }
                    }
                    else
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
                                    sb.AppendLine($"                if ({argName}.Unbox<{cName}>() is {cName} c_{m.Concrete.Name}) {{");
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

        private static string GetProxyStructName(ITypeSymbol sourceType, ITypeSymbol interfaceType)
        {
            var sName = sourceType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).Replace(".", "_").Replace("<", "_").Replace(">", "_");
            var iName = interfaceType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).Replace(".", "_").Replace("<", "_").Replace(">", "_");
            return $"{sName}_{iName}_Proxy";
        }
    }
}

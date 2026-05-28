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
        // Find all method invocations that fail to compile
        var candidateInvocations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => s is InvocationExpressionSyntax,
                transform: static (ctx, _) => GetCandidateInvocation(ctx))
            .Where(static c => c != null)
            .Select(static (c, _) => c!.Value);

        var compilationAndCandidates = context.CompilationProvider.Combine(candidateInvocations.Collect());

        context.RegisterSourceOutput(compilationAndCandidates, static (spc, source) => Execute(spc, source.Left, source.Right));
    }

    private static CandidateInvocation? GetCandidateInvocation(GeneratorSyntaxContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var semanticModel = context.SemanticModel;

        // We are looking for invocations that have compiler errors
        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
        if (symbolInfo.Symbol != null)
        {
            // If the symbol resolved perfectly, it doesn't need our help
            return null;
        }

        // We need candidate symbols (methods that failed overload resolution)
        if (symbolInfo.CandidateSymbols.Length == 0)
        {
            return null;
        }

        // Check if any candidate failed because of an argument type mismatch where duck typing could help
        foreach (var candidate in symbolInfo.CandidateSymbols.OfType<IMethodSymbol>())
        {
            // We support generating extensions for instance methods or static methods
            // Find the receiver type: it's the class/struct defining the method.
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

                // Check if the argument fails standard conversion to the parameter type
                var conversion = semanticModel.ClassifyConversion(arg.Expression, paramType);
                if (!conversion.Exists || !conversion.IsImplicit)
                {
                    // It failed! Is the parameter an interface, and the arg type a class/struct?
                    if (paramType.TypeKind == TypeKind.Interface && (argType.TypeKind == TypeKind.Class || argType.TypeKind == TypeKind.Struct))
                    {
                        // Potential match!
                        return new CandidateInvocation(
                            invocation,
                            candidate.Name,
                            targetType,
                            argType,
                            paramType,
                            i,
                            candidate,
                            candidate.IsStatic
                        );
                    }
                }
            }
        }

        return null;
    }

        private static void Execute(SourceProductionContext context, Compilation compilation, System.Collections.Immutable.ImmutableArray<CandidateInvocation> candidates)
        {
            if (candidates.IsDefaultOrEmpty)
                return;

            // Group by the target type to avoid duplicate class names
            foreach (var group in candidates.GroupBy(c => c.TargetType, SymbolEqualityComparer.Default))
            {
                var targetType = (ITypeSymbol)group.Key!;
                var extensionsToGenerate = new System.Collections.Generic.List<(CandidateInvocation candidate, StructuralMatchResult matchResult)>();
                var generatedHints = new System.Collections.Generic.HashSet<string>();

                foreach (var candidate in group)
                {
                    var matchResult = CheckStructuralMatch(candidate.ArgumentType, candidate.ExpectedInterfaceType);
                    if (matchResult.IsMatch)
                    {
                        // Ensure we don't generate the same extension method multiple times
                        var methodHint = $"{candidate.OriginalMethod.Name}_{candidate.ArgumentType.Name}";
                        if (generatedHints.Add(methodHint))
                        {
                            extensionsToGenerate.Add((candidate, matchResult));
                        }
                    }
                }

                if (extensionsToGenerate.Count > 0)
                {
                    GenerateProxyAndExtension(context, targetType, extensionsToGenerate);
                }
            }
        }

        private static StructuralMatchResult CheckStructuralMatch(ITypeSymbol sourceType, ITypeSymbol interfaceType)
        {
            var matchedMethods = new System.Collections.Generic.List<ProxyMethodInfo>();
            var interfaceMethods = interfaceType.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary);

            foreach (var expectedMethod in interfaceMethods)
            {
                // Simple structural check for matching method signatures
                var matchingMethod = sourceType.GetMembers(expectedMethod.Name)
                    .OfType<IMethodSymbol>()
                    .FirstOrDefault(m => AreMethodsCompatible(expectedMethod, m));

                if (matchingMethod == null)
                {
                    return new StructuralMatchResult(false, new System.Collections.Generic.List<ProxyMethodInfo>());
                }

                matchedMethods.Add(new ProxyMethodInfo(expectedMethod.Name, expectedMethod.ReturnType, expectedMethod.Parameters));
            }

            return new StructuralMatchResult(true, matchedMethods);
        }

        private static bool AreMethodsCompatible(IMethodSymbol expected, IMethodSymbol actual)
        {
            if (!SymbolEqualityComparer.Default.Equals(expected.ReturnType, actual.ReturnType))
                return false;

            if (expected.Parameters.Length != actual.Parameters.Length)
                return false;

            for (int i = 0; i < expected.Parameters.Length; i++)
            {
                if (!SymbolEqualityComparer.Default.Equals(expected.Parameters[i].Type, actual.Parameters[i].Type))
                    return false;
                if (expected.Parameters[i].RefKind != actual.Parameters[i].RefKind)
                    return false;
            }

            return true;
        }

        private static void GenerateProxyAndExtension(SourceProductionContext context, ITypeSymbol targetType, System.Collections.Generic.List<(CandidateInvocation candidate, StructuralMatchResult matchResult)> extensions)
        {
            var extensionClassName = $"{targetType.Name}_DuckTypingExtensions";
            var targetNamespace = targetType.ContainingNamespace.IsGlobalNamespace ? "" : targetType.ContainingNamespace.ToDisplayString();
            var targetFullName = targetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            var sb = new System.Text.StringBuilder();

            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("using System;");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(targetNamespace))
            {
                sb.AppendLine($"namespace {targetNamespace}");
                sb.AppendLine("{");
            }

            // Generate Proxy Structs uniquely
            var generatedProxies = new System.Collections.Generic.HashSet<string>();

            foreach (var item in extensions)
            {
                var sourceType = item.candidate.ArgumentType;
                var interfaceType = item.candidate.ExpectedInterfaceType;

                var sourceTypeName = sourceType.Name;
                var interfaceTypeName = interfaceType.Name;
                var proxyStructName = $"{sourceTypeName}_{interfaceTypeName}_Proxy";

                if (generatedProxies.Add(proxyStructName))
                {
                    var interfaceFullName = interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    var sourceFullName = sourceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                    sb.AppendLine($"    internal readonly struct {proxyStructName} : {interfaceFullName}");
                    sb.AppendLine("    {");
                    sb.AppendLine($"        private readonly {sourceFullName} _instance;");
                    sb.AppendLine();
                    sb.AppendLine($"        public {proxyStructName}({sourceFullName} instance)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            _instance = instance;");
                    sb.AppendLine("        }");
                    sb.AppendLine();

                    foreach (var method in item.matchResult.MatchedMethods)
                    {
                        var returnTypeStr = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                        var parametersStr = string.Join(", ", method.Parameters.Select(p => {
                            var refKind = p.RefKind switch {
                                RefKind.Ref => "ref ",
                                RefKind.Out => "out ",
                                RefKind.In => "in ",
                                _ => ""
                            };
                            return $"{refKind}{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}";
                        }));

                        var argsStr = string.Join(", ", method.Parameters.Select(p => {
                            var refKind = p.RefKind switch {
                                RefKind.Ref => "ref ",
                                RefKind.Out => "out ",
                                RefKind.In => "in ",
                                _ => ""
                            };
                            return $"{refKind}{p.Name}";
                        }));

                        sb.AppendLine($"        public {returnTypeStr} {method.Name}({parametersStr})");
                        sb.AppendLine("        {");
                        if (method.ReturnType.SpecialType == SpecialType.System_Void)
                        {
                            sb.AppendLine($"            _instance.{method.Name}({argsStr});");
                        }
                        else
                        {
                            sb.AppendLine($"            return _instance.{method.Name}({argsStr});");
                        }
                        sb.AppendLine("        }");
                    }
                    sb.AppendLine("    }");
                    sb.AppendLine();
                }
            }

            // Generate Extension Type
            sb.AppendLine($"    public static class {extensionClassName}");
            sb.AppendLine("    {");
            var receiverTargetName = "target";
            sb.AppendLine($"        extension ({targetFullName} {receiverTargetName})");
            sb.AppendLine("        {");

            var generatedExtensions = new System.Collections.Generic.HashSet<string>();

            foreach (var item in extensions)
            {
                var candidate = item.candidate;
                var originalMethod = candidate.OriginalMethod;
                var sourceType = candidate.ArgumentType;
                var interfaceType = candidate.ExpectedInterfaceType;
                var proxyStructName = $"{sourceType.Name}_{interfaceType.Name}_Proxy";
                var sourceFullName = sourceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                // Build parameters for the extension method
                var extParams = new System.Collections.Generic.List<string>();

                for (int i = 0; i < originalMethod.Parameters.Length; i++)
                {
                    var p = originalMethod.Parameters[i];
                    var refKind = p.RefKind switch {
                        RefKind.Ref => "ref ",
                        RefKind.Out => "out ",
                        RefKind.In => "in ",
                        _ => ""
                    };

                    if (i == candidate.ArgumentIndex)
                    {
                        extParams.Add($"{sourceFullName} {p.Name}");
                    }
                    else
                    {
                        extParams.Add($"{refKind}{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}");
                    }
                }

                var extParamsStr = string.Join(", ", extParams);
                var extReturnTypeStr = originalMethod.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var methodName = originalMethod.Name;
                var isStaticStr = candidate.IsStatic ? "static " : "";
                var receiverStr = candidate.IsStatic ? targetFullName : receiverTargetName;

                var methodSignature = $"{isStaticStr}{extReturnTypeStr} {methodName}({extParamsStr})";
                if (!generatedExtensions.Add(methodSignature))
                {
                    continue;
                }

                sb.AppendLine($"            public {isStaticStr}{extReturnTypeStr} {methodName}({extParamsStr})");
                sb.AppendLine("            {");

                // Build arguments for calling the original method
                var callArgs = new System.Collections.Generic.List<string>();
                for (int i = 0; i < originalMethod.Parameters.Length; i++)
                {
                    var p = originalMethod.Parameters[i];
                    var refKind = p.RefKind switch {
                        RefKind.Ref => "ref ",
                        RefKind.Out => "out ",
                        RefKind.In => "in ",
                        _ => ""
                    };

                    if (i == candidate.ArgumentIndex)
                    {
                        callArgs.Add($"new {proxyStructName}({p.Name})");
                    }
                    else
                    {
                        callArgs.Add($"{refKind}{p.Name}");
                    }
                }

                var callArgsStr = string.Join(", ", callArgs);

                if (originalMethod.ReturnType.SpecialType == SpecialType.System_Void)
                {
                    sb.AppendLine($"                {receiverStr}.{methodName}({callArgsStr});");
                }
                else
                {
                    sb.AppendLine($"                return {receiverStr}.{methodName}({callArgsStr});");
                }

                sb.AppendLine("            }");
            }

            sb.AppendLine("        }");
            sb.AppendLine("    }");

            if (!string.IsNullOrEmpty(targetNamespace))
            {
                sb.AppendLine("}");
            }

            context.AddSource($"{targetType.Name}_DuckTyping.g.cs", sb.ToString());
        }
    }
}

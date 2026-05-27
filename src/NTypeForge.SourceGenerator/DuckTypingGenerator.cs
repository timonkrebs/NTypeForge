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
            // We only support generating extensions for instance methods or static methods
            // Find the receiver type
            ITypeSymbol? targetType = null;
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var receiverInfo = semanticModel.GetTypeInfo(memberAccess.Expression);
                targetType = receiverInfo.Type;
            }
            else if (invocation.Expression is IdentifierNameSyntax)
            {
                // Implicit this
                targetType = semanticModel.GetEnclosingSymbol(invocation.SpanStart)?.ContainingType;
            }

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
                            candidate
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

            var generatedHints = new System.Collections.Generic.HashSet<string>();

            foreach (var candidate in candidates)
            {
                var matchResult = CheckStructuralMatch(candidate.ArgumentType, candidate.ExpectedInterfaceType);
                if (matchResult.IsMatch)
                {
                    var hintName = $"{candidate.TargetType.Name}_{candidate.OriginalMethod.Name}_{candidate.ArgumentType.Name}_DuckTyping.g.cs";
                    if (generatedHints.Add(hintName))
                    {
                        GenerateProxyAndExtension(context, candidate, matchResult, hintName);
                    }
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
            }

            return true;
        }

        private static void GenerateProxyAndExtension(SourceProductionContext context, CandidateInvocation candidate, StructuralMatchResult matchResult, string hintName)
        {
            var sourceType = candidate.ArgumentType;
            var interfaceType = candidate.ExpectedInterfaceType;
            var targetType = candidate.TargetType;
            var originalMethod = candidate.OriginalMethod;

            var sourceTypeName = sourceType.Name;
            var interfaceTypeName = interfaceType.Name;
            var proxyStructName = $"{sourceTypeName}_{interfaceTypeName}_Proxy";
            var extensionClassName = $"{targetType.Name}_DuckTypingExtensions";

            var sb = new System.Text.StringBuilder();

            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("using System;");
            sb.AppendLine();

            var targetNamespace = targetType.ContainingNamespace.IsGlobalNamespace ? "" : targetType.ContainingNamespace.ToDisplayString();
            if (!string.IsNullOrEmpty(targetNamespace))
            {
                sb.AppendLine($"namespace {targetNamespace}");
                sb.AppendLine("{");
            }

            // Generate Proxy Struct
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

            foreach (var method in matchResult.MatchedMethods)
            {
                var returnTypeStr = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var parametersStr = string.Join(", ", method.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}"));
                var argsStr = string.Join(", ", method.Parameters.Select(p => p.Name));

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

            // Generate Extension Method
            var targetFullName = targetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            // Build parameters for the extension method
            var extParams = new System.Collections.Generic.List<string>();
            extParams.Add($"this {targetFullName} target");

            for (int i = 0; i < originalMethod.Parameters.Length; i++)
            {
                var p = originalMethod.Parameters[i];
                if (i == candidate.ArgumentIndex)
                {
                    extParams.Add($"{sourceFullName} {p.Name}");
                }
                else
                {
                    extParams.Add($"{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}");
                }
            }

            var extParamsStr = string.Join(", ", extParams);
            var extReturnTypeStr = originalMethod.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var methodName = originalMethod.Name;

            sb.AppendLine($"    public static class {extensionClassName}");
            sb.AppendLine("    {");
            sb.AppendLine($"        public static {extReturnTypeStr} {methodName}({extParamsStr})");
            sb.AppendLine("        {");

            // Build arguments for calling the original method
            var callArgs = new System.Collections.Generic.List<string>();
            for (int i = 0; i < originalMethod.Parameters.Length; i++)
            {
                var p = originalMethod.Parameters[i];
                if (i == candidate.ArgumentIndex)
                {
                    callArgs.Add($"new {proxyStructName}({p.Name})");
                }
                else
                {
                    callArgs.Add(p.Name);
                }
            }

            var callArgsStr = string.Join(", ", callArgs);

            if (originalMethod.ReturnType.SpecialType == SpecialType.System_Void)
            {
                sb.AppendLine($"            target.{methodName}({callArgsStr});");
            }
            else
            {
                sb.AppendLine($"            return target.{methodName}({callArgsStr});");
            }

            sb.AppendLine("        }");
            sb.AppendLine("    }");

            if (!string.IsNullOrEmpty(targetNamespace))
            {
                sb.AppendLine("}");
            }

            context.AddSource(hintName, sb.ToString());
        }
    }
}

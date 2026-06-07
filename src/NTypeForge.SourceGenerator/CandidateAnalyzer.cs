using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NTypeForge.SourceGenerator.Models;

namespace NTypeForge.SourceGenerator
{
    // Transform stage (symbol-aware): detect a duck-typing site in one invocation and resolve it to
    // a value-equatable CandidateModel built entirely from primitives (strings/enums/spans). No
    // ISymbol or SyntaxNode is retained, so the incremental pipeline does not root the compilation.
    // Member analysis is delegated to InterfaceRequirementsAnalyzer / SurfaceAnalyzer / MemberSignatures;
    // this type owns only the "is this a duck site, and against what types?" decision plus assembly
    // of the model. Returns null for invocations that are not duck-typing sites.
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

            // The instance already satisfies the interface (nominally or via variance), so no proxy
            // is needed: the runtime Duck<T> fallback's `instance is T` returns it directly.
            // Generating a proxy here would only add a needless wrap/box.
            if (AlreadyImplements(semanticModel, argType, targetInterface)) return null;

            return BuildModel(
                invocation, target: argType, argType: argType, underlyingType: underlyingType,
                interfaceType: targetInterface, argumentIndex: 0, isStatic: false, isDuckCall: true,
                originalMethod: null, isUnambiguousDuckSite: true);
        }

        private static bool AlreadyImplements(SemanticModel semanticModel, ITypeSymbol type, ITypeSymbol interfaceType)
        {
            // Identity or an implicit reference conversion (which covers nominal implementation AND
            // variance, e.g. ICovariant<string> is-a ICovariant<object>) means the value already
            // is the interface at runtime.
            var conversion = semanticModel.Compilation.ClassifyConversion(type, interfaceType);
            return conversion.IsIdentity || (conversion.IsImplicit && conversion.IsReference);
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
            var requirements = InterfaceRequirementsAnalyzer.Analyze(interfaceType);

            var surface = SurfaceAnalyzer.BuildSurfaceCompatKeys(underlyingType);
            var surfaceSet = new HashSet<string>(surface, StringComparer.Ordinal);

            bool isSelfMatch = StructuralMatch.IsSatisfiedBy(
                requirements.Methods, requirements.Properties, requirements.Indexers, requirements.Events, surfaceSet);

            var originalParams = originalMethod == null
                ? (IReadOnlyList<ParamSig>)Array.Empty<ParamSig>()
                : originalMethod.Parameters.Select(MemberSignatures.ToParamSig).ToList();

            var loc = invocation.GetLocation();

            return new CandidateModel(
                targetFq: SymbolNames.Fq(target),
                targetNamespace: SymbolNames.NamespaceOf(target),
                targetMinimalName: SymbolNames.MinimalName(target),
                targetIsInterface: target.TypeKind == TypeKind.Interface,
                argumentIsInterface: argType.TypeKind == TypeKind.Interface,
                argumentFq: SymbolNames.Fq(argType),
                underlyingFq: SymbolNames.Fq(underlyingType),
                underlyingNamespace: SymbolNames.NamespaceOf(underlyingType),
                underlyingMinimalName: SymbolNames.MinimalName(underlyingType),
                underlyingIsInterface: underlyingType.TypeKind == TypeKind.Interface,
                underlyingBaseDepth: SymbolNames.BaseTypeDepth(underlyingType),
                interfaceFq: SymbolNames.Fq(interfaceType),
                interfaceMinimalName: SymbolNames.MinimalName(interfaceType),
                argumentIndex: argumentIndex,
                isStatic: isStatic,
                isDuckCall: isDuckCall,
                originalMethodName: originalMethod?.Name ?? "",
                originalReturnTypeFq: originalMethod == null ? "" : SymbolNames.Fq(originalMethod.ReturnType),
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
    }
}

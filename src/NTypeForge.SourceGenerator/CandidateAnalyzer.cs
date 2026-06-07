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

        // The underlying type kinds NTypeForge can build a proxy around. A `ref struct` is excluded:
        // it can't be a field of the (non-ref) proxy class, can't be a type argument to IProxy<T>, and
        // can't be cast to object - so proxying it would only emit code that fails to compile. Leaving
        // such a site alone lets the compiler's own (correct) error stand.
        private static bool IsProxyableKind(ITypeSymbol type)
            => !type.IsRefLikeType &&
               (type.TypeKind == TypeKind.Class || type.TypeKind == TypeKind.Struct || type.TypeKind == TypeKind.Interface);

        private static bool IsUsableFromGeneratedTopLevelCode(ITypeSymbol type)
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    return IsUsableFromGeneratedTopLevelCode(array.ElementType);
                case IPointerTypeSymbol pointer:
                    return IsUsableFromGeneratedTopLevelCode(pointer.PointedAtType);
                case INamedTypeSymbol named:
                    foreach (var arg in named.TypeArguments)
                    {
                        if (!IsUsableFromGeneratedTopLevelCode(arg)) return false;
                    }
                    for (INamedTypeSymbol? current = named; current != null; current = current.ContainingType)
                    {
                        if (!IsTypeAccessibilityUsable(current.DeclaredAccessibility)) return false;
                    }
                    return true;
                case ITypeParameterSymbol:
                    return true;
                default:
                    return true;
            }
        }

        private static bool IsTypeAccessibilityUsable(Accessibility accessibility)
            => accessibility == Accessibility.Public ||
               accessibility == Accessibility.Internal ||
               accessibility == Accessibility.ProtectedOrInternal;

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

        // An explicit `instance.Duck<T>()` call (the member-access form is the only one the generated
        // instance extension member can intercept; see GetDuckInstanceExpression).
        private static CandidateModel? TryGetDuckCall(
            InvocationExpressionSyntax invocation, SemanticModel semanticModel, SymbolInfo symbolInfo)
        {
            if (!(symbolInfo.Symbol is IMethodSymbol resolved) || resolved.Name != "Duck" ||
                !IsTopLevelNTypeForgeNamespace(resolved.ContainingNamespace) || resolved.TypeArguments.Length != 1)
                return null;

            var instanceExpr = GetDuckInstanceExpression(invocation, semanticModel);
            if (instanceExpr == null) return null;

            var argType = semanticModel.GetTypeInfo(instanceExpr).Type;
            if (argType == null) return null;

            var targetInterface = resolved.TypeArguments[0];
            var underlyingType = GetUnderlyingType(argType);
            if (targetInterface.TypeKind != TypeKind.Interface || !IsProxyableKind(underlyingType)) return null;
            if (!IsUsableFromGeneratedTopLevelCode(argType) ||
                !IsUsableFromGeneratedTopLevelCode(underlyingType) ||
                !IsUsableFromGeneratedTopLevelCode(targetInterface))
                return null;

            // The instance already satisfies the interface (nominally or via variance), so no proxy
            // is needed: the runtime Duck<T> fallback's `instance is T` returns it directly.
            // Generating a proxy here would only add a needless wrap/box.
            if (AlreadyImplements(semanticModel, argType, targetInterface)) return null;

            return BuildModel(
                invocation, target: argType, argType: argType, underlyingType: underlyingType,
                interfaceType: targetInterface, argumentIndex: 0, isStatic: false, isDuckCall: true,
                originalMethod: null);
        }

        private static bool AlreadyImplements(SemanticModel semanticModel, ITypeSymbol type, ITypeSymbol interfaceType)
        {
            // Identity or an implicit reference conversion (which covers nominal implementation AND
            // variance, e.g. ICovariant<string> is-a ICovariant<object>) means the value already
            // is the interface at runtime.
            var conversion = semanticModel.Compilation.ClassifyConversion(type, interfaceType);
            return conversion.IsIdentity || (conversion.IsImplicit && conversion.IsReference);
        }

        // The ducked instance in `x.Duck<T>()`. Only the member-access form whose receiver is a
        // *value* is a real duck site. A static-qualified `DuckExtensions.Duck<T>(x)` has the library
        // type as its receiver and can never bind to the generated instance extension member, so
        // treating it as a site would only emit a spurious NTF001 against `DuckExtensions` itself -
        // we leave it to the runtime fallback instead.
        private static ExpressionSyntax? GetDuckInstanceExpression(
            InvocationExpressionSyntax invocation, SemanticModel semanticModel)
        {
            if (!(invocation.Expression is MemberAccessExpressionSyntax memberAccess)) return null;

            var receiver = semanticModel.GetSymbolInfo(memberAccess.Expression).Symbol;
            if (receiver is ITypeSymbol or INamespaceSymbol) return null;

            return memberAccess.Expression;
        }

        // A failed call whose argument could implicitly become an interface parameter via a proxy.
        // We rewire only when the call has exactly one duckable interpretation. With more than one,
        // silently choosing a single (overload, argument) would be non-deterministic and could bind
        // the call to the wrong overload, so we leave the original (still-failing) call for the
        // compiler to report - which is also what suppresses the NTF003 near-miss on ambiguous sites.
        private static CandidateModel? TryGetMethodArgumentDuck(
            InvocationExpressionSyntax invocation, SemanticModel semanticModel, SymbolInfo symbolInfo)
        {
            var sites = DistinctInterpretations(CollectDuckableArgumentSites(invocation, semanticModel, symbolInfo));
            if (sites.Count != 1) return null;

            var (candidate, paramIndex, syntaxIndex) = sites[0];
            var argType = semanticModel.GetTypeInfo(invocation.ArgumentList.Arguments[syntaxIndex].Expression).Type!;
            var underlyingType = GetUnderlyingType(argType);
            var target = GetForwardingTarget(invocation, semanticModel, candidate);
            if (target == null ||
                !IsUsableFromGeneratedTopLevelCode(candidate.ContainingType!) ||
                !IsUsableFromGeneratedTopLevelCode(target) ||
                !IsUsableFromGeneratedTopLevelCode(argType) ||
                !IsUsableFromGeneratedTopLevelCode(underlyingType) ||
                !IsUsableFromGeneratedTopLevelCode(candidate.Parameters[paramIndex].Type))
                return null;

            return BuildModel(
                invocation, target: target, argType: argType,
                underlyingType: underlyingType, interfaceType: candidate.Parameters[paramIndex].Type,
                argumentIndex: EmittedParameterIndex(candidate, paramIndex),
                isStatic: candidate.IsStatic && !IsExtensionLike(candidate),
                isDuckCall: false,
                originalMethod: candidate);
        }

        private static bool IsExtensionLike(IMethodSymbol method)
            => method.IsExtensionMethod || method.ReducedFrom != null;

        private static bool HasExplicitExtensionReceiverParameter(IMethodSymbol method)
            => method.IsExtensionMethod && method.ReducedFrom == null;

        private static IMethodSymbol OriginalExtensionDefinition(IMethodSymbol method)
            => method.ReducedFrom ?? method;

        private static ITypeSymbol? GetForwardingTarget(
            InvocationExpressionSyntax invocation, SemanticModel semanticModel, IMethodSymbol candidate)
        {
            if (candidate.ReducedFrom != null && invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                return semanticModel.GetTypeInfo(memberAccess.Expression).Type;

            return HasExplicitExtensionReceiverParameter(candidate) && candidate.Parameters.Length > 0
                ? candidate.Parameters[0].Type
                : candidate.ContainingType;
        }

        private static int EmittedParameterIndex(IMethodSymbol candidate, int paramIndex)
            => HasExplicitExtensionReceiverParameter(candidate) ? paramIndex - 1 : paramIndex;

        // Collapses (overload, argument) sites that would generate the same forwarding method - an
        // override and the base it hides, or a symbol Roslyn lists more than once - so they don't
        // count as a false ambiguity. Genuinely distinct interpretations (different target interface
        // or different remaining parameters) survive as separate entries.
        private static List<(IMethodSymbol Candidate, int ParamIndex, int SyntaxIndex)> DistinctInterpretations(
            List<(IMethodSymbol Candidate, int ParamIndex, int SyntaxIndex)> sites)
        {
            // Nothing to dedup with 0 or 1 sites (the common case); skip the HashSet and key strings.
            if (sites.Count <= 1) return sites;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<(IMethodSymbol Candidate, int ParamIndex, int SyntaxIndex)>();
            foreach (var site in sites)
            {
                if (seen.Add(InterpretationKey(site.Candidate, site.ParamIndex))) result.Add(site);
            }
            return result;
        }

        // Identifies the forwarding method an interpretation would emit: its declaring type, which
        // argument is ducked, and the method's canonical signature key. Reusing MethodSig.DedupKey
        // (name + arity + full parameter shape incl. ref kinds + constraints, generics-normalized)
        // keeps this notion of "same method" from drifting from the rest of the generator and folds
        // in cases a hand-rolled key missed - e.g. two overloads differing only by a ref kind, or two
        // same-signature methods on different declaring types. Equal keys are interchangeable;
        // differing keys are a real ambiguity the user must resolve.
        private static string InterpretationKey(IMethodSymbol candidate, int argIndex)
            => $"{SymbolNames.Fq(candidate.ContainingType)}|{argIndex}|{MemberSignatures.ToMethodSig(candidate).DedupKey}";

        private static List<(IMethodSymbol Candidate, int ParamIndex, int SyntaxIndex)> CollectDuckableArgumentSites(
            InvocationExpressionSyntax invocation, SemanticModel semanticModel, SymbolInfo symbolInfo)
        {
            var sites = new List<(IMethodSymbol, int, int)>();
            foreach (var candidate in symbolInfo.CandidateSymbols.OfType<IMethodSymbol>())
            {
                if (candidate.ContainingType == null) continue;
                var arguments = invocation.ArgumentList.Arguments;
                if (!TryMapArgumentsToParameters(arguments, candidate, out var parameterIndices)) continue;

                AddDuckableArgIndices(semanticModel, candidate, arguments, parameterIndices, sites);
            }
            return sites;
        }

        private static void AddDuckableArgIndices(
            SemanticModel semanticModel, IMethodSymbol candidate,
            SeparatedSyntaxList<ArgumentSyntax> arguments, IReadOnlyList<int> parameterIndices,
            List<(IMethodSymbol, int, int)> sites)
        {
            for (int syntaxIndex = 0; syntaxIndex < arguments.Count; syntaxIndex++)
            {
                var paramIndex = parameterIndices[syntaxIndex];
                if (IsDuckableArgument(semanticModel, candidate, arguments, syntaxIndex, paramIndex))
                    sites.Add((candidate, paramIndex, syntaxIndex));
            }
        }

        private static bool IsDuckableArgument(
            SemanticModel semanticModel, IMethodSymbol candidate, SeparatedSyntaxList<ArgumentSyntax> arguments,
            int syntaxIndex, int paramIndex)
        {
            var arg = arguments[syntaxIndex];
            var argType = semanticModel.GetTypeInfo(arg.Expression).Type;
            var paramType = candidate.Parameters[paramIndex].Type;

            if (paramIndex < 0 || argType == null || paramType == null || paramType.TypeKind != TypeKind.Interface) return false;

            var conversion = semanticModel.ClassifyConversion(arg.Expression, paramType);
            if (conversion.Exists && conversion.IsImplicit) return false;

            return IsProxyableKind(GetUnderlyingType(argType));
        }

        private static bool TryMapArgumentsToParameters(
            SeparatedSyntaxList<ArgumentSyntax> arguments, IMethodSymbol candidate, out List<int> parameterIndices)
        {
            parameterIndices = new List<int>(arguments.Count);
            var used = new HashSet<int>();
            int firstCallableParameter = HasExplicitExtensionReceiverParameter(candidate) ? 1 : 0;
            int nextPositional = firstCallableParameter;

            foreach (var arg in arguments)
            {
                if (!TryMapArgument(arg, candidate, used, ref nextPositional, out var paramIndex)) return false;

                parameterIndices.Add(paramIndex);
                if (!candidate.Parameters[paramIndex].IsParams) used.Add(paramIndex);
            }

            return AllRequiredParametersUsed(candidate, used, firstCallableParameter);
        }

        private static bool TryMapArgument(
            ArgumentSyntax arg, IMethodSymbol candidate, HashSet<int> used, ref int nextPositional, out int paramIndex)
        {
            return arg.NameColon != null
                ? TryMapNamedArgument(arg, candidate, used, out paramIndex)
                : TryMapPositionalArgument(candidate, used, ref nextPositional, out paramIndex);
        }

        private static bool TryMapNamedArgument(
            ArgumentSyntax arg, IMethodSymbol candidate, HashSet<int> used, out int paramIndex)
        {
            var name = arg.NameColon!.Name.Identifier.ValueText;
            paramIndex = FindParameter(candidate, name);
            return paramIndex >= 0 && !used.Contains(paramIndex);
        }

        private static bool TryMapPositionalArgument(
            IMethodSymbol candidate, HashSet<int> used, ref int nextPositional, out int paramIndex)
        {
            while (nextPositional < candidate.Parameters.Length && used.Contains(nextPositional))
                nextPositional++;

            if (nextPositional < candidate.Parameters.Length)
            {
                paramIndex = nextPositional;
                if (!candidate.Parameters[paramIndex].IsParams) nextPositional++;
                return true;
            }

            paramIndex = candidate.Parameters.Length - 1;
            return paramIndex >= 0 && candidate.Parameters[paramIndex].IsParams;
        }

        private static bool AllRequiredParametersUsed(IMethodSymbol candidate, HashSet<int> used, int firstCallableParameter)
        {
            for (int i = firstCallableParameter; i < candidate.Parameters.Length; i++)
            {
                if (!used.Contains(i) && !candidate.Parameters[i].IsOptional && !candidate.Parameters[i].IsParams)
                    return false;
            }
            return true;
        }

        private static int FindParameter(IMethodSymbol candidate, string name)
        {
            for (int i = 0; i < candidate.Parameters.Length; i++)
            {
                if (candidate.Parameters[i].Name == name) return i;
            }
            return -1;
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
            var requirements = InterfaceRequirementsAnalyzer.Analyze(interfaceType);

            var surface = SurfaceAnalyzer.BuildSurfaceCompatKeys(underlyingType);
            var surfaceSet = new HashSet<string>(surface, StringComparer.Ordinal);

            bool isSelfMatch = StructuralMatch.IsSatisfiedBy(
                requirements.Methods, requirements.Properties, requirements.Indexers, requirements.Events, surfaceSet);

            var originalParams = originalMethod == null
                ? (IReadOnlyList<ParamSig>)Array.Empty<ParamSig>()
                : ForwardedParameters(originalMethod).Select(MemberSignatures.ToParamSig).ToList();

            var loc = invocation.GetLocation();
            var originalDefinition = originalMethod == null ? null : OriginalExtensionDefinition(originalMethod);

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
                originalContainingTypeFq: originalDefinition?.ContainingType == null ? "" : SymbolNames.Fq(originalDefinition.ContainingType),
                originalIsExtensionMethod: originalMethod != null && IsExtensionLike(originalMethod),
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
                diagFilePath: loc.SourceTree?.FilePath,
                diagSpan: loc.SourceSpan,
                diagLineSpan: loc.GetLineSpan().Span);
        }

        private static IEnumerable<IParameterSymbol> ForwardedParameters(IMethodSymbol method)
            => HasExplicitExtensionReceiverParameter(method) ? method.Parameters.Skip(1) : method.Parameters;
    }
}

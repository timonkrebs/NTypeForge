using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
        // A ducked argument still in symbol form, resolved per argument before model assembly.
        private readonly struct DuckedArgSite
        {
            public readonly ITypeSymbol ArgType;
            public readonly ITypeSymbol UnderlyingType;
            public readonly ITypeSymbol InterfaceType;
            public readonly int EmittedIndex;

            public DuckedArgSite(ITypeSymbol argType, ITypeSymbol underlyingType, ITypeSymbol interfaceType, int emittedIndex)
            {
                ArgType = argType;
                UnderlyingType = underlyingType;
                InterfaceType = interfaceType;
                EmittedIndex = emittedIndex;
            }
        }

        public static CandidateModel? GetCandidate(GeneratorSyntaxContext context, CancellationToken cancellationToken)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            var semanticModel = context.SemanticModel;
            var symbolInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);

            var duckCall = TryGetDuckCall(invocation, semanticModel, symbolInfo, cancellationToken);
            if (duckCall != null) return duckCall;

            // A call that bound successfully needs no duck typing; only a failed overload
            // resolution (Symbol == null, with candidates) can be rescued by generated proxies.
            if (symbolInfo.Symbol != null || symbolInfo.CandidateSymbols.Length == 0) return null;

            return TryGetMethodArgumentDuck(invocation, semanticModel, symbolInfo, cancellationToken);
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
                default:
                    // Type parameters, dynamic, etc.: nothing the generated code couldn't name.
                    return true;
            }
        }

        private static bool IsTypeAccessibilityUsable(Accessibility accessibility)
            => accessibility == Accessibility.Public ||
               accessibility == Accessibility.Internal ||
               accessibility == Accessibility.ProtectedOrInternal;

        private static bool IsEffectivelyPublic(ITypeSymbol type)
        {
            for (var current = type; current != null; current = current.ContainingType)
            {
                if (current.DeclaredAccessibility != Accessibility.Public) return false;
            }
            return true;
        }

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
            InvocationExpressionSyntax invocation, SemanticModel semanticModel, SymbolInfo symbolInfo,
            CancellationToken cancellationToken)
        {
            if (!(symbolInfo.Symbol is IMethodSymbol resolved) || resolved.Name != "Duck" ||
                !IsTopLevelNTypeForgeNamespace(resolved.ContainingNamespace) || resolved.TypeArguments.Length != 1)
                return null;

            var instanceExpr = GetDuckInstanceExpression(invocation, semanticModel, cancellationToken);
            if (instanceExpr == null) return null;

            var argType = semanticModel.GetTypeInfo(instanceExpr, cancellationToken).Type;
            if (argType == null) return null;

            var targetInterface = resolved.TypeArguments[0];
            var underlyingType = GetUnderlyingType(argType);
            if (targetInterface.TypeKind != TypeKind.Interface || !IsProxyableKind(underlyingType)) return null;
            if (ContainsTypeParameter(argType) || ContainsTypeParameter(underlyingType) || ContainsTypeParameter(targetInterface))
                return null;
            if (!IsUsableFromGeneratedTopLevelCode(argType) ||
                !IsUsableFromGeneratedTopLevelCode(underlyingType) ||
                !IsUsableFromGeneratedTopLevelCode(targetInterface))
                return null;

            // The instance already satisfies the interface (nominally or via variance), so no proxy
            // is needed: the runtime Duck<T> fallback's `instance is T` returns it directly.
            // Generating a proxy here would only add a needless wrap/box.
            if (AlreadyImplements(semanticModel, argType, targetInterface)) return null;

            return BuildModel(
                invocation, target: argType,
                duckedArgs: new[] { new DuckedArgSite(argType, underlyingType, targetInterface, emittedIndex: 0) },
                isStatic: false, isDuckCall: true, originalMethod: null);
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
            InvocationExpressionSyntax invocation, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (!(invocation.Expression is MemberAccessExpressionSyntax memberAccess)) return null;

            var receiver = semanticModel.GetSymbolInfo(memberAccess.Expression, cancellationToken).Symbol;
            if (receiver is ITypeSymbol or INamespaceSymbol) return null;

            return memberAccess.Expression;
        }

        // A failed call whose arguments could implicitly become interface parameters via proxies.
        // Within one overload, every duckable argument is ducked together (each one that fails to
        // convert must be replaced for the forwarded call to bind), so the overload's whole set of
        // duckable arguments is a single interpretation. We rewire only when the call has exactly
        // one duckable interpretation. With more than one, silently choosing a single overload
        // would be non-deterministic and could bind the call to the wrong one, so we leave the
        // original (still-failing) call for the compiler to report - which is also what suppresses
        // the NTF003 near-miss on ambiguous sites.
        private static CandidateModel? TryGetMethodArgumentDuck(
            InvocationExpressionSyntax invocation, SemanticModel semanticModel, SymbolInfo symbolInfo,
            CancellationToken cancellationToken)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax) return null;

            // Argument-side facts (the expression's type and its proxyable underlying) do not
            // depend on which candidate overload is being tested, so they are resolved at most
            // once per argument here and shared across the candidate loop and site resolution.
            var argFacts = new ArgumentDuckFact?[invocation.ArgumentList.Arguments.Count];
            var interpretations = DistinctInterpretations(
                CollectDuckableInterpretations(invocation, semanticModel, symbolInfo, argFacts, cancellationToken));
            if (interpretations.Count != 1) return null;

            var (candidate, args) = interpretations[0];
            var target = GetForwardingTarget(invocation, semanticModel, candidate, cancellationToken);
            if (target == null ||
                ContainsTypeParameter(target) ||
                !IsUsableFromGeneratedTopLevelCode(candidate.ContainingType!) ||
                !IsUsableFromGeneratedTopLevelCode(target))
                return null;

            var duckedArgs = ResolveDuckedArgSites(candidate, args, argFacts);
            if (duckedArgs == null) return null;

            return BuildModel(
                invocation, target: target, duckedArgs,
                isStatic: candidate.IsStatic && !IsExtensionLike(candidate),
                isDuckCall: false,
                originalMethod: candidate);
        }

        // Resolves each duckable (parameter, argument) pair of the chosen overload to its symbol
        // triple, ordered by emitted parameter index so the model is independent of named-argument
        // order. The argument-side types come from the per-invocation fact cache populated by
        // IsDuckableArgument. Null when any argument involves a type the generated code could not
        // utter (an open type parameter or an inaccessible type) - the site is then left alone as
        // a whole, since a forwarding method missing one ducked parameter could never bind.
        private static List<DuckedArgSite>? ResolveDuckedArgSites(
            IMethodSymbol candidate, IReadOnlyList<(int ParamIndex, int SyntaxIndex)> args, ArgumentDuckFact?[] argFacts)
        {
            var resolved = new List<DuckedArgSite>(args.Count);
            foreach (var (paramIndex, syntaxIndex) in args)
            {
                // A duckable site was recorded at this index, so its fact is populated and non-null.
                var argFact = argFacts[syntaxIndex]!.Value;
                var argType = argFact.Type!;
                var underlyingType = argFact.Underlying!;
                var interfaceType = candidate.Parameters[paramIndex].Type;
                if (ContainsTypeParameter(argType) ||
                    ContainsTypeParameter(underlyingType) ||
                    ContainsTypeParameter(interfaceType) ||
                    !IsUsableFromGeneratedTopLevelCode(argType) ||
                    !IsUsableFromGeneratedTopLevelCode(underlyingType) ||
                    !IsUsableFromGeneratedTopLevelCode(interfaceType))
                    return null;

                resolved.Add(new DuckedArgSite(argType, underlyingType, interfaceType, EmittedParameterIndex(candidate, paramIndex)));
            }

            resolved.Sort((a, b) => a.EmittedIndex.CompareTo(b.EmittedIndex));
            return resolved;
        }

        private static bool IsExtensionLike(IMethodSymbol method)
            => method.IsExtensionMethod || method.ReducedFrom != null;

        private static bool HasExplicitExtensionReceiverParameter(IMethodSymbol method)
            => method.IsExtensionMethod && method.ReducedFrom == null;

        private static IMethodSymbol OriginalExtensionDefinition(IMethodSymbol method)
            => method.ReducedFrom ?? method;

        private static ITypeSymbol? GetForwardingTarget(
            InvocationExpressionSyntax invocation, SemanticModel semanticModel, IMethodSymbol candidate,
            CancellationToken cancellationToken)
        {
            if (candidate.ReducedFrom != null && invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                return semanticModel.GetTypeInfo(memberAccess.Expression, cancellationToken).Type;

            return HasExplicitExtensionReceiverParameter(candidate) && candidate.Parameters.Length > 0
                ? candidate.Parameters[0].Type
                : candidate.ContainingType;
        }

        private static int EmittedParameterIndex(IMethodSymbol candidate, int paramIndex)
            => HasExplicitExtensionReceiverParameter(candidate) ? paramIndex - 1 : paramIndex;

        // Collapses interpretations that would generate the same forwarding method - an override
        // and the base it hides, or a symbol Roslyn lists more than once - so they don't count as
        // a false ambiguity. Genuinely distinct interpretations (different target interfaces or
        // different remaining parameters) survive as separate entries.
        private static List<(IMethodSymbol Candidate, List<(int ParamIndex, int SyntaxIndex)> Args)> DistinctInterpretations(
            List<(IMethodSymbol Candidate, List<(int ParamIndex, int SyntaxIndex)> Args)> interpretations)
        {
            // Nothing to dedup with 0 or 1 interpretations (the common case); skip the HashSet and
            // key strings.
            if (interpretations.Count <= 1) return interpretations;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<(IMethodSymbol Candidate, List<(int ParamIndex, int SyntaxIndex)> Args)>();
            foreach (var interpretation in interpretations)
            {
                if (seen.Add(InterpretationKey(interpretation.Candidate, interpretation.Args))) result.Add(interpretation);
            }
            return result;
        }

        // Identifies the forwarding method an interpretation would emit: its declaring type, which
        // arguments are ducked, and the method's canonical signature key. Reusing MethodSig.DedupKey
        // (name + arity + full parameter shape incl. ref kinds + constraints, generics-normalized)
        // keeps this notion of "same method" from drifting from the rest of the generator and folds
        // in cases a hand-rolled key missed - e.g. two overloads differing only by a ref kind, or two
        // same-signature methods on different declaring types. Equal keys are interchangeable;
        // differing keys are a real ambiguity the user must resolve.
        private static string InterpretationKey(IMethodSymbol candidate, List<(int ParamIndex, int SyntaxIndex)> args)
            => $"{SymbolNames.Fq(candidate.ContainingType)}|{string.Join(",", args.Select(a => a.ParamIndex))}|{MemberSignatures.ToMethodSig(candidate).DedupKey}";

        private static List<(IMethodSymbol Candidate, List<(int ParamIndex, int SyntaxIndex)> Args)> CollectDuckableInterpretations(
            InvocationExpressionSyntax invocation, SemanticModel semanticModel, SymbolInfo symbolInfo,
            ArgumentDuckFact?[] argFacts, CancellationToken cancellationToken)
        {
            var interpretations = new List<(IMethodSymbol, List<(int, int)>)>();
            foreach (var candidate in symbolInfo.CandidateSymbols.OfType<IMethodSymbol>())
            {
                if (candidate.ContainingType == null) continue;
                var arguments = invocation.ArgumentList.Arguments;
                if (!TryMapArgumentsToParameters(arguments, candidate, out var parameterIndices)) continue;

                var args = CollectDuckableArgs(semanticModel, candidate, arguments, parameterIndices, argFacts, cancellationToken);
                if (args.Count > 0) interpretations.Add((candidate, args));
            }
            return interpretations;
        }

        private static List<(int ParamIndex, int SyntaxIndex)> CollectDuckableArgs(
            SemanticModel semanticModel, IMethodSymbol candidate,
            SeparatedSyntaxList<ArgumentSyntax> arguments, IReadOnlyList<int> parameterIndices,
            ArgumentDuckFact?[] argFacts, CancellationToken cancellationToken)
        {
            var args = new List<(int, int)>();
            for (int syntaxIndex = 0; syntaxIndex < arguments.Count; syntaxIndex++)
            {
                var paramIndex = parameterIndices[syntaxIndex];
                if (IsDuckableArgument(semanticModel, candidate, arguments, argFacts, syntaxIndex, paramIndex, cancellationToken))
                    args.Add((paramIndex, syntaxIndex));
            }
            return args;
        }

        // Ordered cheapest-first: the parameter-side test is a plain symbol-property read, the
        // argument-side facts are one (cached, candidate-independent) GetTypeInfo plus
        // GetUnderlyingType, and ClassifyConversion - the most expensive test - runs last, only
        // for an interface parameter receiving a proxyable argument.
        private static bool IsDuckableArgument(
            SemanticModel semanticModel, IMethodSymbol candidate, SeparatedSyntaxList<ArgumentSyntax> arguments,
            ArgumentDuckFact?[] argFacts, int syntaxIndex, int paramIndex, CancellationToken cancellationToken)
        {
            if (paramIndex < 0) return false;

            var parameter = candidate.Parameters[paramIndex];
            // Ducking rewrites the failed call by replacing the argument expression with a freshly
            // constructed proxy. That is only valid for by-value parameters: ref/out/in parameters
            // require a variable passed with the corresponding modifier, and a generated proxy
            // temporary cannot be used to preserve those semantics.
            if (parameter.RefKind != RefKind.None) return false;

            var paramType = parameter.Type;
            if (paramType == null || paramType.TypeKind != TypeKind.Interface) return false;

            var argFact = argFacts[syntaxIndex] ??=
                ComputeArgumentDuckFact(semanticModel, arguments[syntaxIndex].Expression, cancellationToken);
            if (argFact.Type == null) return false;

            var conversion = semanticModel.ClassifyConversion(arguments[syntaxIndex].Expression, paramType);
            return !(conversion.Exists && conversion.IsImplicit);
        }

        // The candidate-independent half of IsDuckableArgument for one argument: the argument
        // expression's type and its underlying type (cached because GetUnderlyingType walks
        // AllInterfaces), both null when the argument can never be ducked - the expression has no
        // type, or its underlying kind is not proxyable. Wrapped in a struct so the per-invocation
        // cache can tell "not yet computed" (null entry) from "computed: not duckable".
        private readonly struct ArgumentDuckFact
        {
            public ArgumentDuckFact(ITypeSymbol? type, ITypeSymbol? underlying)
            {
                Type = type;
                Underlying = underlying;
            }

            public ITypeSymbol? Type { get; }
            public ITypeSymbol? Underlying { get; }
        }

        private static ArgumentDuckFact ComputeArgumentDuckFact(
            SemanticModel semanticModel, ExpressionSyntax expression, CancellationToken cancellationToken)
        {
            var type = semanticModel.GetTypeInfo(expression, cancellationToken).Type;
            if (type == null) return new ArgumentDuckFact(null, null);

            var underlying = GetUnderlyingType(type);
            return IsProxyableKind(underlying)
                ? new ArgumentDuckFact(type, underlying)
                : new ArgumentDuckFact(null, null);
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

        private static bool ContainsTypeParameter(ITypeSymbol type)
        {
            switch (type)
            {
                case ITypeParameterSymbol:
                    return true;
                case IArrayTypeSymbol array:
                    return ContainsTypeParameter(array.ElementType);
                case INamedTypeSymbol named:
                    return named.TypeArguments.Any(ContainsTypeParameter);
                default:
                    return false;
            }
        }

        private static CandidateModel BuildModel(
            InvocationExpressionSyntax invocation,
            ITypeSymbol target,
            IReadOnlyList<DuckedArgSite> duckedArgs,
            bool isStatic,
            bool isDuckCall,
            IMethodSymbol? originalMethod)
        {
            var argModels = duckedArgs.Select(BuildDuckedArg).ToList();

            var originalDefinition = originalMethod == null ? null : OriginalExtensionDefinition(originalMethod);
            var unconstructedMethod = originalDefinition?.OriginalDefinition;

            var originalParams = unconstructedMethod == null
                ? (IReadOnlyList<ParamSig>)Array.Empty<ParamSig>()
                : ForwardedParameters(unconstructedMethod).Select(MemberSignatures.ToParamSig).ToList();
            var originalSig = unconstructedMethod == null ? null : MemberSignatures.ToMethodSig(unconstructedMethod);

            var loc = invocation.GetLocation();

            return new CandidateModel(
                targetFq: SymbolNames.Fq(target),
                targetNamespace: SymbolNames.NamespaceOf(target),
                targetMinimalName: SymbolNames.MinimalName(target),
                targetIsInterface: target.TypeKind == TypeKind.Interface,
                targetIsPublic: IsEffectivelyPublic(target),
                duckedArgs: argModels,
                isStatic: isStatic,
                isDuckCall: isDuckCall,
                originalMethodName: originalMethod?.Name ?? "",
                originalContainingTypeFq: originalDefinition?.ContainingType == null ? "" : SymbolNames.Fq(originalDefinition.ContainingType),
                originalIsExtensionMethod: originalMethod != null && IsExtensionLike(originalMethod),
                originalReturnTypeFq: unconstructedMethod == null ? "" : SymbolNames.Fq(unconstructedMethod.ReturnType),
                originalReturnsVoid: unconstructedMethod != null && unconstructedMethod.ReturnType.SpecialType == SpecialType.System_Void,
                originalParameters: originalParams,
                originalArity: originalSig?.Arity ?? 0,
                originalTypeParameters: originalSig?.TypeParameters ?? Array.Empty<string>(),
                originalConstraints: originalSig?.Constraints ?? Array.Empty<string>(),
                diagFilePath: loc.SourceTree?.FilePath,
                diagSpan: loc.SourceSpan,
                diagLineSpan: loc.GetLineSpan().Span);
        }

        private static DuckedArgModel BuildDuckedArg(DuckedArgSite site)
        {
            var requirements = InterfaceRequirementsAnalyzer.Analyze(site.InterfaceType);

            var surface = SurfaceAnalyzer.BuildSurfaceCompatKeys(site.UnderlyingType);
            var surfaceSet = new HashSet<string>(surface, StringComparer.Ordinal);

            bool isSelfMatch = StructuralMatch.IsSatisfiedBy(
                requirements.Methods, requirements.Properties, requirements.Indexers, requirements.Events, surfaceSet);

            return new DuckedArgModel(
                argumentIndex: site.EmittedIndex,
                argumentIsInterface: site.ArgType.TypeKind == TypeKind.Interface,
                argumentFq: SymbolNames.Fq(site.ArgType),
                underlyingFq: SymbolNames.Fq(site.UnderlyingType),
                underlyingNamespace: SymbolNames.NamespaceOf(site.UnderlyingType),
                underlyingMinimalName: SymbolNames.MinimalName(site.UnderlyingType),
                underlyingIsInterface: site.UnderlyingType.TypeKind == TypeKind.Interface,
                underlyingBaseDepth: SymbolNames.BaseTypeDepth(site.UnderlyingType),
                interfaceFq: SymbolNames.Fq(site.InterfaceType),
                interfaceMinimalName: SymbolNames.MinimalName(site.InterfaceType),
                methodRequirements: requirements.Methods,
                propertyRequirements: requirements.Properties,
                indexerRequirements: requirements.Indexers,
                eventRequirements: requirements.Events,
                underlyingSurfaceCompatKeys: surface,
                isSelfMatch: isSelfMatch,
                unsupportedMemberName: requirements.Unsupported);
        }

        private static IEnumerable<IParameterSymbol> ForwardedParameters(IMethodSymbol method)
            => HasExplicitExtensionReceiverParameter(method) ? method.Parameters.Skip(1) : method.Parameters;
    }
}

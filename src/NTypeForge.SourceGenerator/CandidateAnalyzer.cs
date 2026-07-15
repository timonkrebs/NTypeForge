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
            // Set only for a ref/out/in near-miss (NTF004): the structural match is blocked because
            // the parameter is by-reference. Null on a normal (duckable) argument.
            public readonly string? RefKindBlocker;
            public readonly string? BlockedParameterName;

            public DuckedArgSite(ITypeSymbol argType, ITypeSymbol underlyingType, ITypeSymbol interfaceType, int emittedIndex,
                string? refKindBlocker = null, string? blockedParameterName = null)
            {
                ArgType = argType;
                UnderlyingType = underlyingType;
                InterfaceType = interfaceType;
                EmittedIndex = emittedIndex;
                RefKindBlocker = refKindBlocker;
                BlockedParameterName = blockedParameterName;
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

            return TryGetMethodArgumentDuck(invocation, semanticModel, symbolInfo, cancellationToken)
                ?? TryGetRefKindNearMiss(invocation, semanticModel, symbolInfo, cancellationToken);
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

        // NTF004 - a ref/out/in near-miss. Runs only when the normal duck path found nothing: the
        // argument may still be a *structural* match that simply can't be ducked because its
        // parameter is by-reference (a generated proxy can't be passed by ref/out/in). We surface
        // that, and only that, as a warning - mirroring NTF003's high-confidence bar: the by-ref
        // kind must be the *sole* blocker (every other argument already binds, the argument was
        // passed with the matching ref/out/in keyword, and it structurally - but not already
        // implicitly - satisfies the interface), and, after collapsing equivalent overloads, exactly
        // one interpretation may qualify. Otherwise the call is genuinely ambiguous, or is a plain
        // keyword/type error the compiler already explains, so we stay silent.
        private static CandidateModel? TryGetRefKindNearMiss(
            InvocationExpressionSyntax invocation, SemanticModel semanticModel, SymbolInfo symbolInfo,
            CancellationToken cancellationToken)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax) return null;

            var argFacts = new ArgumentDuckFact?[invocation.ArgumentList.Arguments.Count];
            var interpretations = DistinctInterpretations(
                CollectRefKindNearMissInterpretations(invocation, semanticModel, symbolInfo, argFacts, cancellationToken));
            if (interpretations.Count != 1) return null;

            var (candidate, args) = interpretations[0];
            var target = GetForwardingTarget(invocation, semanticModel, candidate, cancellationToken);
            if (target == null ||
                ContainsTypeParameter(target) ||
                !IsUsableFromGeneratedTopLevelCode(candidate.ContainingType!) ||
                !IsUsableFromGeneratedTopLevelCode(target))
                return null;

            // Sole-blocker gate (authoritative): only explain a call whose *only* compiler errors are
            // the ducked argument's conversions. Any other error - a missing/extra ref keyword, a
            // readonly or unassigned location, an inapplicable receiver form, invalid argument
            // ordering, failed generic inference, inaccessibility, ... - means the by-reference type
            // mismatch is not the actionable problem, so the compiler's own error stands and we stay
            // silent. The structural guards above are a cheap first pass; this is the guarantee.
            if (!RefKindIsSoleBlocker(semanticModel, invocation, cancellationToken)) return null;

            return BuildModel(
                invocation, target, ResolveRefKindNearMissSites(candidate, args, argFacts),
                isStatic: candidate.IsStatic && !IsExtensionLike(candidate),
                isDuckCall: false,
                originalMethod: candidate);
        }

        // Whether the only errors on the invocation are argument-conversion errors (CS1503/CS1502) -
        // the ones a proxy would resolve. Any other error code (keyword, lvalue/readonly, definite
        // assignment, receiver form, argument ordering, generic inference, accessibility, ...) is an
        // independent blocker, so the by-reference type mismatch is not the sole reason the call
        // fails and NTF004 would be misleading. Runs once, only after a single near-miss
        // interpretation has already been isolated, so the GetDiagnostics cost is bounded.
        private static bool RefKindIsSoleBlocker(
            SemanticModel semanticModel, InvocationExpressionSyntax invocation, CancellationToken cancellationToken)
        {
            foreach (var diagnostic in semanticModel.GetDiagnostics(invocation.Span, cancellationToken))
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error &&
                    diagnostic.Id != "CS1503" && diagnostic.Id != "CS1502")
                    return false;
            }
            return true;
        }

        // Whether the candidate's static/instance form is callable in this invocation's receiver
        // form. An instance method named through its type (Mgr.H(...)), or a static method through an
        // instance, is not - so the by-reference type mismatch is not the sole reason the call fails.
        private static bool ReceiverFormApplies(
            InvocationExpressionSyntax invocation, SemanticModel semanticModel, IMethodSymbol candidate,
            CancellationToken cancellationToken)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) return true;
            var receiver = semanticModel.GetSymbolInfo(memberAccess.Expression, cancellationToken).Symbol;
            if (receiver is ITypeSymbol or INamespaceSymbol)
                return candidate.IsStatic;
            return !candidate.IsStatic || IsExtensionLike(candidate);
        }

        // Mirrors CollectDuckableInterpretations, but keeps an overload only when every argument
        // either already binds or is a ref/out/in interface near-miss - so the by-ref kind is the
        // sole reason the call fails. Sharing the (ParamIndex, SyntaxIndex) shape lets these run
        // through DistinctInterpretations, so an override and the base it hides (or a repeated
        // candidate) collapse to one near-miss instead of being mistaken for ambiguity.
        private static List<(IMethodSymbol Candidate, List<(int ParamIndex, int SyntaxIndex)> Args)> CollectRefKindNearMissInterpretations(
            InvocationExpressionSyntax invocation, SemanticModel semanticModel, SymbolInfo symbolInfo,
            ArgumentDuckFact?[] argFacts, CancellationToken cancellationToken)
        {
            var interpretations = new List<(IMethodSymbol, List<(int, int)>)>();
            foreach (var candidate in symbolInfo.CandidateSymbols.OfType<IMethodSymbol>())
            {
                if (candidate.ContainingType == null) continue;
                // An inaccessible candidate fails on accessibility, not the ref duck, so it is not a
                // sole-blocker near-miss - skip it (also keeps generated code from naming a member it
                // could not call).
                if (!semanticModel.IsAccessible(invocation.SpanStart, candidate)) continue;
                // A generic candidate whose type arguments were not inferred to concrete types fails
                // on type inference too (not solely the ref duck), so it is not a sole-blocker
                // near-miss.
                if (candidate.IsGenericMethod && candidate.TypeArguments.Any(t => t.TypeKind == TypeKind.TypeParameter)) continue;
                // An instance method named through its type (Mgr.H(...)) or a static through an
                // instance is not callable in this receiver form - a separate error the compiler
                // folds into the argument mismatch, so it needs an explicit check here.
                if (!ReceiverFormApplies(invocation, semanticModel, candidate, cancellationToken)) continue;
                var arguments = invocation.ArgumentList.Arguments;
                if (!TryMapArgumentsToParameters(arguments, candidate, out var parameterIndices)) continue;

                var blocked = CollectRefKindBlockedArgs(semanticModel, candidate, arguments, parameterIndices, argFacts, cancellationToken);
                if (blocked != null && blocked.Count > 0) interpretations.Add((candidate, blocked));
            }
            return interpretations;
        }

        // The ref/out/in interface arguments of one overload that are clean structural-match
        // near-misses, or null when the overload is not one (some other argument doesn't bind, or a
        // by-ref interface argument isn't a clean near-miss - e.g. a missing keyword or an
        // already-convertible value, both plain compiler errors rather than ducking blockers).
        private static List<(int ParamIndex, int SyntaxIndex)>? CollectRefKindBlockedArgs(
            SemanticModel semanticModel, IMethodSymbol candidate, SeparatedSyntaxList<ArgumentSyntax> arguments,
            IReadOnlyList<int> parameterIndices, ArgumentDuckFact?[] argFacts, CancellationToken cancellationToken)
        {
            var blocked = new List<(int, int)>();
            for (int syntaxIndex = 0; syntaxIndex < arguments.Count; syntaxIndex++)
            {
                var paramIndex = parameterIndices[syntaxIndex];
                if (paramIndex < 0) return null;
                var parameter = candidate.Parameters[paramIndex];
                var argument = arguments[syntaxIndex];

                if (IsRefKindBlockedNearMiss(semanticModel, parameter, argument, argFacts, syntaxIndex, cancellationToken))
                    blocked.Add((paramIndex, syntaxIndex));
                else if (!ArgumentAlreadyBinds(semanticModel, argument, parameter, cancellationToken))
                    return null;
            }
            return blocked;
        }

        // True when this argument fills a ref/out/in interface parameter, was passed with the
        // matching by-reference keyword, and structurally - but not already implicitly - satisfies
        // the interface. That is exactly the case that would duck were the parameter by-value, so the
        // by-ref kind is the only blocker. A missing/mismatched keyword or an already-convertible
        // value is excluded: no proxy is involved and the compiler's own error is the actionable one.
        private static bool IsRefKindBlockedNearMiss(
            SemanticModel semanticModel, IParameterSymbol parameter, ArgumentSyntax argument,
            ArgumentDuckFact?[] argFacts, int syntaxIndex, CancellationToken cancellationToken)
        {
            if (parameter.RefKind == RefKind.None || parameter.Type.TypeKind != TypeKind.Interface) return false;
            if (!ArgumentRefKindMatches(argument, parameter.RefKind)) return false;
            if (!IsRefKindAssignable(semanticModel, argument.Expression, parameter.RefKind, cancellationToken)) return false;

            var argFact = argFacts[syntaxIndex] ??= ComputeArgumentDuckFact(semanticModel, argument.Expression, cancellationToken);
            if (argFact.Type == null || argFact.Underlying == null) return false;
            if (ContainsTypeParameter(argFact.Type) || ContainsTypeParameter(argFact.Underlying) || ContainsTypeParameter(parameter.Type))
                return false;
            if (!IsUsableFromGeneratedTopLevelCode(argFact.Underlying) || !IsUsableFromGeneratedTopLevelCode(parameter.Type))
                return false;
            if (BindsImplicitly(semanticModel, argument.Expression, parameter.Type)) return false;

            return StructurallyMatches(parameter.Type, argFact.Underlying);
        }

        // A non-near-miss argument must already bind for the by-ref kind to be the sole blocker.
        // By-value parameters bind on an implicit conversion (with a positional expanded-params
        // element checked against the array element type); a by-reference passthrough has stricter
        // requirements handled by ByReferenceArgumentBinds.
        private static bool ArgumentAlreadyBinds(
            SemanticModel semanticModel, ArgumentSyntax argument, IParameterSymbol parameter, CancellationToken cancellationToken)
        {
            var expression = argument.Expression;
            if (parameter.RefKind != RefKind.None)
                return ByReferenceArgumentBinds(semanticModel, argument, parameter, cancellationToken);

            // A by-value parameter passed with a ref/out/in keyword is a compiler error, so it is not
            // already bound - the by-ref duck is then not the sole blocker.
            if (argument.RefKindKeyword.ValueText.Length != 0) return false;

            if (BindsImplicitly(semanticModel, expression, parameter.Type)) return true;

            // Only a *positional* argument is expanded element-by-element; a named params argument
            // (values: x) must supply the whole array, so it is not an element-type match.
            return argument.NameColon == null && parameter.IsParams &&
                   parameter.Type is IArrayTypeSymbol array &&
                   BindsImplicitly(semanticModel, expression, array.ElementType);
        }

        // A by-reference passthrough binds only when the caller used the matching keyword, the
        // argument is an assignable variable, and its type matches exactly (by-reference parameters
        // are invariant). Otherwise the argument's own keyword/lvalue/type error is an additional
        // blocker, so the ref duck is not the sole reason the call fails.
        private static bool ByReferenceArgumentBinds(
            SemanticModel semanticModel, ArgumentSyntax argument, IParameterSymbol parameter, CancellationToken cancellationToken)
        {
            if (!ArgumentRefKindMatches(argument, parameter.RefKind)) return false;
            if (!IsRefKindAssignable(semanticModel, argument.Expression, parameter.RefKind, cancellationToken)) return false;
            var argType = semanticModel.GetTypeInfo(argument.Expression, cancellationToken).Type;
            return argType != null && SymbolEqualityComparer.Default.Equals(argType, parameter.Type);
        }

        // Whether the expression can be passed with the given by-reference kind. ref/out require a
        // writable variable; in accepts any readable variable. Rvalues, method/property results, and
        // constants qualify for none, so a structurally-matching type in one of those positions is a
        // plain ref-argument error rather than a ducking near-miss.
        private static bool IsRefKindAssignable(
            SemanticModel semanticModel, ExpressionSyntax expression, RefKind refKind, CancellationToken cancellationToken)
            => IsVariable(semanticModel, expression, requiresWritable: refKind == RefKind.Ref || refKind == RefKind.Out, cancellationToken);

        // A ref/out target must be writable along its whole access path; an in target only needs to
        // be readable. Locals and parameters are constrained by their own ref kind (a ref-readonly
        // local or in-parameter is not writable); fields defer to IsFieldVariable.
        private static bool IsVariable(
            SemanticModel semanticModel, ExpressionSyntax expression, bool requiresWritable, CancellationToken cancellationToken)
        {
            switch (semanticModel.GetSymbolInfo(expression, cancellationToken).Symbol)
            {
                case ILocalSymbol local:
                    return !local.IsConst && (!requiresWritable || local.RefKind == RefKind.None || local.RefKind == RefKind.Ref);
                case IParameterSymbol parameter:
                    return !requiresWritable ||
                           parameter.RefKind == RefKind.None || parameter.RefKind == RefKind.Ref || parameter.RefKind == RefKind.Out;
                case IFieldSymbol field:
                    return IsFieldVariable(semanticModel, field, expression, requiresWritable, cancellationToken);
                default:
                    return false;
            }
        }

        // A field is a usable ref/out target unless it is const or readonly; additionally a *struct*
        // field is writable only through a writable receiver (`readonly Holder h; ref h.Value` is a
        // readonly location), so that receiver is verified recursively. A class field is not
        // constrained by its receiver's writability.
        private static bool IsFieldVariable(
            SemanticModel semanticModel, IFieldSymbol field, ExpressionSyntax expression, bool requiresWritable,
            CancellationToken cancellationToken)
        {
            if (field.IsConst) return false;
            if (!requiresWritable) return true;
            if (field.IsReadOnly) return false;
            if (field.ContainingType?.IsValueType == true && expression is MemberAccessExpressionSyntax member)
                return IsVariable(semanticModel, member.Expression, requiresWritable: true, cancellationToken);
            return true;
        }

        private static bool BindsImplicitly(SemanticModel semanticModel, ExpressionSyntax expression, ITypeSymbol type)
        {
            var conversion = semanticModel.ClassifyConversion(expression, type);
            return conversion.Exists && conversion.IsImplicit;
        }

        // Whether the argument was passed with the by-reference keyword the parameter expects. A
        // missing or mismatched keyword means the by-ref kind is not the sole blocker (the compiler
        // reports the keyword error directly), so it is not an NTF004 near-miss. `in` requires the
        // explicit `in` keyword here to stay conservative, though the language also permits omitting
        // it.
        private static bool ArgumentRefKindMatches(ArgumentSyntax argument, RefKind parameterRefKind)
        {
            var keyword = argument.RefKindKeyword.ValueText;
            switch (parameterRefKind)
            {
                case RefKind.Ref: return keyword == "ref";
                case RefKind.Out: return keyword == "out";
                case RefKind.In: return keyword == "in";
                default: return false;
            }
        }

        // Builds the blocked DuckedArgSites for the single qualifying near-miss interpretation. The
        // collection pass already verified each argument's facts and utterability, so this only
        // attaches the ref-kind blocker metadata that drives the NTF004 message.
        private static List<DuckedArgSite> ResolveRefKindNearMissSites(
            IMethodSymbol candidate, IReadOnlyList<(int ParamIndex, int SyntaxIndex)> args, ArgumentDuckFact?[] argFacts)
        {
            var resolved = new List<DuckedArgSite>(args.Count);
            foreach (var (paramIndex, syntaxIndex) in args)
            {
                var argFact = argFacts[syntaxIndex]!.Value;
                var parameter = candidate.Parameters[paramIndex];
                resolved.Add(new DuckedArgSite(
                    argFact.Type!, argFact.Underlying!, parameter.Type, EmittedParameterIndex(candidate, paramIndex),
                    refKindBlocker: RefKindKeyword(parameter.RefKind), blockedParameterName: parameter.Name));
            }
            resolved.Sort((a, b) => a.EmittedIndex.CompareTo(b.EmittedIndex));
            return resolved;
        }

        // Whether the underlying type structurally satisfies the interface over a fully-proxyable
        // contract. An unsupported member is NTF002/NTF003 territory, not a ref-kind near-miss, so it
        // disqualifies the match here.
        private static bool StructurallyMatches(ITypeSymbol interfaceType, ITypeSymbol underlyingType)
        {
            var requirements = InterfaceRequirementsAnalyzer.Analyze(interfaceType);
            if (requirements.Unsupported != null) return false;
            var surfaceSet = new HashSet<string>(SurfaceAnalyzer.BuildSurfaceCompatKeys(underlyingType), StringComparer.Ordinal);
            return StructuralMatch.IsSatisfiedBy(
                requirements.Methods, requirements.Properties, requirements.Indexers, requirements.Events, surfaceSet);
        }

        private static string RefKindKeyword(RefKind kind)
        {
            switch (kind)
            {
                case RefKind.Ref: return "ref";
                case RefKind.Out: return "out";
                case RefKind.In: return "in";
                default: return "ref readonly";
            }
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
                if (args.Count > 0 &&
                    !HasUnbridgeableByRefArgument(semanticModel, candidate, arguments, parameterIndices, args, cancellationToken))
                    interpretations.Add((candidate, args));
            }
            return interpretations;
        }

        // A forwarding overload keeps every non-ducked parameter unchanged, so a non-ducked ref/out/in
        // argument that doesn't already convert to its parameter (e.g. a by-reference interface the
        // caller hoped to duck) would leave the generated overload uncallable. Such sites are left to
        // the NTF004 path or to silence rather than emitting a dead extension.
        private static bool HasUnbridgeableByRefArgument(
            SemanticModel semanticModel, IMethodSymbol candidate, SeparatedSyntaxList<ArgumentSyntax> arguments,
            IReadOnlyList<int> parameterIndices, List<(int ParamIndex, int SyntaxIndex)> duckedArgs, CancellationToken cancellationToken)
        {
            for (int syntaxIndex = 0; syntaxIndex < arguments.Count; syntaxIndex++)
            {
                if (ContainsSyntaxIndex(duckedArgs, syntaxIndex)) continue;
                var paramIndex = parameterIndices[syntaxIndex];
                if (paramIndex < 0) continue;
                var parameter = candidate.Parameters[paramIndex];
                if (parameter.RefKind == RefKind.None) continue;

                // ref/out require the caller to write the matching keyword; without it the (unchanged)
                // by-reference parameter is uncallable in the forwarding. (in is keyword-optional, so
                // it is judged by type alone below.)
                if ((parameter.RefKind == RefKind.Ref || parameter.RefKind == RefKind.Out) &&
                    !ArgumentRefKindMatches(arguments[syntaxIndex], parameter.RefKind))
                    return true;

                // Compare the argument's *type* to the parameter (not an expression conversion): a
                // by-reference argument is a variable, and `out var x` has no expression conversion
                // yet still binds. A matching type - a struct passthrough, or an out variable of the
                // parameter type - converts implicitly; a structural-only near-miss (Adder vs ICalc)
                // does not, and that is the interpretation that would emit a dead forwarding.
                var argType = semanticModel.GetTypeInfo(arguments[syntaxIndex].Expression, cancellationToken).Type;
                if (argType != null && !semanticModel.Compilation.ClassifyConversion(argType, parameter.Type).IsImplicit)
                    return true;
            }
            return false;
        }

        private static bool ContainsSyntaxIndex(List<(int ParamIndex, int SyntaxIndex)> args, int syntaxIndex)
        {
            foreach (var arg in args)
                if (arg.SyntaxIndex == syntaxIndex) return true;
            return false;
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
                unsupportedMemberName: requirements.Unsupported,
                refKindBlocker: site.RefKindBlocker,
                blockedParameterName: site.BlockedParameterName);
        }

        private static IEnumerable<IParameterSymbol> ForwardedParameters(IMethodSymbol method)
            => HasExplicitExtensionReceiverParameter(method) ? method.Parameters.Skip(1) : method.Parameters;
    }
}

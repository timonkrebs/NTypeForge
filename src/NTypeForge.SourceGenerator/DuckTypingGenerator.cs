using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NTypeForge.SourceGenerator.Models;

namespace NTypeForge.SourceGenerator
{
    // Pipeline orchestrator. CandidateAnalyzer turns each invocation into an equatable
    // CandidateModel; Initialize triages those models into two disjoint output branches
    // (diagnostics vs. emit set); Execute computes the interface->concrete match map over the
    // emit set and hands the result to ProxyEmitter for rendering.
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
            messageFormat: "Interface '{0}' cannot be duck-typed: member '{1}' is not a supported member",
            category: "NTypeForge",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        // Implicit (no Duck<T>) near-miss. Unlike NTF002, the user did not explicitly ask to duck
        // here, so this is only a Warning - it must not mask the user's real call-resolution error,
        // and it fires only at a high-confidence site (see ReportUnsupported). A distinct id (not a
        // second severity of NTF002) keeps it independently suppressible via .editorconfig.
        private static readonly DiagnosticDescriptor UnbridgeableImplicitDuck = new DiagnosticDescriptor(
            id: "NTF003",
            title: "Type structurally matches but cannot be implicitly duck-typed",
            messageFormat: "Type '{0}' structurally matches '{1}' but cannot be implicitly duck-typed: member '{2}' is not a supported member",
            category: "NTypeForge",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        // Implicit (no Duck<T>) near-miss of a different kind: the argument structurally matches the
        // parameter interface, but the parameter is ref/out/in, so a generated proxy can't be passed
        // (a by-reference parameter needs a real variable of the exact type). Warning, not error -
        // the user's own call-resolution error already stands; this only explains why duck typing
        // couldn't bridge an otherwise-matching type. Fired only at a clean near-miss site (see
        // CandidateAnalyzer.TryGetRefKindNearMiss), so it does not fire on ordinary type errors.
        private static readonly DiagnosticDescriptor RefKindBlockedImplicitDuck = new DiagnosticDescriptor(
            id: "NTF004",
            title: "Type structurally matches but cannot be implicitly duck-typed through a by-reference parameter",
            messageFormat: "Type '{0}' structurally matches '{1}' but cannot be implicitly duck-typed: parameter '{2}' is passed by '{3}', and a generated proxy cannot be passed by reference",
            category: "NTypeForge",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // The transform resolves every duck-typing site into a value-equatable CandidateModel
            // (strings/enums/spans only - no ISymbol or SyntaxNode). That keeps symbols out of the
            // cached pipeline, so the compilation is not rooted and edits that don't change any
            // candidate skip regeneration. The output branches consume only these primitives.
            var candidates = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (s, _) => IsPossibleDuckSite(s),
                    transform: static (ctx, ct) => CandidateAnalyzer.GetCandidate(ctx, ct))
                .Where(static c => c != null)
                .Select(static (c, _) => c!);

            // Diagnostics and codegen are separate branches with different equality, and their
            // filters are disjoint (a failed implicit duck with no unsupported member reaches
            // neither: the user's original call error already stands - see ReportDiagnostic).
            //
            // Diagnostics compare by full Key (location included) and report per candidate, so an
            // edit that moves a site re-reports only that diagnostic, at its fresh location.
            var diagnosticCandidates = candidates
                .Where(static c => c.DuckedArgs.Any(a => a.UnsupportedMemberName != null) ||
                                   (c.IsDuckCall && c.DuckedArgs.Any(a => !a.IsSelfMatch)));
            context.RegisterSourceOutput(diagnosticCandidates, static (spc, candidate) => ReportDiagnostic(spc, candidate));

            // A structural match blocked only by a ref/out/in parameter can never be ducked (a proxy
            // can't be passed by reference), but it is a high-confidence near-miss worth a warning.
            // Disjoint from the branch above (no unsupported member, not a Duck<T> call) and from
            // emit below (the RefKindBlocker flag excludes it there).
            var refKindCandidates = candidates
                .Where(static c => c.DuckedArgs.Any(a => a.RefKindBlocker != null && a.IsSelfMatch));
            context.RegisterSourceOutput(refKindCandidates, static (spc, candidate) => ReportRefKindNearMiss(spc, candidate));

            // Codegen compares by CodegenKey (location ignored): the emitted code does not depend
            // on where a site sits, so an edit that merely moves one (whitespace, unrelated code
            // above it) must not invalidate the collected array and re-emit every generated file.
            var emitCandidates = candidates
                .Where(static c => c.DuckedArgs.All(a => a.UnsupportedMemberName == null && a.IsSelfMatch && a.RefKindBlocker == null))
                .WithComparer(CandidateModel.CodegenComparer);
            context.RegisterSourceOutput(emitCandidates.Collect(), static (spc, models) => Execute(spc, models));
        }

        // Syntactic pre-filter, run for every node of every edited file, so it must stay cheap and
        // allocation-free. Both duck-site shapes require the member-access invocation form
        // (TryGetDuckCall via GetDuckInstanceExpression; TryGetMethodArgumentDuck explicitly), and
        // a zero-argument invocation can only be an explicit `x.Duck<T>()` - an implicit
        // method-argument duck needs at least one argument to duck. Everything else (identifier
        // and delegate calls, zero-argument calls like `x.ToString()`) is rejected here, before
        // the transform pays for a semantic bind.
        private static bool IsPossibleDuckSite(SyntaxNode node)
            => node is InvocationExpressionSyntax invocation &&
               invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
               (invocation.ArgumentList.Arguments.Count > 0 || memberAccess.Name.Identifier.ValueText == "Duck");

        // Renders the emit set. Every candidate here already passed the codegen branch filter
        // (every ducked argument supported and structurally self-matching), so there is no triage
        // left to do. The site was admitted as a whole: a forwarding method that ducks only some
        // of its failing arguments could never bind.
        private static void Execute(SourceProductionContext context, ImmutableArray<CandidateModel> candidates)
        {
            if (candidates.IsDefaultOrEmpty) return;

            var allExtensions = new List<CandidateModel>(candidates.Length);
            var interfaceInfo = new Dictionary<string, InterfaceInfo>(StringComparer.Ordinal);
            var concreteInfo = new Dictionary<string, ConcreteInfo>(StringComparer.Ordinal);

            foreach (var candidate in candidates)
            {
                allExtensions.Add(candidate);
                foreach (var arg in candidate.DuckedArgs)
                {
                    RegisterInterface(interfaceInfo, arg);
                    RegisterConcrete(concreteInfo, arg);
                }
            }

            var possibleMatches = ComputeMatches(interfaceInfo, concreteInfo);
            new ProxyEmitter(possibleMatches, interfaceInfo).Emit(context, allExtensions);
        }

        // One candidate of the diagnostics branch, one report: an unsupported interface member
        // (NTF002/NTF003 - see ReportUnsupported), or an explicit Duck<T> with no structural
        // match (NTF001). An implicit method-argument duck with an unmatched argument reports
        // nothing - the original call error already stands - and never reaches this branch.
        private static void ReportDiagnostic(SourceProductionContext context, CandidateModel candidate)
        {
            if (candidate.DuckedArgs.Any(a => a.UnsupportedMemberName != null))
            {
                ReportUnsupported(context, candidate);
            }
            else if (candidate.IsDuckCall && candidate.DuckedArgs.Any(a => !a.IsSelfMatch))
            {
                var arg = candidate.DuckedArgs[0];
                context.ReportDiagnostic(Diagnostic.Create(
                    NoStructuralMatch, candidate.ToLocation(),
                    arg.UnderlyingFq, arg.InterfaceFq));
            }
        }

        // An interface member NTypeForge can't proxy was found. An explicit Duck<T> is always a hard
        // NTF002 error. An implicit method-argument duck only earns a warning, and only at a
        // high-confidence site where every ducked argument satisfies every proxyable member of its
        // interface (IsSelfMatch across the whole site) - so the unsupported member really is the
        // only blocker. The warning names each argument whose interface carries one, over a
        // non-empty instance contract. Ambiguous sites never reach here: CandidateAnalyzer drops a
        // failed call with more than one duckable interpretation, so any model built for an
        // implicit duck is already the unique interpretation of its call.
        private static void ReportUnsupported(SourceProductionContext context, CandidateModel candidate)
        {
            if (candidate.IsDuckCall)
            {
                var arg = candidate.DuckedArgs[0];
                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedInterfaceMember, candidate.ToLocation(),
                    arg.InterfaceFq, arg.UnsupportedMemberName));
                return;
            }

            if (candidate.DuckedArgs.Any(a => !a.IsSelfMatch)) return;

            foreach (var arg in candidate.DuckedArgs)
            {
                if (arg.UnsupportedMemberName != null && HasInstanceRequirements(arg))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        UnbridgeableImplicitDuck, candidate.ToLocation(),
                        arg.UnderlyingFq, arg.InterfaceFq, arg.UnsupportedMemberName));
                }
            }
        }

        private static bool HasInstanceRequirements(DuckedArgModel arg)
            => arg.MethodRequirements.Count > 0 || arg.PropertyRequirements.Count > 0 ||
               arg.IndexerRequirements.Count > 0 || arg.EventRequirements.Count > 0;

        // One NTF004 per ref/out/in-blocked argument of a near-miss site, naming the type, the
        // interface it structurally matches, the blocking parameter, and its ref kind.
        private static void ReportRefKindNearMiss(SourceProductionContext context, CandidateModel candidate)
        {
            foreach (var arg in candidate.DuckedArgs)
            {
                if (arg.RefKindBlocker != null && arg.IsSelfMatch)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        RefKindBlockedImplicitDuck, candidate.ToLocation(),
                        arg.UnderlyingFq, arg.InterfaceFq, arg.BlockedParameterName, arg.RefKindBlocker));
                }
            }
        }

        private static void RegisterInterface(Dictionary<string, InterfaceInfo> interfaceInfo, DuckedArgModel arg)
        {
            if (interfaceInfo.ContainsKey(arg.InterfaceFq)) return;
            interfaceInfo[arg.InterfaceFq] = new InterfaceInfo
            {
                Fq = arg.InterfaceFq,
                MinimalName = arg.InterfaceMinimalName,
                MethodRequirements = arg.MethodRequirements,
                PropertyRequirements = arg.PropertyRequirements,
                IndexerRequirements = arg.IndexerRequirements,
                EventRequirements = arg.EventRequirements,
            };
        }

        private static void RegisterConcrete(Dictionary<string, ConcreteInfo> concreteInfo, DuckedArgModel arg)
        {
            if (arg.UnderlyingIsInterface || concreteInfo.ContainsKey(arg.UnderlyingFq)) return;
            concreteInfo[arg.UnderlyingFq] = new ConcreteInfo
            {
                Fq = arg.UnderlyingFq,
                Namespace = arg.UnderlyingNamespace,
                MinimalName = arg.UnderlyingMinimalName,
                BaseDepth = arg.UnderlyingBaseDepth,
                SurfaceKeys = new HashSet<string>(arg.UnderlyingSurfaceCompatKeys, StringComparer.Ordinal),
            };
        }

        private static Dictionary<string, List<ConcreteInfo>> ComputeMatches(
            Dictionary<string, InterfaceInfo> interfaceInfo,
            Dictionary<string, ConcreteInfo> concreteInfo)
        {
            // For each interface, the concrete types that structurally match it, most-derived
            // first: the generated unwrap branches test `TryUnbox<C>` (an `is C` check), which is
            // also true for subtypes of C, so a derived type must win its own branch ahead of its
            // base. Ties broken by fully-qualified name for deterministic output.
            // The concrete order (most-derived first, name-tiebroken) is the same for every
            // interface, so sort once and only re-filter per interface.
            var sortedConcretes = concreteInfo.Values
                .OrderByDescending(c => c.BaseDepth)
                .ThenBy(c => c.Fq, StringComparer.Ordinal)
                .ToList();

            var possibleMatches = new Dictionary<string, List<ConcreteInfo>>(StringComparer.Ordinal);
            foreach (var iface in interfaceInfo.Values.OrderBy(i => i.Fq, StringComparer.Ordinal))
            {
                possibleMatches[iface.Fq] = sortedConcretes.Where(c => ConcreteSatisfies(iface, c)).ToList();
            }
            return possibleMatches;
        }

        private static bool ConcreteSatisfies(InterfaceInfo iface, ConcreteInfo concrete)
            => StructuralMatch.IsSatisfiedBy(
                iface.MethodRequirements, iface.PropertyRequirements, iface.IndexerRequirements,
                iface.EventRequirements, concrete.SurfaceKeys);
    }
}

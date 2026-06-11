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
    // CandidateModel; this class triages those models (emit set vs. diagnostic), computes the
    // interface->concrete match map, and hands the result to ProxyEmitter for rendering.
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

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // The transform resolves every duck-typing site into a value-equatable CandidateModel
            // (strings/enums/spans only - no ISymbol or SyntaxNode). That keeps symbols out of the
            // cached pipeline, so the compilation is not rooted and edits that don't change any
            // candidate skip regeneration. Execute consumes only these primitives.
            var candidates = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (s, _) => s is InvocationExpressionSyntax,
                    transform: static (ctx, _) => CandidateAnalyzer.GetCandidate(ctx))
                .Where(static c => c != null)
                .Select(static (c, _) => c!);

            context.RegisterSourceOutput(candidates.Collect(), static (spc, models) => Execute(spc, models));
        }

        private static void Execute(SourceProductionContext context, ImmutableArray<CandidateModel> candidates)
        {
            if (candidates.IsDefaultOrEmpty) return;

            var allExtensions = new List<CandidateModel>();
            var interfaceInfo = new Dictionary<string, InterfaceInfo>(StringComparer.Ordinal);
            var concreteInfo = new Dictionary<string, ConcreteInfo>(StringComparer.Ordinal);

            foreach (var candidate in candidates)
            {
                ProcessCandidate(context, candidate, allExtensions, interfaceInfo, concreteInfo);
            }

            if (allExtensions.Count == 0) return;

            var possibleMatches = ComputeMatches(interfaceInfo, concreteInfo);
            new ProxyEmitter(possibleMatches, interfaceInfo).Emit(context, allExtensions);
        }

        // Sort one candidate into the emit set (every ducked argument structurally self-matches),
        // a diagnostic (an unmatched Duck<T> or an unsupported member), or silent drop (an
        // implicit method-argument duck with an unmatched argument, where the original call error
        // already stands). The site is bridged or dropped as a whole: a forwarding method that
        // ducks only some of its failing arguments could never bind.
        private static void ProcessCandidate(
            SourceProductionContext context, CandidateModel candidate,
            List<CandidateModel> allExtensions,
            Dictionary<string, InterfaceInfo> interfaceInfo,
            Dictionary<string, ConcreteInfo> concreteInfo)
        {
            if (candidate.DuckedArgs.Any(a => a.UnsupportedMemberName != null))
            {
                ReportUnsupported(context, candidate);
                return;
            }

            if (candidate.DuckedArgs.Any(a => !a.IsSelfMatch))
            {
                if (candidate.IsDuckCall)
                {
                    var arg = candidate.DuckedArgs[0];
                    context.ReportDiagnostic(Diagnostic.Create(
                        NoStructuralMatch, candidate.ToLocation(),
                        arg.UnderlyingFq, arg.InterfaceFq));
                }
                return;
            }

            allExtensions.Add(candidate);
            foreach (var arg in candidate.DuckedArgs)
            {
                RegisterInterface(interfaceInfo, arg);
                RegisterConcrete(concreteInfo, arg);
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

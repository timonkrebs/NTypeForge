using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace NTypeForge.Variadic.SourceGenerator
{
    // A standalone incremental generator that emits a family of variadic delegates
    // (VariadicFunc/VariadicAction) and N-ary sequence combinators (Zip/ForEach) for every arity
    // up to N. This is the classic "scale to an arbitrary number of arguments" pattern that C#
    // can't express with a single generic - each arity is its own generated declaration.
    //
    // N defaults to 8 and is set per-assembly with `[assembly: VariadicArity(N)]`. The config
    // attribute is provided by the generator itself (post-initialization output), so a consumer
    // references the analyzer and nothing else. The whole pipeline reduces to a single equatable
    // int, so it re-emits only when the requested arity actually changes.
    [Generator]
    public sealed class VariadicGenerator : IIncrementalGenerator
    {
        internal const int DefaultArity = 8;
        internal const int MinArity = 1;
        internal const int MaxArity = 64;
        private const string AttributeMetadataName = "NTypeForge.Variadic.VariadicArityAttribute";

        private static readonly DiagnosticDescriptor ArityTooSmall = new DiagnosticDescriptor(
            id: "NTFV001",
            title: "Variadic arity too small",
            messageFormat: "VariadicArity must be at least {0}; the requested value {1} was raised to {2}",
            category: "NTypeForge.Variadic",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor ArityTooLarge = new DiagnosticDescriptor(
            id: "NTFV002",
            title: "Variadic arity clamped",
            messageFormat: "VariadicArity {0} exceeds the supported maximum {1} and was clamped to it",
            category: "NTypeForge.Variadic",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // The config attribute lives in the compilation before the main pipeline runs, so both
            // ForAttributeWithMetadataName below and the user's `[assembly: VariadicArity(...)]`
            // bind against it.
            context.RegisterPostInitializationOutput(static ctx =>
                ctx.AddSource("VariadicArityAttribute.g.cs", VariadicEmitter.AttributeSource));

            var requested = context.SyntaxProvider.ForAttributeWithMetadataName(
                    AttributeMetadataName,
                    predicate: static (_, _) => true,
                    transform: static (ctx, _) => ReadRequestedArity(ctx))
                .Collect();

            var config = requested.Select(static (values, _) => Resolve(values));

            context.RegisterSourceOutput(config, static (spc, cfg) => Emit(spc, cfg));
        }

        private static int ReadRequestedArity(GeneratorAttributeSyntaxContext context)
        {
            foreach (var attribute in context.Attributes)
            {
                if (attribute.ConstructorArguments.Length == 1 &&
                    attribute.ConstructorArguments[0].Value is int value)
                    return value;
            }
            return DefaultArity;
        }

        // 0 attributes -> silent default. 1+ (AllowMultiple is false, but a duplicate could still
        // be authored) -> the largest requested value wins, so the result is order-independent and
        // therefore deterministic.
        private static ArityConfig Resolve(ImmutableArray<int> requestedValues)
        {
            if (requestedValues.IsDefaultOrEmpty)
                return new ArityConfig(DefaultArity, DefaultArity, hasAttribute: false);

            int requested = requestedValues.Max();
            int effective = requested < MinArity ? MinArity : requested > MaxArity ? MaxArity : requested;
            return new ArityConfig(effective, requested, hasAttribute: true);
        }

        private static void Emit(SourceProductionContext context, ArityConfig config)
        {
            if (config.HasAttribute && config.Requested < MinArity)
                context.ReportDiagnostic(Diagnostic.Create(ArityTooSmall, Location.None, MinArity, config.Requested, config.Effective));
            else if (config.HasAttribute && config.Requested > MaxArity)
                context.ReportDiagnostic(Diagnostic.Create(ArityTooLarge, Location.None, config.Requested, MaxArity));

            context.AddSource("Variadic.g.cs", VariadicEmitter.EmitAll(config.Effective));
        }
    }
}

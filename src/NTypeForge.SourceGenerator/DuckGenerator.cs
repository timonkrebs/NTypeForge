using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace NTypeForge.SourceGenerator
{
    [Generator]
    public class DuckGenerator : IIncrementalGenerator
    {
        private const string Namespace = "NTypeForge";
        private const string ExtensionClassName = "DuckExtensions";
        private const string ExtensionMethodName = "AsDuck";

        private const string InitialSources = @"
namespace NTypeForge
{
    public static class " + ExtensionClassName + @"
    {
        public static T " + ExtensionMethodName + @"<T>(this object obj) where T : class
        {
            throw new System.InvalidOperationException(""Source generator failed to generate the duck wrapper for "" + obj.GetType().FullName + "" to "" + typeof(T).FullName);
        }
    }
}
";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(i => i.AddSource("DuckExtensions.g.cs", SourceText.From(InitialSources, Encoding.UTF8)));

            var calls = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: (s, _) => IsPotentialDuckCall(s),
                    transform: (ctx, _) => GetDuckCallInfo(ctx))
                .Where(m => m != null);

            var compilationAndCalls = context.CompilationProvider.Combine(calls.Collect());

            context.RegisterSourceOutput(compilationAndCalls, (spc, source) => Execute(source.Left, source.Right!, spc));
        }

        private static bool IsPotentialDuckCall(SyntaxNode node)
        {
            return node is InvocationExpressionSyntax invocation &&
                   invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                   memberAccess.Name is GenericNameSyntax genericName &&
                   genericName.Identifier.Text == ExtensionMethodName;
        }

        private static DuckCallInfo? GetDuckCallInfo(GeneratorSyntaxContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;

            var memberSymbol = context.SemanticModel.GetSymbolInfo(memberAccess).Symbol as IMethodSymbol;
            if (memberSymbol == null) return null;

            if (memberSymbol.Name != ExtensionMethodName || memberSymbol.ContainingType.Name != ExtensionClassName || memberSymbol.ContainingNamespace.ToDisplayString() != Namespace)
                return null;

            var targetType = context.SemanticModel.GetTypeInfo(memberAccess.Expression).Type;
            var interfaceType = memberSymbol.ReturnType; // The generic parameter T

            if (targetType == null || interfaceType == null || interfaceType.TypeKind != TypeKind.Interface)
                return null;

            return new DuckCallInfo(targetType, interfaceType);
        }

        private void Execute(Compilation compilation, ImmutableArray<DuckCallInfo> calls, SourceProductionContext context)
        {
            if (calls.IsDefaultOrEmpty) return;

            var distinctPairs = calls.Distinct(DuckCallInfoComparer.Instance);

            foreach (var pair in distinctPairs)
            {
                GenerateWrapper(compilation, pair, context);
            }
        }

        private void GenerateWrapper(Compilation compilation, DuckCallInfo pair, SourceProductionContext context)
        {
            var targetType = pair.TargetType;
            var interfaceType = (INamedTypeSymbol)pair.InterfaceType;

            var wrapperName = SanitizeIdentifier($"Duck_{targetType.ToDisplayString()}_{interfaceType.ToDisplayString()}");
            var sb = new StringBuilder();

            sb.AppendLine("using System;");
            sb.AppendLine();
            sb.AppendLine("namespace NTypeForge");
            sb.AppendLine("{");
            sb.AppendLine($"    internal class {wrapperName} : {interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}");
            sb.AppendLine("    {");
            sb.AppendLine($"        private readonly {targetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} _target;");
            sb.AppendLine();
            sb.AppendLine($"        public {wrapperName}({targetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} target)");
            sb.AppendLine("        {");
            sb.AppendLine("            _target = target;");
            sb.AppendLine("        }");
            sb.AppendLine();

            var membersToImplement = GetAllInterfaceMembers(interfaceType);
            var implementedMembers = new HashSet<string>();

            foreach (var member in membersToImplement)
            {
                if (member is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary)
                {
                    var signature = GetMethodSignature(method);
                    if (implementedMembers.Add(signature))
                    {
                        if (!TryImplementMethod(sb, targetType, method))
                        {
                            // TODO: report diagnostic
                        }
                    }
                }
                else if (member is IPropertySymbol property)
                {
                    string key = property.IsIndexer
                        ? GetIndexerSignature(property)
                        : $"prop:{property.Name}";

                    if (implementedMembers.Add(key))
                    {
                        if (!TryImplementProperty(sb, targetType, property))
                        {
                            // TODO: report diagnostic
                        }
                    }
                }
            }

            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine($"    public static class {wrapperName}_Extensions");
            sb.AppendLine("    {");
            sb.AppendLine($"        public static T {ExtensionMethodName}<T>(this {targetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} obj)");
            sb.AppendLine($"            where T : class, {interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}");
            sb.AppendLine("        {");
            sb.AppendLine($"            return (T)(object)new {wrapperName}(obj);");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            context.AddSource($"{wrapperName}.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
        }

        private IEnumerable<ISymbol> GetAllInterfaceMembers(INamedTypeSymbol interfaceType)
        {
            return interfaceType.GetMembers().Concat(interfaceType.AllInterfaces.SelectMany(i => i.GetMembers()));
        }

        private string GetMethodSignature(IMethodSymbol method)
        {
            var sb = new StringBuilder();
            sb.Append(method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            sb.Append(" ");
            sb.Append(method.Name);
            sb.Append("(");
            foreach (var p in method.Parameters)
            {
                sb.Append(GetParameterModifier(p));
                sb.Append(p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                sb.Append(",");
            }
            sb.Append(")");
            return sb.ToString();
        }

        private string GetIndexerSignature(IPropertySymbol property)
        {
            var sb = new StringBuilder();
            sb.Append(property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            sb.Append(" this[");
            foreach (var p in property.Parameters)
            {
                sb.Append(p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                sb.Append(",");
            }
            sb.Append("]");
            return sb.ToString();
        }

        private bool TryImplementMethod(StringBuilder sb, ITypeSymbol targetType, IMethodSymbol interfaceMethod)
        {
            var targetMethod = FindMatchingMethod(targetType, interfaceMethod);

            if (targetMethod == null) return false;

            sb.Append($"        public {interfaceMethod.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {interfaceMethod.Name}(");
            sb.Append(string.Join(", ", interfaceMethod.Parameters.Select(p => $"{GetParameterModifier(p)}{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}")));
            sb.AppendLine(")");
            sb.AppendLine("        {");
            sb.Append("            ");
            if (!interfaceMethod.ReturnsVoid) sb.Append("return ");
            sb.Append($"_target.{interfaceMethod.Name}(");
            sb.Append(string.Join(", ", interfaceMethod.Parameters.Select(p => $"{GetParameterModifier(p)}{p.Name}")));
            sb.AppendLine(");");
            sb.AppendLine("        }");
            sb.AppendLine();

            return true;
        }

        private IMethodSymbol? FindMatchingMethod(ITypeSymbol targetType, IMethodSymbol interfaceMethod)
        {
            return targetType.GetMembers().OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.Name == interfaceMethod.Name && m.DeclaredAccessibility == Accessibility.Public && MatchesSignature(m, interfaceMethod));
        }

        private bool TryImplementProperty(StringBuilder sb, ITypeSymbol targetType, IPropertySymbol interfaceProperty)
        {
            IPropertySymbol? targetProperty;
            if (interfaceProperty.IsIndexer)
            {
                targetProperty = targetType.GetMembers().OfType<IPropertySymbol>()
                    .FirstOrDefault(p => p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public && MatchesIndexerSignature(p, interfaceProperty));
            }
            else
            {
                targetProperty = targetType.GetMembers().OfType<IPropertySymbol>()
                    .FirstOrDefault(p => p.Name == interfaceProperty.Name && p.DeclaredAccessibility == Accessibility.Public && SymbolEqualityComparer.Default.Equals(p.Type, interfaceProperty.Type));
            }

            if (targetProperty == null) return false;

            if (interfaceProperty.IsIndexer)
            {
                sb.Append($"        public {interfaceProperty.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} this[");
                sb.Append(string.Join(", ", interfaceProperty.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}")));
                sb.AppendLine("]");
            }
            else
            {
                sb.AppendLine($"        public {interfaceProperty.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {interfaceProperty.Name}");
            }
            sb.AppendLine("        {");
            if (interfaceProperty.GetMethod != null)
            {
                if (targetProperty.GetMethod == null) return false;
                if (interfaceProperty.IsIndexer)
                    sb.AppendLine($"            get => _target[{string.Join(", ", interfaceProperty.Parameters.Select(p => p.Name))}];");
                else
                    sb.AppendLine($"            get => _target.{interfaceProperty.Name};");
            }
            if (interfaceProperty.SetMethod != null)
            {
                if (targetProperty.SetMethod == null) return false;
                if (interfaceProperty.IsIndexer)
                    sb.AppendLine($"            set => _target[{string.Join(", ", interfaceProperty.Parameters.Select(p => p.Name))}] = value;");
                else
                    sb.AppendLine($"            set => _target.{interfaceProperty.Name} = value;");
            }
            sb.AppendLine("        }");
            sb.AppendLine();

            return true;
        }

        private bool MatchesIndexerSignature(IPropertySymbol targetIndexer, IPropertySymbol interfaceIndexer)
        {
            if (!SymbolEqualityComparer.Default.Equals(targetIndexer.Type, interfaceIndexer.Type)) return false;
            if (targetIndexer.Parameters.Length != interfaceIndexer.Parameters.Length) return false;
            for (int i = 0; i < targetIndexer.Parameters.Length; i++)
            {
                if (!SymbolEqualityComparer.Default.Equals(targetIndexer.Parameters[i].Type, interfaceIndexer.Parameters[i].Type)) return false;
            }
            return true;
        }

        private string GetParameterModifier(IParameterSymbol p)
        {
            return p.RefKind switch
            {
                RefKind.Ref => "ref ",
                RefKind.Out => "out ",
                RefKind.In => "in ",
                _ => ""
            };
        }

        private bool MatchesSignature(IMethodSymbol targetMethod, IMethodSymbol interfaceMethod)
        {
            if (!SymbolEqualityComparer.Default.Equals(targetMethod.ReturnType, interfaceMethod.ReturnType)) return false;
            if (targetMethod.Parameters.Length != interfaceMethod.Parameters.Length) return false;
            for (int i = 0; i < targetMethod.Parameters.Length; i++)
            {
                if (!SymbolEqualityComparer.Default.Equals(targetMethod.Parameters[i].Type, interfaceMethod.Parameters[i].Type)) return false;
                if (targetMethod.Parameters[i].RefKind != interfaceMethod.Parameters[i].RefKind) return false;
            }
            return true;
        }

        private static string SanitizeIdentifier(string identifier)
        {
            var sb = new StringBuilder();
            foreach (char c in identifier)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
                else
                    sb.Append('_');
            }
            return sb.ToString();
        }

        private class DuckCallInfo
        {
            public ITypeSymbol TargetType { get; }
            public ITypeSymbol InterfaceType { get; }

            public DuckCallInfo(ITypeSymbol targetType, ITypeSymbol interfaceType)
            {
                TargetType = targetType;
                InterfaceType = interfaceType;
            }
        }

        private class DuckCallInfoComparer : IEqualityComparer<DuckCallInfo>
        {
            public static DuckCallInfoComparer Instance { get; } = new DuckCallInfoComparer();

            public bool Equals(DuckCallInfo x, DuckCallInfo y)
            {
                return SymbolEqualityComparer.Default.Equals(x.TargetType, y.TargetType) &&
                       SymbolEqualityComparer.Default.Equals(x.InterfaceType, y.InterfaceType);
            }

            public int GetHashCode(DuckCallInfo obj)
            {
                return SymbolEqualityComparer.Default.GetHashCode(obj.TargetType) ^
                       SymbolEqualityComparer.Default.GetHashCode(obj.InterfaceType);
            }
        }
    }
}

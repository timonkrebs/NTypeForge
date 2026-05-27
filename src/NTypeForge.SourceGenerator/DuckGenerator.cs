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
        private const string ExtensionMethodName = "Duck";

        private const string InitialSources = @"
namespace NTypeForge
{
    public interface IDuckHandler<T> where T : class { }

    public static class Duck
    {
        public static IDuckHandler<T> Handler<T>() where T : class => null!;
    }

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

            var handlerCalls = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: (s, _) => IsPotentialHandlerCall(s),
                    transform: (ctx, _) => GetHandlerCallInfo(ctx))
                .Where(m => m != null);

            var allCalls = calls.Collect().Combine(handlerCalls.Collect())
                .Select((pair, _) => pair.Left.AddRange(pair.Right));

            var compilationAndCallsCombined = context.CompilationProvider.Combine(allCalls);

            context.RegisterSourceOutput(compilationAndCallsCombined, (spc, source) => Execute(source.Left, source.Right!, spc));
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

        private static bool IsPotentialHandlerCall(SyntaxNode node)
        {
            return node is InvocationExpressionSyntax invocation &&
                   invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                   memberAccess.Name is GenericNameSyntax genericName &&
                   genericName.Identifier.Text == "Handler" &&
                   memberAccess.Expression is IdentifierNameSyntax id &&
                   id.Identifier.Text == "Duck";
        }

        private static DuckCallInfo? GetHandlerCallInfo(GeneratorSyntaxContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;
            var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;

            var memberSymbol = context.SemanticModel.GetSymbolInfo(memberAccess).Symbol as IMethodSymbol;
            if (memberSymbol == null) return null;

            if (memberSymbol.Name != "Handler" || memberSymbol.ContainingType.Name != "Duck" || memberSymbol.ContainingNamespace.ToDisplayString() != Namespace)
                return null;

            var interfaceType = (memberSymbol.ReturnType as INamedTypeSymbol)?.TypeArguments.FirstOrDefault();
            if (interfaceType == null || interfaceType.TypeKind != TypeKind.Interface)
                return null;

            return new DuckCallInfo(null, interfaceType);
        }

        private void Execute(Compilation compilation, ImmutableArray<DuckCallInfo> calls, SourceProductionContext context)
        {
            if (calls.IsDefaultOrEmpty) return;

            var distinctPairs = calls.Distinct(DuckCallInfoComparer.Instance);

            foreach (var pair in distinctPairs)
            {
                if (pair.TargetType != null)
                {
                    GenerateWrapper(compilation, pair, context);
                }
                else
                {
                    GenerateHandlerExtensions(compilation, pair, context);
                }
            }
        }

        private void GenerateWrapper(Compilation compilation, DuckCallInfo pair, SourceProductionContext context)
        {
            var targetType = pair.TargetType!;
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

            var membersToImplement = GetAllInterfaceMembers(interfaceType).ToList();
            var implementedMembers = new HashSet<string>();

            foreach (var member in membersToImplement)
            {
                if (member is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary)
                {
                    var signature = GetMethodSignature(method);
                    if (implementedMembers.Add(signature))
                    {
                        if (!TryImplementMethod(sb, targetType, method) && method.IsAbstract)
                        {
                            // TODO: report diagnostic - abstract member MUST be implemented
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
                        if (!TryImplementProperty(sb, targetType, property) && property.IsAbstract)
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
            sb.AppendLine();

            implementedMembers.Clear();
            foreach (var member in membersToImplement)
            {
                if (member is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary)
                {
                    var signature = GetMethodSignature(method);
                    if (implementedMembers.Add(signature))
                    {
                        // Generate structural extension method if it doesn't exist on target
                        if (FindMatchingMethod(targetType, method) == null)
                        {
                            GenerateStructuralMethodExtension(sb, targetType, interfaceType, method);
                        }
                    }
                }
                else if (member is IPropertySymbol property)
                {
                    string key = property.IsIndexer ? GetIndexerSignature(property) : $"prop:{property.Name}";
                    if (implementedMembers.Add(key))
                    {
                        if (FindMatchingProperty(targetType, property) == null)
                        {
                            GenerateStructuralPropertyExtension(sb, targetType, interfaceType, property);
                        }
                    }
                }
            }

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

        private IPropertySymbol? FindMatchingProperty(ITypeSymbol targetType, IPropertySymbol interfaceProperty)
        {
            if (interfaceProperty.IsIndexer)
            {
                return targetType.GetMembers().OfType<IPropertySymbol>()
                    .FirstOrDefault(p => p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public && MatchesIndexerSignature(p, interfaceProperty));
            }
            else
            {
                return targetType.GetMembers().OfType<IPropertySymbol>()
                    .FirstOrDefault(p => p.Name == interfaceProperty.Name && p.DeclaredAccessibility == Accessibility.Public && SymbolEqualityComparer.Default.Equals(p.Type, interfaceProperty.Type));
            }
        }

        private bool TryImplementProperty(StringBuilder sb, ITypeSymbol targetType, IPropertySymbol interfaceProperty)
        {
            IPropertySymbol? targetProperty = FindMatchingProperty(targetType, interfaceProperty);

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

        private void GenerateHandlerExtensions(Compilation compilation, DuckCallInfo pair, SourceProductionContext context)
        {
            var interfaceType = (INamedTypeSymbol)pair.InterfaceType;
            var wrapperName = SanitizeIdentifier($"Duck_Lambda_{interfaceType.ToDisplayString()}");
            var sb = new StringBuilder();

            sb.AppendLine("using System;");
            sb.AppendLine();
            sb.AppendLine("namespace NTypeForge");
            sb.AppendLine("{");

            var members = GetAllInterfaceMembers(interfaceType).ToList();
            var methods = members.OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary).ToList();
            var properties = members.OfType<IPropertySymbol>().ToList();

            var delegateMap = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
            int delegateCount = 0;

            foreach (var method in methods)
            {
                var delegateName = $"{wrapperName}_Delegate_{delegateCount++}";
                sb.Append($"    internal delegate {method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {delegateName}(");
                sb.Append(string.Join(", ", method.Parameters.Select(p => $"{GetParameterModifier(p)}{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}")));
                sb.AppendLine(");");
                delegateMap[method] = delegateName;
            }

            foreach (var prop in properties)
            {
                if (prop.GetMethod != null)
                {
                    var delegateName = $"{wrapperName}_Delegate_{delegateCount++}";
                    if (prop.IsIndexer)
                    {
                        sb.Append($"    internal delegate {prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {delegateName}(");
                        sb.Append(string.Join(", ", prop.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}")));
                        sb.AppendLine(");");
                    }
                    else
                    {
                        sb.AppendLine($"    internal delegate {prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {delegateName}();");
                    }
                    delegateMap[prop.GetMethod] = delegateName;
                }
                if (prop.SetMethod != null)
                {
                    var delegateName = $"{wrapperName}_Delegate_{delegateCount++}";
                    if (prop.IsIndexer)
                    {
                        sb.Append($"    internal delegate void {delegateName}(");
                        sb.Append(string.Join(", ", prop.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}")));
                        sb.AppendLine($", {prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} value);");
                    }
                    else
                    {
                        sb.AppendLine($"    internal delegate void {delegateName}({prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} value);");
                    }
                    delegateMap[prop.SetMethod] = delegateName;
                }
            }

            sb.AppendLine();
            sb.AppendLine($"    internal class {wrapperName} : {interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}");
            sb.AppendLine("    {");

            foreach (var entry in delegateMap)
            {
                sb.AppendLine($"        private readonly {entry.Value} _{entry.Value};");
            }

            sb.AppendLine();
            sb.Append($"        public {wrapperName}(");
            sb.Append(string.Join(", ", delegateMap.Select(e => $"{e.Value} {e.Value}")));
            sb.AppendLine(")");
            sb.AppendLine("        {");
            foreach (var entry in delegateMap)
            {
                sb.AppendLine($"            _{entry.Value} = {entry.Value};");
            }
            sb.AppendLine("        }");
            sb.AppendLine();

            foreach (var method in methods)
            {
                var delName = delegateMap[method];
                sb.Append($"        public {method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {method.Name}(");
                sb.Append(string.Join(", ", method.Parameters.Select(p => $"{GetParameterModifier(p)}{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}")));
                sb.AppendLine(")");
                sb.AppendLine("        {");
                sb.Append("            ");
                if (!method.ReturnsVoid) sb.Append("return ");
                sb.Append($"_{delName}(");
                sb.Append(string.Join(", ", method.Parameters.Select(p => $"{GetParameterModifier(p)}{p.Name}")));
                sb.AppendLine(");");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            foreach (var prop in properties)
            {
                if (prop.IsIndexer)
                {
                    sb.Append($"        public {prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} this[");
                    sb.Append(string.Join(", ", prop.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}")));
                    sb.AppendLine("]");
                }
                else
                {
                    sb.AppendLine($"        public {prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {prop.Name}");
                }
                sb.AppendLine("        {");
                if (prop.GetMethod != null)
                {
                    var delName = delegateMap[prop.GetMethod];
                    sb.Append("            get => ");
                    sb.Append($"_{delName}(");
                    if (prop.IsIndexer) sb.Append(string.Join(", ", prop.Parameters.Select(p => p.Name)));
                    sb.AppendLine(");");
                }
                if (prop.SetMethod != null)
                {
                    var delName = delegateMap[prop.SetMethod];
                    sb.Append("            set => ");
                    sb.Append($"_{delName}(");
                    if (prop.IsIndexer)
                    {
                        sb.Append(string.Join(", ", prop.Parameters.Select(p => p.Name)));
                        sb.Append(", ");
                    }
                    sb.AppendLine("value);");
                }
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine($"    public static class {wrapperName}_HandlerExtensions");
            sb.AppendLine("    {");
            sb.Append($"        public static {interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} Create(this IDuckHandler<{interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}> handler");
            foreach (var entry in delegateMap)
            {
                var paramName = GetParameterNameForSymbol(entry.Key);
                sb.Append($", {entry.Value} {paramName}");
            }
            sb.AppendLine(")");
            sb.AppendLine("        {");
            sb.Append($"            return new {wrapperName}(");
            sb.Append(string.Join(", ", delegateMap.Select(e => GetParameterNameForSymbol(e.Key))));
            sb.AppendLine(");");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            context.AddSource($"{wrapperName}.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
        }

        private string GetParameterNameForSymbol(ISymbol symbol)
        {
            if (symbol is IMethodSymbol method && method.MethodKind != MethodKind.Ordinary)
            {
                var prefix = method.MethodKind == MethodKind.PropertyGet ? "get" : "set";
                return $"{prefix}_{method.AssociatedSymbol!.Name}";
            }
            return symbol.Name;
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

        private void GenerateStructuralMethodExtension(StringBuilder sb, ITypeSymbol targetType, INamedTypeSymbol interfaceType, IMethodSymbol method)
        {
            sb.Append($"        public static {method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {method.Name}(this {targetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} obj");
            if (method.Parameters.Length > 0)
            {
                sb.Append(", ");
                sb.Append(string.Join(", ", method.Parameters.Select(p => $"{GetParameterModifier(p)}{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}")));
            }
            sb.AppendLine(")");
            sb.AppendLine("        {");
            sb.Append($"            ");

            bool implementsInterface = targetType.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, interfaceType));
            if (implementsInterface)
            {
                if (!method.ReturnsVoid) sb.Append("return ");
                sb.Append($"(({interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})obj).{method.Name}(");
            }
            else
            {
                if (!method.ReturnsVoid) sb.Append("return ");
                sb.Append($"obj.Duck<{interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>().{method.Name}(");
            }

            sb.Append(string.Join(", ", method.Parameters.Select(p => $"{GetParameterModifier(p)}{p.Name}")));
            sb.AppendLine(");");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private void GenerateStructuralPropertyExtension(StringBuilder sb, ITypeSymbol targetType, INamedTypeSymbol interfaceType, IPropertySymbol property)
        {
            // Not straightforward as extension properties don't exist in C#.
            // We could generate GetX/SetX methods, but the request was "looks like the method was called directly".
            // For properties, we'll skip for now or generate methods if appropriate.
            // "so that it looks like the method was called directly" - implies methods.
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
            public ITypeSymbol? TargetType { get; }
            public ITypeSymbol InterfaceType { get; }

            public DuckCallInfo(ITypeSymbol? targetType, ITypeSymbol interfaceType)
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
                int hash = SymbolEqualityComparer.Default.GetHashCode(obj.InterfaceType);
                if (obj.TargetType != null)
                    hash ^= SymbolEqualityComparer.Default.GetHashCode(obj.TargetType);
                return hash;
            }
        }
    }
}

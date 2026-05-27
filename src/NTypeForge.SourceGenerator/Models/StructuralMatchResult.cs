using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace NTypeForge.SourceGenerator.Models
{
    internal class ProxyMethodInfo
    {
        public string Name { get; }
        public ITypeSymbol ReturnType { get; }
        public IReadOnlyList<IParameterSymbol> Parameters { get; }

        public ProxyMethodInfo(string name, ITypeSymbol returnType, IReadOnlyList<IParameterSymbol> parameters)
        {
            Name = name;
            ReturnType = returnType;
            Parameters = parameters;
        }
    }

    internal class StructuralMatchResult
    {
        public bool IsMatch { get; }
        public IReadOnlyList<ProxyMethodInfo> MatchedMethods { get; }

        public StructuralMatchResult(bool isMatch, IReadOnlyList<ProxyMethodInfo> matchedMethods)
        {
            IsMatch = isMatch;
            MatchedMethods = matchedMethods;
        }
    }
}

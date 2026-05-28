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

    internal class ProxyPropertyInfo
    {
        public string Name { get; }
        public ITypeSymbol Type { get; }
        public bool HasGet { get; }
        public bool HasSet { get; }

        public ProxyPropertyInfo(string name, ITypeSymbol type, bool hasGet, bool hasSet)
        {
            Name = name;
            Type = type;
            HasGet = hasGet;
            HasSet = hasSet;
        }
    }

    internal class StructuralMatchResult
    {
        public bool IsMatch { get; }
        public IReadOnlyList<ProxyMethodInfo> MatchedMethods { get; }
        public IReadOnlyList<ProxyPropertyInfo> MatchedProperties { get; }

        public StructuralMatchResult(bool isMatch, IReadOnlyList<ProxyMethodInfo> matchedMethods, IReadOnlyList<ProxyPropertyInfo> matchedProperties)
        {
            IsMatch = isMatch;
            MatchedMethods = matchedMethods;
            MatchedProperties = matchedProperties;
        }
    }
}

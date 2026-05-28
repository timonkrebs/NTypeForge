using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NTypeForge.SourceGenerator.Models
{
    internal struct CandidateInvocation
    {
        public ExpressionSyntax Node;
        public string MethodName;
        public ITypeSymbol TargetType;
        public ITypeSymbol ArgumentType;
        public ITypeSymbol ExpectedInterfaceType;
        public int ArgumentIndex;
        public IMethodSymbol OriginalMethod;
        public bool IsStatic;

        public CandidateInvocation(
            ExpressionSyntax node,
            string methodName,
            ITypeSymbol targetType,
            ITypeSymbol argumentType,
            ITypeSymbol expectedInterfaceType,
            int argumentIndex,
            IMethodSymbol originalMethod,
            bool isStatic)
        {
            Node = node;
            MethodName = methodName;
            TargetType = targetType;
            ArgumentType = argumentType;
            ExpectedInterfaceType = expectedInterfaceType;
            ArgumentIndex = argumentIndex;
            OriginalMethod = originalMethod;
            IsStatic = isStatic;
        }
    }
}

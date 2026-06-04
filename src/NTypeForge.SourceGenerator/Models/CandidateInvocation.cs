using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NTypeForge.SourceGenerator.Models
{
    internal struct CandidateInvocation
    {
        public InvocationExpressionSyntax Invocation;
        public ITypeSymbol TargetType;
        public ITypeSymbol ArgumentType;
        public ITypeSymbol UnderlyingType;
        public ITypeSymbol ExpectedInterfaceType;
        public int ArgumentIndex;
        public IMethodSymbol OriginalMethod;
        public bool IsStatic;
        public bool IsDuckCall;

        public CandidateInvocation(
            InvocationExpressionSyntax invocation,
            ITypeSymbol targetType,
            ITypeSymbol argumentType,
            ITypeSymbol underlyingType,
            ITypeSymbol expectedInterfaceType,
            int argumentIndex,
            IMethodSymbol originalMethod,
            bool isStatic,
            bool isDuckCall)
        {
            Invocation = invocation;
            TargetType = targetType;
            ArgumentType = argumentType;
            UnderlyingType = underlyingType;
            ExpectedInterfaceType = expectedInterfaceType;
            ArgumentIndex = argumentIndex;
            OriginalMethod = originalMethod;
            IsStatic = isStatic;
            IsDuckCall = isDuckCall;
        }
    }
}

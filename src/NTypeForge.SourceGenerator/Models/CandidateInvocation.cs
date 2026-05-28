using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NTypeForge.SourceGenerator.Models
{
    internal struct CandidateInvocation
    {
        public InvocationExpressionSyntax Invocation;
        public string MethodName;
        public ITypeSymbol? TargetType;
        public ITypeSymbol ArgumentType;
        public ITypeSymbol UnderlyingType;
        public ITypeSymbol ExpectedInterfaceType;
        public int ArgumentIndex;
        public IMethodSymbol OriginalMethod;
        public bool IsStatic;
        public bool NeedsUnwrapping;
        public bool IsDuckCall;

        public CandidateInvocation(
            InvocationExpressionSyntax invocation,
            string methodName,
            ITypeSymbol? targetType,
            ITypeSymbol argumentType,
            ITypeSymbol underlyingType,
            ITypeSymbol expectedInterfaceType,
            int argumentIndex,
            IMethodSymbol originalMethod,
            bool isStatic,
            bool needsUnwrapping,
            bool isDuckCall)
        {
            Invocation = invocation;
            MethodName = methodName;
            TargetType = targetType;
            ArgumentType = argumentType;
            UnderlyingType = underlyingType;
            ExpectedInterfaceType = expectedInterfaceType;
            ArgumentIndex = argumentIndex;
            OriginalMethod = originalMethod;
            IsStatic = isStatic;
            NeedsUnwrapping = needsUnwrapping;
            IsDuckCall = isDuckCall;
        }
    }
}

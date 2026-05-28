using System;

namespace NTypeForge;

public static class DuckExtensions
{
    public static T? Unbox<T>(this object proxy)
    {
        object? current = proxy;
        while (current is IProxy untypedProxy)
        {
            if (untypedProxy.Unwrapped is T unwrapped)
            {
                return unwrapped;
            }
            current = untypedProxy.Unwrapped;
        }

        if (current is T alreadyT)
        {
            return alreadyT;
        }

        return default;
    }

    public static T Duck<T>(this object instance) where T : class
    {
        if (instance is T t) return t;

        throw new InvalidOperationException("NTypeForge: Duck<T> was called but no proxy was generated. Ensure the NTypeForge source generator is running and the target type matches the interface.");
    }
}

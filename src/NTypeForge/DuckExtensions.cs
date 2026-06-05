using System;

namespace NTypeForge;

public static class DuckExtensions
{
    public static T? Unbox<T>(this object? proxy)
        => proxy.TryUnbox<T>(out var value) ? value : default;

    // Walks the proxy chain and reports whether a T was found via an explicit
    // bool, rather than relying on an `is T` pattern. For value-type T a miss
    // would otherwise surface as default(T), which `is T` treats as a match.
    // The guard bounds pathological IProxy implementations that cycle.
    public static bool TryUnbox<T>(this object? proxy, out T value)
    {
        object? current = proxy;
        int guard = 0;
        while (current is IProxy untypedProxy && guard++ < 64)
        {
            if (untypedProxy.Unwrapped is T unwrapped)
            {
                value = unwrapped;
                return true;
            }
            current = untypedProxy.Unwrapped;
        }

        if (current is T alreadyT)
        {
            value = alreadyT;
            return true;
        }

        value = default!;
        return false;
    }

    public static T Duck<T>(this object instance) where T : class
    {
        if (instance is T t) return t;

        throw new InvalidOperationException("NTypeForge: Duck<T> was called but no proxy was generated. Ensure the NTypeForge source generator is running and the target type matches the interface.");
    }
}

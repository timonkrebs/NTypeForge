using System;

namespace NTypeForge;

/// <summary>
/// Runtime helpers for NTypeForge duck typing: recover the wrapped instance from a proxy
/// (<see cref="Unbox{T}"/> / <see cref="TryUnbox{T}"/>) and the <see cref="Duck{T}"/> entry point.
/// </summary>
public static class DuckExtensions
{
    /// <summary>
    /// Recovers the wrapped <typeparamref name="T"/> from a proxy, walking nested proxies. Returns
    /// <c>default</c> if <paramref name="proxy"/> neither is nor wraps a <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The type to recover.</typeparam>
    /// <param name="proxy">A proxy, a wrapped instance, or any object.</param>
    public static T? Unbox<T>(this object? proxy)
        => proxy.TryUnbox<T>(out var value) ? value : default;

    /// <summary>
    /// Tries to recover the wrapped <typeparamref name="T"/> from a proxy, walking nested proxies.
    /// </summary>
    /// <typeparam name="T">The type to recover.</typeparam>
    /// <param name="proxy">A proxy, a wrapped instance, or any object.</param>
    /// <param name="value">The recovered instance when this returns <c>true</c>.</param>
    /// <returns><c>true</c> if a <typeparamref name="T"/> was found.</returns>
    // Reports success via an explicit bool rather than relying on an `is T` pattern: for value-type
    // T a miss would otherwise surface as default(T), which `is T` treats as a match. The guard
    // bounds pathological IProxy implementations that cycle.
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

    /// <summary>
    /// Returns <paramref name="instance"/> viewed as <typeparamref name="T"/>. When the type does
    /// not nominally implement <typeparamref name="T"/>, the NTypeForge source generator replaces
    /// this call with one that returns a generated proxy. This fallback body runs only if the
    /// generator did not produce a proxy for the call.
    /// </summary>
    /// <typeparam name="T">The interface to present.</typeparam>
    /// <param name="instance">The instance to duck-type.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no proxy was generated and <paramref name="instance"/> is not already a
    /// <typeparamref name="T"/> (e.g. the generator is not running).
    /// </exception>
    public static T Duck<T>(this object instance) where T : class
    {
        if (instance is T t) return t;

        throw new InvalidOperationException("NTypeForge: Duck<T> was called but no proxy was generated. Ensure the NTypeForge source generator is running and the target type matches the interface.");
    }
}

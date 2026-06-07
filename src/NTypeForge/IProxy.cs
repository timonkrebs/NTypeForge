namespace NTypeForge;

/// <summary>
/// Implemented by every generated duck-typing proxy. Exposes the wrapped instance as
/// <see cref="object"/> so it can be recovered without knowing its static type (see the
/// <c>Unbox</c>/<c>TryUnbox</c> extensions on <see cref="DuckExtensions"/>).
/// </summary>
public interface IProxy
{
    /// <summary>The instance this proxy forwards to.</summary>
    object Unwrapped { get; }
}

/// <summary>
/// The strongly-typed view of a duck-typing proxy that wraps a <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T">The wrapped (underlying) type.</typeparam>
public interface IProxy<out T> : IProxy
{
    /// <summary>The wrapped instance, strongly typed.</summary>
    T Inner { get; }
}

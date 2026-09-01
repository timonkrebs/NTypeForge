using System;

namespace NTypeForge.Variadic.SourceGenerator
{
    // The resolved arity, carried through the incremental pipeline as pure primitives (no symbols,
    // no locations), so an edit that doesn't change the requested arity reuses the cached output.
    //   Effective    - the arity actually emitted, after clamping into [MinArity, MaxArity].
    //   Requested    - the value the user wrote (or the default), kept so Emit can diagnose a
    //                  clamp without re-reading the attribute.
    //   HasAttribute - whether an [assembly: VariadicArity] was present at all; a clamp is only
    //                  diagnosed for an explicit request, never for the silent default.
    internal readonly struct ArityConfig : IEquatable<ArityConfig>
    {
        public int Effective { get; }
        public int Requested { get; }
        public bool HasAttribute { get; }

        public ArityConfig(int effective, int requested, bool hasAttribute)
        {
            Effective = effective;
            Requested = requested;
            HasAttribute = hasAttribute;
        }

        public bool Equals(ArityConfig other)
            => Effective == other.Effective && Requested == other.Requested && HasAttribute == other.HasAttribute;

        public override bool Equals(object? obj) => obj is ArityConfig other && Equals(other);

        public override int GetHashCode()
            => (Effective * 397 ^ Requested) * 397 ^ (HasAttribute ? 1 : 0);
    }
}

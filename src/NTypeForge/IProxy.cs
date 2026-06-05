namespace NTypeForge;

public interface IProxy
{
    object Unwrapped { get; }
}

public interface IProxy<out T> : IProxy
{
    T Inner { get; }
}

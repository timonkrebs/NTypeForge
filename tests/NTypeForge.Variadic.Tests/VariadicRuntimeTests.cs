using NTypeForge.Variadic;

namespace NTypeForge.Variadic.Tests;

// End-to-end: the generator is wired as an analyzer on this project ([assembly: VariadicArity(12)]),
// so these tests exercise the *generated* delegates and combinators at run time - proving the
// lockstep iteration, deferred execution, and delegate shapes are correct, not merely that they
// compile.
public class VariadicRuntimeTests
{
    [Fact]
    public void Zip_ThreeSequences_CombinesElementwise()
    {
        int[] a = { 1, 2, 3 };
        int[] b = { 10, 20, 30 };
        int[] c = { 100, 200, 300 };

        var result = a.Zip(b, c, (x, y, z) => x + y + z).ToArray();

        Assert.Equal(new[] { 111, 222, 333 }, result);
    }

    [Fact]
    public void Zip_StopsAtShortestSequence()
    {
        int[] a = { 1, 2, 3, 4 };
        int[] b = { 10, 20 };
        int[] c = { 100, 200, 300 };

        var result = a.Zip(b, c, (x, y, z) => x + y + z).ToArray();

        Assert.Equal(new[] { 111, 222 }, result);
    }

    [Fact]
    public void Zip_FiveSequences_Works()
    {
        int[] s = { 1, 1, 1 };

        var result = s.Zip(s, s, s, s, (a, b, c, d, e) => a + b + c + d + e).ToArray();

        Assert.Equal(new[] { 5, 5, 5 }, result);
    }

    [Fact]
    public void Zip_IsDeferred_UntilEnumerated()
    {
        var started = false;
        IEnumerable<int> Source()
        {
            started = true;
            yield return 1;
        }

        var query = Source().Zip(new[] { 1 }, new[] { 1 }, (a, b, c) => a + b + c);
        Assert.False(started);   // building the query must not enumerate

        _ = query.ToArray();
        Assert.True(started);
    }

    [Fact]
    public void Zip_NullSource_ThrowsEagerly()
    {
        int[]? missing = null;

        // The eager null check fires on the call, not on enumeration.
        Assert.Throws<ArgumentNullException>(() => missing!.Zip(new[] { 1 }, new[] { 1 }, (a, b, c) => a));
    }

    [Fact]
    public void ForEach_TwoSequences_InvokesActionInLockstep()
    {
        int[] numbers = { 1, 2, 3 };
        string[] labels = { "a", "b", "c" };
        var seen = new List<string>();

        numbers.ForEach(labels, (n, label) => seen.Add($"{n}{label}"));

        Assert.Equal(new[] { "1a", "2b", "3c" }, seen);
    }

    [Fact]
    public void VariadicFunc_IsInvocable()
    {
        VariadicFunc<int, int, int, int> add3 = (a, b, c) => a + b + c;

        Assert.Equal(6, add3(1, 2, 3));
    }

    [Fact]
    public void VariadicAction_IsInvocable()
    {
        var total = 0;
        VariadicAction<int, int, int> accumulate = (a, b, c) => total = a + b + c;

        accumulate(4, 5, 6);

        Assert.Equal(15, total);
    }
}

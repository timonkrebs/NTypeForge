using Xunit;

namespace NTypeForge.Tests;

// Issue #11: several arguments of one call can need ducking at the same time. The generator
// emits a single forwarding extension that replaces every duck-typed parameter and wraps each
// argument in its own proxy - ducking one argument per call could never make such a call bind.

public interface ISummer { int Sum(int a, int b); }
public interface IStamper { string Stamp(string text); }

// Structurally identical to ISummer; used to hand an already-proxied value into a call that
// needs it re-ducked to ISummer.
public interface IOtherSummer { int Sum(int a, int b); }

public class PlainSummer { public int Sum(int a, int b) => a + b; }
public class PlainStamper { public string Stamp(string text) => $"[{text}]"; }

public class Pipeline
{
    public string Run(ISummer summer, IStamper stamper, int x, int y)
        => stamper.Stamp(summer.Sum(x, y).ToString());

    public static string RunStatic(ISummer summer, IStamper stamper)
        => stamper.Stamp(summer.Sum(1, 2).ToString());

    public int RunPair(ISummer first, ISummer second)
        => first.Sum(1, 2) + second.Sum(3, 4);

    public string RunThree(ISummer summer, string sep, IStamper stamper, IStamper again)
        => again.Stamp(stamper.Stamp(summer.Sum(2, 3).ToString()) + sep);
}

public class MultipleArgumentDuckingTests
{
    [Fact]
    public void DucksTwoArgumentsInOneCall()
    {
        var result = new Pipeline().Run(new PlainSummer(), new PlainStamper(), 10, 20);

        Assert.Equal("[30]", result);
    }

    [Fact]
    public void DucksTwoArgumentsInStaticCall()
    {
        var result = Pipeline.RunStatic(new PlainSummer(), new PlainStamper());

        Assert.Equal("[3]", result);
    }

    [Fact]
    public void DucksTwoArgumentsOfSameInterface()
    {
        var result = new Pipeline().RunPair(new PlainSummer(), new PlainSummer());

        Assert.Equal(10, result);
    }

    [Fact]
    public void DucksThreeArgumentsAroundPassthroughParameter()
    {
        var result = new Pipeline().RunThree(new PlainSummer(), "!", new PlainStamper(), new PlainStamper());

        Assert.Equal("[[5]!]", result);
    }

    [Fact]
    public void DucksTwoArgumentsWithNamedReorderedArguments()
    {
        var result = new Pipeline().Run(stamper: new PlainStamper(), summer: new PlainSummer(), x: 1, y: 2);

        Assert.Equal("[3]", result);
    }

    [Fact]
    public void DucksProxyArgumentAlongsideConcreteArgument()
    {
        // `viaOther` is a proxy with static type IOtherSummer; passing it where ISummer is
        // expected re-ducks it (unwrapping back to the PlainSummer rather than stacking proxies),
        // while the PlainStamper in the same call gets its own direct proxy.
        IOtherSummer viaOther = new PlainSummer().Duck<IOtherSummer>();

        var result = new Pipeline().Run(viaOther, new PlainStamper(), 4, 5);

        Assert.Equal("[9]", result);
    }
}

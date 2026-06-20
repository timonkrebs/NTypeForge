using System.Threading.Tasks;
using Xunit;

namespace NTypeForge.Tests;

// Task / Task<T> / ValueTask<T> are ordinary return types as far as a proxy is concerned: it
// forwards the call and hands back the awaitable untouched (the generator emits no async/await of
// its own). These tests pin that a structurally-matching type can be ducked to an async-returning
// interface and awaited through the proxy - both explicitly via Duck<T>() and via implicit
// argument ducking at a call site.
public interface IAsyncWorker
{
    Task RunAsync();
    Task<int> ComputeAsync(int seed);
    ValueTask<string> DescribeAsync(string name);
}

// Does not declare IAsyncWorker; matches it structurally.
public class PlainAsyncWorker
{
    public bool Ran { get; private set; }

    public Task RunAsync()
    {
        Ran = true;
        return Task.CompletedTask;
    }

    public Task<int> ComputeAsync(int seed) => Task.FromResult(seed * 2);

    public ValueTask<string> DescribeAsync(string name) => new ValueTask<string>($"worker:{name}");
}

public class AsyncConsumer
{
    public static async Task<int> RunThenComputeAsync(IAsyncWorker worker, int seed)
    {
        await worker.RunAsync();
        return await worker.ComputeAsync(seed);
    }
}

public class AsyncMemberDuckingTests
{
    [Fact]
    public async Task ForwardsTaskAndValueTaskMembersThroughProxy()
    {
        var original = new PlainAsyncWorker();
        IAsyncWorker worker = original.Duck<IAsyncWorker>();

        await worker.RunAsync();
        var computed = await worker.ComputeAsync(21);
        var described = await worker.DescribeAsync("a");

        Assert.True(original.Ran);            // non-generic Task member actually forwarded and ran
        Assert.Equal(42, computed);           // Task<int> result flows back through the proxy
        Assert.Equal("worker:a", described);  // ValueTask<string> result flows back too
    }

    [Fact]
    public async Task ImplicitlyDucksConcreteIntoAsyncReturningInterface()
    {
        // PlainAsyncWorker doesn't implement IAsyncWorker; the generator bridges it at the call site.
        var computed = await AsyncConsumer.RunThenComputeAsync(new PlainAsyncWorker(), 21);

        Assert.Equal(42, computed);
    }
}

using System.Diagnostics;
using System.Runtime.CompilerServices;

using LiveTranslator.Core.Providers;

namespace LiveTranslator.Core.Tests;

public class HedgedProviderTests
{
    [Fact]
    public async Task Fast_primary_never_starts_the_backup()
    {
        var primary = FakeProvider.Echo(name: "p");
        var backup = FakeProvider.Echo(name: "b");
        var hedged = new HedgedProvider(primary, backup, TimeSpan.FromMilliseconds(200));

        var result = string.Concat(await TestData.Collect(hedged.TranslateStreamAsync(TestData.Request("x"))));

        Assert.Equal("T(x )", result);
        await Task.Delay(300);
        Assert.Equal(0, backup.CallCount);
    }

    [Fact]
    public async Task Slow_primary_loses_to_backup_after_the_delay()
    {
        var primaryCancelled = new TaskCompletionSource();
        var primary = new FakeProvider((r, ct) => Slow(ct, primaryCancelled), "p");
        var backup = FakeProvider.Echo(firstTokenMs: 20, name: "b");
        var hedged = new HedgedProvider(primary, backup, TimeSpan.FromMilliseconds(50));

        var sw = Stopwatch.StartNew();
        var result = string.Concat(await TestData.Collect(hedged.TranslateStreamAsync(TestData.Request("x"))));

        Assert.Equal("T(x )", result);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
        await primaryCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)); // loser is cancelled, not leaked

        static async IAsyncEnumerable<string> Slow([EnumeratorCancellation] CancellationToken ct, TaskCompletionSource cancelled)
        {
            try
            {
                await Task.Delay(5000, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }
            yield return "slow";
        }
    }

    [Fact]
    public async Task Primary_failure_starts_backup_immediately()
    {
        var primary = new FakeProvider((_, _) => Fail("primary down"), "p");
        var backup = FakeProvider.Echo(name: "b");
        var hedged = new HedgedProvider(primary, backup, TimeSpan.FromSeconds(10));

        var sw = Stopwatch.StartNew();
        var result = string.Concat(await TestData.Collect(hedged.TranslateStreamAsync(TestData.Request("x"))));

        Assert.Equal("T(x )", result);
        Assert.True(sw.ElapsedMilliseconds < 2000);
    }

    [Fact]
    public async Task Both_failing_reports_the_primary_error()
    {
        var hedged = new HedgedProvider(
            new FakeProvider((_, _) => Fail("primary down")),
            new FakeProvider((_, _) => Fail("backup down")),
            TimeSpan.FromMilliseconds(10));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => TestData.Collect(hedged.TranslateStreamAsync(TestData.Request("x"))));
        Assert.Equal("primary down", ex.Message);
    }

    private static async IAsyncEnumerable<string> Fail(string message, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        throw new ProviderException(message);
#pragma warning disable CS0162 // iterator needs a yield to be an iterator
        yield break;
#pragma warning restore CS0162
    }
}

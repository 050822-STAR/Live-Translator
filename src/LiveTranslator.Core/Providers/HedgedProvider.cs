using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace LiveTranslator.Core.Providers;

/// <summary>
/// Hedged request: if the primary provider has not produced its first token within the delay
/// (or fails), the backup is started too, and whichever streams first wins; the loser is cancelled.
/// Cuts tail latency caused by a slow or overloaded endpoint without doubling normal cost.
/// </summary>
public sealed class HedgedProvider : ITranslationProvider
{
    private readonly ITranslationProvider _primary;
    private readonly ITranslationProvider _backup;
    private readonly TimeSpan _delay;

    public HedgedProvider(ITranslationProvider primary, ITranslationProvider backup, TimeSpan delay)
    {
        _primary = primary;
        _backup = backup;
        _delay = delay;
    }

    public string Name => $"{_primary.Name} ⇄ {_backup.Name}";

    public async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
        var sources = new[] { CancellationTokenSource.CreateLinkedTokenSource(ct), CancellationTokenSource.CreateLinkedTokenSource(ct) };
        var pumps = new List<Task> { Task.Run(() => PumpAsync(_primary, 0, request, channel.Writer, sources[0].Token)) };
        var errors = new Exception?[2];
        var backupStarted = false;
        var winner = -1;
        var delayTask = Task.Delay(_delay, ct);

        try
        {
            while (true)
            {
                var readTask = channel.Reader.ReadAsync(ct).AsTask();
                if (!backupStarted && winner < 0 &&
                    await Task.WhenAny(readTask, delayTask).ConfigureAwait(false) == delayTask &&
                    !readTask.IsCompleted && !ct.IsCancellationRequested)
                {
                    StartBackup();
                }
                var item = await readTask.ConfigureAwait(false);

                switch (item.Kind)
                {
                    case ItemKind.Chunk:
                        if (winner < 0)
                        {
                            winner = item.Source;
                            sources[1 - winner].Cancel();
                        }
                        if (item.Source == winner)
                            yield return item.Text!;
                        break;

                    case ItemKind.Done:
                        if (winner < 0)
                        {
                            winner = item.Source; // finished with empty output: still a valid answer
                            sources[1 - winner].Cancel();
                        }
                        if (item.Source == winner)
                            yield break;
                        break;

                    case ItemKind.Error:
                        errors[item.Source] = item.Error;
                        if (item.Source == winner)
                            ExceptionDispatchInfo.Throw(item.Error!);
                        if (winner < 0)
                        {
                            if (!backupStarted)
                                StartBackup();
                            else if (errors[0] is not null && errors[1] is not null)
                                ExceptionDispatchInfo.Throw(errors[0]!);
                        }
                        break;
                }
            }
        }
        finally
        {
            sources[0].Cancel();
            sources[1].Cancel();
            // Dispose only after the pumps observed cancellation, so they never touch a disposed source.
            _ = Task.WhenAll(pumps).ContinueWith(_ =>
            {
                sources[0].Dispose();
                sources[1].Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        void StartBackup()
        {
            backupStarted = true;
            pumps.Add(Task.Run(() => PumpAsync(_backup, 1, request, channel.Writer, sources[1].Token)));
        }
    }

    private static async Task PumpAsync(
        ITranslationProvider provider, int source, TranslationRequest request, ChannelWriter<Item> writer, CancellationToken ct)
    {
        try
        {
            await foreach (var chunk in provider.TranslateStreamAsync(request, ct).ConfigureAwait(false))
                writer.TryWrite(new Item(source, ItemKind.Chunk, chunk, null));
            writer.TryWrite(new Item(source, ItemKind.Done, null, null));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            writer.TryWrite(new Item(source, ItemKind.Error, null, ex));
        }
    }

    public Task WarmUpAsync(CancellationToken ct = default) =>
        Task.WhenAll(_primary.WarmUpAsync(ct), _backup.WarmUpAsync(ct));

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) => _primary.ListModelsAsync(ct);

    private enum ItemKind { Chunk, Done, Error }

    private sealed record Item(int Source, ItemKind Kind, string? Text, Exception? Error);
}

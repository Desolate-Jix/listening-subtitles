namespace ListeningSubtitles.Core;

public sealed record TranscriptTranslationUpdate(string Id, string OriginalText, string Translation, string Status);

/// <summary>
/// Only an explicit enqueue can request a translation. Retains at most eight pending items
/// and one active item; completion/error IDs are released so a user can manually retry.
/// Callbacks run on background threads and should return promptly.
/// </summary>
public sealed class TranscriptTranslationQueue : IAsyncDisposable
{
    public const int Capacity = 8;
    private readonly Func<string, CancellationToken, Task<string>> _translate;
    private readonly Action<TranscriptTranslationUpdate> _callback;
    private readonly object _gate = new();
    private readonly Queue<Item> _pending = new();
    private readonly HashSet<string> _inProgress = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private Task? _stopTask;
    private bool _stopped, _disposed;

    public TranscriptTranslationQueue(Func<string, CancellationToken, Task<string>> translate, Action<TranscriptTranslationUpdate> callback)
    {
        _translate = translate ?? throw new ArgumentNullException(nameof(translate));
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _worker = Task.Run(WorkAsync);
    }

    public static bool NeedsTranslation(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var ascii = 0;
        var cjk = 0;
        foreach (var c in text)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') ascii++;
            else if (c is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff') cjk++;
        }
        return ascii >= 3 && ascii >= (long)cjk * 2;
    }

    public bool TryEnqueue(string id, string text)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(text) || text.Length > 5000 || !NeedsTranslation(text)) return false;
        lock (_gate)
        {
            if (_stopped || _pending.Count >= Capacity || !_inProgress.Add(id)) return false;
            _pending.Enqueue(new(id, text));
            _available.Release();
            return true;
        }
    }

    private async Task WorkAsync()
    {
        var token = _lifetime.Token;
        try
        {
            while (true)
            {
                await _available.WaitAsync(token).ConfigureAwait(false);
                Item? item;
                lock (_gate) item = _pending.TryDequeue(out var next) ? next : null;
                if (item is null) continue;
                var status = "error";
                var translation = "";
                try
                {
                    token.ThrowIfCancellationRequested();
                    Notify(item, "", "translating");
                    token.ThrowIfCancellationRequested();
                    // WaitAsync makes stop bounded even when an injected delegate ignores cancellation.
                    // Its already-started external work may still finish remotely; its late result is discarded.
                    var request = _translate(item.Text, token);
                    var result = await request.WaitAsync(token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(result) && result.Length <= 16000)
                    {
                        translation = result.Trim();
                        status = "completed";
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { status = "cancelled"; }
                catch (Exception) { /* Never expose delegate/server exception details or retry automatically. */ }
                lock (_gate)
                {
                    if (_stopped) { status = "cancelled"; translation = ""; }
                    _inProgress.Remove(item.Id);
                }
                Notify(item, translation, status);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void Notify(Item item, string translation, string status)
    {
        try { _callback(new(item.Id, item.Text, translation, status)); }
        catch (Exception) { /* A UI callback cannot interrupt queue cleanup or another item. */ }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopTask is not null) return _stopTask;
            _stopped = true;
            try { _lifetime.Cancel(); } catch (AggregateException) { }
            var dropped = _pending.ToArray();
            _pending.Clear();
            foreach (var item in dropped) _inProgress.Remove(item.Id);
            _stopTask = Task.Run(async () =>
            {
                foreach (var item in dropped) Notify(item, "", "cancelled");
                await _worker.ConfigureAwait(false);
            });
            return _stopTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _available.Dispose();
            _lifetime.Dispose();
        }
    }

    private sealed record Item(string Id, string Text);
}

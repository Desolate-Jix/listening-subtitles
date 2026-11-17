using ListeningSubtitles.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ListeningSubtitles.Audio;

namespace ListeningSubtitles.Speech;

public sealed record TranscriptUpdate(AudioSourceKind Source, string ItemId, string Text, bool IsFinal, DateTimeOffset Timestamp)
{
    public long Sequence { get; init; }
    public DateTimeOffset? EndTime { get; init; }
}

/// <summary>
/// Single-use transcription connection. Events run on background threads. TryAppend copies
/// PCM24k mono16; callers may clear their source immediately. Stop discards queued audio.
/// </summary>
public sealed class RealtimeTranscriber : ListeningSubtitles.Core.ITranscriber
{
    private static readonly Uri Endpoint = new("wss://api.openai.com/v1/realtime?intent=transcription");
    private const int MaximumChunkBytes = 200 * 48;
    private const int MaximumQueuedBytes = 3000 * 48;
    private const int MaximumQueuedChunks = 150;
    private const int MaximumMessageBytes = 1024 * 1024;
    private const int MaximumTextLength = 64000;
    private const int MaximumUnboundItems = 64;
    private const int MaximumUnboundCharacters = 128000;
    private readonly AudioSourceKind _source;
    private readonly string _model;
    private readonly string[] _languages;
    private readonly IRealtimeTransport _transport;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _readinessTimeout;
    private readonly object _sync = new(), _events = new();
    private readonly Queue<AudioChunk> _queue = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly ClientVoiceGate _gate = new();
    private readonly TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _updated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly LinkedList<Turn> _turns = new();
    private readonly Dictionary<string, Turn> _items = new(StringComparer.Ordinal);
    private readonly Queue<Turn> _awaitingAcknowledgement = new();
    private readonly HashSet<string> _finalIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _finalHistory = new();
    private readonly Dictionary<string, BufferedTranscript> _unboundTranscripts = new(StringComparer.Ordinal);
    private CancellationTokenSource? _lifetime;
    private Task? _receiveTask, _sendTask, _stopTask;
    private Turn? _currentTurn;
    private TaskCompletionSource? _clearAcknowledgement;
    private int _queuedBytes, _unboundCharacters;
    private long _sequence, _origin, _audioGeneration, _processedGeneration;
    private DateTimeOffset _originUtc;
    private bool _requireAcknowledgedIds;
    private bool _startedOnce, _accepting, _stopping, _disposed, _sessionUpdateSent;
    private string? _failure;

    public RealtimeTranscriber(AudioSourceKind source, string model = "gpt-live-transcribe", string language = "")
        : this(source, model, language, null, null, null, createDefaultTransport: true) { }

    public RealtimeTranscriber(AudioSourceKind source, string model, string language, IRealtimeTransport transport,
        TimeProvider? timeProvider = null, TimeSpan? readinessTimeout = null)
        : this(source, model, language, transport, timeProvider, readinessTimeout, createDefaultTransport: false) { }

    private RealtimeTranscriber(AudioSourceKind source, string model, string language, IRealtimeTransport? transport,
        TimeProvider? timeProvider, TimeSpan? readinessTimeout, bool createDefaultTransport)
    {
        if (!Enum.IsDefined(source)) throw new ArgumentException(AppText.T("音频来源无效。", "Invalid audio source."), nameof(source));
        if (model is not ("gpt-live-transcribe" or "gpt-transcribe"))
            throw new ArgumentException(AppText.T("实时转写目前支持 gpt-live-transcribe 或 gpt-transcribe。", "Realtime transcription supports gpt-live-transcribe or gpt-transcribe."), nameof(model));
        ArgumentNullException.ThrowIfNull(language);
        _languages = string.IsNullOrWhiteSpace(language) ? [] : language.Split(',').Select(x => x.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (_languages.Length > 8 || _languages.Any(x => !Regex.IsMatch(x, @"\A[a-z]{2,3}(-[a-z0-9]{2,4})?\z", RegexOptions.CultureInvariant)))
            throw new ArgumentException(AppText.T("转写语言须为逗号分隔的语言代码，例如 zh,en；留空可自动识别。", "Use comma-separated speech language codes, such as zh,en; leave blank for automatic detection."), nameof(language));
        _source = source;
        _model = model;
        _clock = timeProvider ?? TimeProvider.System;
        _readinessTimeout = readinessTimeout ?? TimeSpan.FromSeconds(10);
        if (_readinessTimeout <= TimeSpan.Zero || _readinessTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(readinessTimeout));
        _transport = createDefaultTransport ? new OpenAiWebSocketTransport() : transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public event Action<TranscriptUpdate>? Transcript;
    public event Action<string>? Failed;

    public async Task StartAsync(string apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 8192 || apiKey.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException(AppText.T("API 密钥无效，不能包含空格或换行。", "Invalid API key: spaces and line breaks are not allowed."), nameof(apiKey));
        CancellationToken token;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startedOnce || _stopping) throw new InvalidOperationException(AppText.T("此转写实例已使用，请创建新实例后启动。", "This transcription connection has already been used. Start a new connection."));
            _startedOnce = true;
            _origin = _clock.GetTimestamp();
            _originUtc = _clock.GetUtcNow();
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            token = _lifetime.Token;
        }
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        startup.CancelAfter(_readinessTimeout);
        try
        {
            await _transport.ConnectAsync(Endpoint, apiKey, startup.Token).ConfigureAwait(false);
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                _receiveTask = Task.Run(() => ReceiveLoopAsync(token));
            }
            await _created.Task.WaitAsync(startup.Token).ConfigureAwait(false);
            var transcription = new Dictionary<string, object> { ["model"] = _model };
            if (_languages.Length > 0) transcription["languages"] = _languages;
            if (_model == "gpt-live-transcribe") transcription["delay"] = "low";
            var update = new
            {
                type = "session.update",
                session = new
                {
                    type = "transcription",
                    audio = new { input = new { format = new { type = "audio/pcm", rate = 24000 }, transcription, turn_detection = (object?)null } }
                }
            };
            lock (_sync) _sessionUpdateSent = true;
            await SendJsonAsync(update, startup.Token).ConfigureAwait(false);
            await _updated.Task.WaitAsync(startup.Token).ConfigureAwait(false);
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                _accepting = true;
                _sendTask = Task.Run(() => SendLoopAsync(token));
            }
        }
        catch (Exception)
        {
            bool stopped;
            string? failure;
            lock (_sync) { stopped = _stopping && _failure is null; failure = _failure; }
            var cancelled = cancellationToken.IsCancellationRequested;
            if (!stopped && !cancelled && failure is null)
            {
                failure = startup.IsCancellationRequested
                    ? AppText.T("实时转写初始化超时，尚未开始上传音频。请检查网络或模型权限后重新启动。", "Transcription setup timed out before audio upload. Check your connection and model access, then start again.")
                    : AppText.T("无法建立 OpenAI 实时转写连接。请检查网络、API 密钥和模型权限后重新启动。", "Unable to connect to OpenAI transcription. Check your network, API key and model access, then start again.");
                ReportFailure(failure);
            }
            await StopAsync().ConfigureAwait(false);
            if (cancelled || stopped) throw new OperationCanceledException(AppText.T("实时转写启动已取消。", "Transcription startup cancelled."), cancellationToken);
            throw new InvalidOperationException(failure ?? AppText.T("实时转写启动失败。", "Transcription startup failed."));
        }
    }

    public bool TryAppend(byte[] pcm)
    {
        string? error = null;
        lock (_sync)
        {
            if (!_accepting || _stopping || _lifetime?.IsCancellationRequested != false) return false;
            if (pcm is null || pcm.Length == 0 || pcm.Length % 2 != 0 || pcm.Length > MaximumChunkBytes)
                error = AppText.T("音频格式无效：需要 24kHz 单声道 PCM16，每个音频块最长 200 毫秒。转写已停止。", "Transcription stopped: expected 24 kHz mono PCM16 audio, with chunks no longer than 200 ms.");
            else if (_queuedBytes + pcm.Length > MaximumQueuedBytes || _queue.Count >= MaximumQueuedChunks)
                error = AppText.T("音频发送积压超过约 3 秒，转写已停止，未重放积压音频。请检查网络后重新启动。", "Transcription stopped after over 3 seconds of queued audio. Buffered audio was not replayed. Check your network and start again.");
            else
            {
                _queue.Enqueue(new(pcm.ToArray(), _clock.GetTimestamp(), _audioGeneration));
                _queuedBytes += pcm.Length;
                _available.Release();
                return true;
            }
        }
        ReportFailure(error);
        return false;
    }

    /// <summary>
    /// Discards this source's queued PCM and uncommitted turn. Call synchronously before
    /// accepting the replacement device's first chunk. The sole sender clears the remote
    /// input buffer after any already in-flight write and before new-generation audio.
    /// Committed turns and the connection remain intact; no old audio is replayed.
    /// </summary>
    public bool ResetAudioBoundary()
    {
        lock (_sync)
        {
            if (!_accepting || _stopping || _lifetime?.IsCancellationRequested != false) return false;
            ++_audioGeneration;
            DrainQueue();
            lock (_events)
            {
                _requireAcknowledgedIds = true;
                if (_currentTurn is { } abandoned)
                {
                    _turns.Remove(abandoned);
                    if (abandoned.Id is { } id)
                    {
                        _items.Remove(id);
                        RememberFinishedId(id);
                    }
                    abandoned.Text.Clear();
                    _currentTurn = null;
                }
            }
            _available.Release();
            return true;
        }
    }

    private async Task SendLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _available.WaitAsync(TimeSpan.FromMilliseconds(50), token).ConfigureAwait(false);
                long generation;
                lock (_sync) generation = _audioGeneration;
                if (generation != _processedGeneration)
                {
                    await ClearAudioBoundaryAsync(token).ConfigureAwait(false);
                    _processedGeneration = generation;
                    continue;
                }
                AudioChunk? chunk;
                lock (_sync) chunk = _queue.TryDequeue(out var value) ? value : null;
                if (chunk is not null)
                {
                    try
                    {
                        lock (_sync)
                        {
                            if (chunk.Generation != _audioGeneration) continue;
                            if (_clock.GetElapsedTime(chunk.Timestamp, _clock.GetTimestamp()) > TimeSpan.FromSeconds(3))
                                throw new StaleAudioException();
                        }
                        await SendActionsAsync(_gate.Push(chunk.Pcm, _clock.GetElapsedTime(_origin, chunk.Timestamp)), chunk.Generation, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(chunk.Pcm);
                        lock (_sync) _queuedBytes -= chunk.Pcm.Length;
                    }
                }
                bool idle;
                lock (_sync) idle = _queue.Count == 0 && _processedGeneration == _audioGeneration;
                if (idle) await SendActionsAsync(_gate.Tick(Elapsed()), generation, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (StaleAudioException) { ReportFailure(AppText.T("音频发送积压超过约 3 秒，转写已停止，未重放过期音频。请检查网络后重新启动。", "Transcription stopped because queued audio was over 3 seconds old. Stale audio was not replayed. Check your network and start again.")); }
        catch (Exception) { if (!token.IsCancellationRequested) ReportFailure(AppText.T("实时音频发送中断或超时，转写已停止。请检查网络后重新启动。", "Audio upload was interrupted or timed out. Transcription stopped; check your network and start again.")); }
        finally
        {
            _gate.Dispose();
            lock (_sync) { _accepting = false; DrainQueue(); }
        }
    }

    private async Task ClearAudioBoundaryAsync(CancellationToken token)
    {
        _gate.Reset();
        var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync) _clearAcknowledgement = acknowledgement;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await SendJsonAsync(new { type = "input_audio_buffer.clear" }, timeout.Token).ConfigureAwait(false);
            await acknowledgement.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally { lock (_sync) if (ReferenceEquals(_clearAcknowledgement, acknowledgement)) _clearAcknowledgement = null; }
    }

    private async Task SendActionsAsync(List<VoiceAction> actions, long generation, CancellationToken token)
    {
        try
        {
            foreach (var action in actions)
            {
                token.ThrowIfCancellationRequested();
                using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                sendTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                var bytes = action.Kind == VoiceActionKind.Append
                    ? JsonSerializer.SerializeToUtf8Bytes(new { type = "input_audio_buffer.append", audio = Convert.ToBase64String(action.Pcm!) })
                    : JsonSerializer.SerializeToUtf8Bytes(new { type = "input_audio_buffer.commit" });
                try
                {
                    ValueTask sending;
                    // Starting a transport write and checking its generation are atomic with
                    // ResetAudioBoundary. Once started, that one write may finish, then clear.
                    lock (_sync)
                    {
                        token.ThrowIfCancellationRequested();
                        if (generation != _audioGeneration || !_accepting || _stopping) return;
                        lock (_events)
                        {
                            if (action.Kind == VoiceActionKind.Append)
                            {
                                if (_currentTurn is null)
                                {
                                    if (_turns.Count >= 64) throw new InvalidDataException();
                                    _currentTurn = new Turn(++_sequence, _originUtc + action.Start);
                                    _turns.AddLast(_currentTurn);
                                }
                                _currentTurn.EndTime = _originUtc + action.End;
                            }
                            else
                            {
                                if (_currentTurn is null) throw new InvalidDataException();
                                _currentTurn.InputCommitted = true;
                                _awaitingAcknowledgement.Enqueue(_currentTurn);
                                _currentTurn = null;
                            }
                        }
                        sending = _transport.SendAsync(bytes, sendTimeout.Token);
                    }
                    await sending.ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        }
        finally { foreach (var action in actions) if (action.Pcm is { } bytes) CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task SendJsonAsync(object value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        try { await _transport.SendAsync(bytes, token).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var frame = new byte[8192];
        using var message = new MemoryStream();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var received = await _transport.ReceiveAsync(frame, token).ConfigureAwait(false);
                if (received.IsClose) throw new InvalidDataException();
                if (!received.IsText || received.Count < 0 || received.Count > frame.Length || message.Length + received.Count > MaximumMessageBytes)
                    throw new InvalidDataException();
                message.Write(frame, 0, received.Count);
                CryptographicOperations.ZeroMemory(frame.AsSpan(0, received.Count));
                if (!received.EndOfMessage) continue;
                using (var json = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length)))
                    HandleEvent(json.RootElement);
                CryptographicOperations.ZeroMemory(message.GetBuffer().AsSpan(0, (int)message.Length));
                message.SetLength(0);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (!token.IsCancellationRequested) ReportFailure(AppText.T("OpenAI 实时转写连接已中断，或返回了无效数据。转写已停止，请重新启动。", "The OpenAI transcription connection closed or returned invalid data. Transcription stopped; start again.")); }
        finally
        {
            CryptographicOperations.ZeroMemory(frame);
            CryptographicOperations.ZeroMemory(message.GetBuffer());
        }
    }

    private void HandleEvent(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var type = ReadString(root, "type");
        if (type == "session.created")
        {
            if (!root.TryGetProperty("session", out var session) || ReadString(session, "type") != "transcription") throw new InvalidDataException();
            _created.TrySetResult();
            return;
        }
        if (type == "session.updated")
        {
            bool expected;
            lock (_sync) expected = _sessionUpdateSent;
            if (expected)
            {
                ValidateUpdatedSession(root);
                _updated.TrySetResult();
            }
            return;
        }
        if (type == "input_audio_buffer.cleared")
        {
            lock (_sync) _clearAcknowledgement?.TrySetResult();
            return;
        }
        if (type is "error" or "conversation.item.input_audio_transcription.failed")
        {
            ReportFailure(AppText.T("OpenAI 实时转写返回错误，转写已停止。请检查 API 额度、模型权限和网络后重新启动。", "OpenAI transcription returned an error. Check your API credit, model access and network, then start again."));
            return;
        }
        if (type is not ("input_audio_buffer.committed" or "conversation.item.input_audio_transcription.delta" or "conversation.item.input_audio_transcription.completed")) return;
        var id = ReadString(root, "item_id");
        if (id is null || id.Length > 256 || !id.StartsWith("item_", StringComparison.Ordinal) || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new InvalidDataException();
        var updates = new List<TranscriptUpdate>();
        lock (_events)
        {
            if (_finalIds.Contains(id)) return;
            if (type == "input_audio_buffer.committed")
            {
                if (!_awaitingAcknowledgement.TryDequeue(out var turn)) throw new InvalidDataException();
                AssignId(turn, id);
                turn.Acknowledged = true;
                turn.PreviousItemId = ReadString(root, "previous_item_id");
                if (_unboundTranscripts.Remove(id, out var pending))
                {
                    _unboundCharacters -= pending.Text.Length;
                    turn.Text.Clear(); turn.Text.Append(pending.Text);
                    turn.FinalSeen = pending.FinalSeen;
                    if (!turn.FinalSeen && turn.Text.Length > 0) updates.Add(Update(turn, turn.Text.ToString(), false));
                    pending.Text.Clear();
                }
            }
            else
            {
                if (!_items.TryGetValue(id, out var turn))
                {
                    // After a source change, a late event may belong to the discarded half.
                    // Never guess it onto a fresh turn: only a commit acknowledgement binds IDs.
                    if (_requireAcknowledgedIds) { BufferUnboundTranscript(root, type!, id); return; }
                    turn = _turns.FirstOrDefault(x => x.Id is null);
                    if (turn is null) return;
                    AssignId(turn, id);
                }
                if (turn.FinalSeen) return;
                if (type.EndsWith(".delta", StringComparison.Ordinal))
                {
                    var delta = ReadString(root, "delta");
                    if (delta is null || turn.Text.Length + delta.Length > MaximumTextLength) throw new InvalidDataException();
                    turn.Text.Append(delta);
                    updates.Add(Update(turn, turn.Text.ToString(), false));
                }
                else
                {
                    var text = ReadString(root, "transcript");
                    if (text is null || text.Length > MaximumTextLength) throw new InvalidDataException();
                    turn.Text.Clear();
                    turn.Text.Append(text);
                    turn.FinalSeen = true;
                }
            }
            while (_turns.First is { Value: { FinalSeen: true, InputCommitted: true, Acknowledged: true } complete })
            {
                updates.Add(Update(complete, complete.Text.ToString(), true));
                _turns.RemoveFirst();
                _items.Remove(complete.Id!);
                RememberFinishedId(complete.Id!);
                complete.Text.Clear();
            }
        }
        foreach (var update in updates)
        {
            lock (_sync) if (_stopping || _failure is not null || _lifetime?.IsCancellationRequested != false) return;
            Transcript?.Invoke(update);
        }
    }

    private void BufferUnboundTranscript(JsonElement root, string type, string id)
    {
        var final = type.EndsWith(".completed", StringComparison.Ordinal);
        var text = ReadString(root, final ? "transcript" : "delta");
        if (text is null || text.Length > MaximumTextLength) throw new InvalidDataException();
        if (!_unboundTranscripts.TryGetValue(id, out var pending))
        {
            if (_unboundTranscripts.Count >= MaximumUnboundItems) throw new InvalidDataException();
            pending = new BufferedTranscript();
        }
        if (pending.FinalSeen) return;
        var newLength = final ? text.Length : pending.Text.Length + text.Length;
        var total = _unboundCharacters - pending.Text.Length + newLength;
        if (newLength > MaximumTextLength || total > MaximumUnboundCharacters) throw new InvalidDataException();
        _unboundTranscripts[id] = pending;
        if (final) pending.Text.Clear();
        pending.Text.Append(text); pending.FinalSeen = final;
        _unboundCharacters = total;
    }

    private void RememberFinishedId(string id)
    {
        if (!_finalIds.Add(id)) return;
        _finalHistory.Enqueue(id);
        if (_finalHistory.Count > 2048) _finalIds.Remove(_finalHistory.Dequeue());
    }

    private void ValidateUpdatedSession(JsonElement root)
    {
        if (!root.TryGetProperty("session", out var session) || ReadString(session, "type") != "transcription" ||
            !session.TryGetProperty("audio", out var audio) || audio.ValueKind != JsonValueKind.Object ||
            !audio.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object ||
            !input.TryGetProperty("format", out var format) || ReadString(format, "type") != "audio/pcm" ||
            !format.TryGetProperty("rate", out var rate) || !rate.TryGetInt32(out var sampleRate) || sampleRate != 24000 ||
            !input.TryGetProperty("transcription", out var transcription) || ReadString(transcription, "model") != _model ||
            (input.TryGetProperty("turn_detection", out var detection) && detection.ValueKind != JsonValueKind.Null))
            throw new InvalidDataException();
    }

    private void AssignId(Turn turn, string id)
    {
        if (turn.Id is not null && turn.Id != id) throw new InvalidDataException();
        if (_items.TryGetValue(id, out var existing) && !ReferenceEquals(existing, turn)) throw new InvalidDataException();
        turn.Id = id;
        _items[id] = turn;
    }

    private TranscriptUpdate Update(Turn turn, string text, bool final) => new(_source, turn.Id!, text, final, turn.Timestamp)
        { Sequence = turn.Sequence, EndTime = turn.EndTime };
    private TimeSpan Elapsed() => _clock.GetElapsedTime(_origin, _clock.GetTimestamp());
    private static string? ReadString(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var found) && found.ValueKind == JsonValueKind.String ? found.GetString() : null;

    private void ReportFailure(string message)
    {
        lock (_sync)
        {
            if (_failure is not null || _stopping) return;
            _failure = message;
            _accepting = false;
            _lifetime?.Cancel();
            _created.TrySetCanceled();
            _updated.TrySetCanceled();
            DrainQueue();
        }
        try { _transport.Abort(); } catch (Exception) { }
        try { Failed?.Invoke(message); } catch (Exception) { }
    }

    public Task StopAsync()
    {
        lock (_sync)
        {
            if (_stopTask is not null) return _stopTask;
            _stopping = true;
            _accepting = false;
            _lifetime?.Cancel();
            _created.TrySetCanceled();
            _updated.TrySetCanceled();
            DrainQueue();
            try { _transport.Abort(); } catch (Exception) { }
            _stopTask = FinishStopAsync(_sendTask, _receiveTask);
            return _stopTask;
        }
    }

    private async Task FinishStopAsync(Task? sender, Task? receiver)
    {
        if (sender is not null) await sender.ConfigureAwait(false);
        if (receiver is not null) await receiver.ConfigureAwait(false);
        _gate.Dispose();
        lock (_events)
        {
            foreach (var turn in _turns) turn.Text.Clear();
            _turns.Clear(); _items.Clear(); _awaitingAcknowledgement.Clear(); _finalIds.Clear(); _finalHistory.Clear(); _currentTurn = null;
            foreach (var pending in _unboundTranscripts.Values) pending.Text.Clear();
            _unboundTranscripts.Clear(); _unboundCharacters = 0;
        }
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private void DrainQueue()
    {
        while (_queue.TryDequeue(out var chunk))
        {
            _queuedBytes -= chunk.Pcm.Length;
            CryptographicOperations.ZeroMemory(chunk.Pcm);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime?.Dispose();
            _available.Dispose();
        }
    }

    private sealed record AudioChunk(byte[] Pcm, long Timestamp, long Generation);
    private sealed class StaleAudioException : Exception;
    private sealed class BufferedTranscript
    {
        public StringBuilder Text { get; } = new();
        public bool FinalSeen;
    }
    private sealed class Turn(long sequence, DateTimeOffset timestamp)
    {
        public long Sequence { get; } = sequence;
        public DateTimeOffset Timestamp { get; } = timestamp;
        public DateTimeOffset? EndTime;
        public string? Id, PreviousItemId;
        public StringBuilder Text { get; } = new();
        public bool InputCommitted, Acknowledged, FinalSeen;
    }
}

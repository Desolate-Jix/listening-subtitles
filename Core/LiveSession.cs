using ListeningSubtitles.Audio;
using ListeningSubtitles.Speech;

namespace ListeningSubtitles.Core;

public interface IAudioInput : IDisposable
{
    event Action<PcmChunk>? ChunkAvailable;
    event Action<string>? Failed;
    event Action<AudioSourceKind>? EndpointChanging;
    event Action<AudioSourceKind>? EndpointChanged;
    void Start(string? outputDeviceId = null);
    void Stop();
}
public interface ITranscriber : IAsyncDisposable
{
    event Action<TranscriptUpdate>? Transcript;
    event Action<string>? Failed;
    Task StartAsync(string apiKey, CancellationToken token);
    bool TryAppend(byte[] pcm);
    bool ResetAudioBoundary();
}

public sealed class LiveSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<IAudioInput> _capture;
    private readonly Func<ListeningSettings, ITranscriber> _transcriber;
    private readonly Func<string, string, string, string, string, CancellationToken, Task<string>> _translate;
    private Run? _current;
    private long _generation;
    private Task _stopping = Task.CompletedTask;
    public bool Active { get { lock (_gate) return _current is not null; } }
    public event Action<Caption>? CaptionChanged;
    public event Action<TranscriptTranslationUpdate>? TranslationChanged;
    public event Action<string>? StateChanged;
    public event Action<float>? LevelChanged;
    public event Action<DateTimeOffset>? CaptureStarted;

    public LiveSession(Func<string, string, string, string, string, CancellationToken, Task<string>> translate,
        Func<IAudioInput>? capture = null, Func<ListeningSettings, ITranscriber>? transcriber = null)
    {
        _translate = translate;
        _capture = capture ?? (() => new SystemAudioCapture());
        _transcriber = transcriber ?? (s => new RealtimeTranscriber(AudioSourceKind.System, s.TranscriptionModel, s.Languages));
    }
    public Task StartAsync(ListeningSettings settings, string key)
    {
        settings.Validate(); SecretStore.ValidateKey(key);
        lock (_gate)
        {
            if (_current is not null || !_stopping.IsCompleted) throw new InvalidOperationException(AppText.T("请等待当前字幕连接停止后再开始。", "Wait for the current subtitle connection to stop before starting again."));
            var audio = _capture();
            ITranscriber speech;
            try { speech = _transcriber(settings); } catch { audio.Dispose(); throw; }
            var run = new Run(++_generation, audio, speech, settings.Languages, settings.TargetLanguage);
            _current = run;
            if (settings.AutoTranslate)
                run.Translations = new TranscriptTranslationQueue((text, token) => _translate(key, settings.TranslationModel, text, settings.Languages, settings.TargetLanguage, token),
                    update => { lock (_gate) if (_current == run) TranslationChanged?.Invoke(update); }, settings.TargetLanguage);
            speech.Transcript += update => OnTranscript(run, update);
            speech.Failed += message => Fail(run, message);
            audio.Failed += message => Fail(run, message);
            audio.ChunkAvailable += chunk =>
            {
                try
                {
                    lock (_gate) if (_current == run && !run.Lifetime.IsCancellationRequested)
                    {
                        speech.TryAppend(chunk.Data);
                        LevelChanged?.Invoke(chunk.Level);
                    }
                }
                finally { Array.Clear(chunk.Data); }
            };
            audio.EndpointChanging += _ =>
            {
                lock (_gate) if (_current == run)
                {
                    speech.ResetAudioBoundary();
                    StateChanged?.Invoke(AppText.T("正在切换到新的默认输出设备…", "Switching to the new default output device…"));
                }
            };
            audio.EndpointChanged += _ => { lock (_gate) if (_current == run) StateChanged?.Invoke(AppText.T("正在转写 · 已切换到新的默认输出", "Transcribing · Switched to the new default output")); };
            StateChanged?.Invoke(AppText.T("正在连接 · 尚未采集声音", "Connecting · Audio capture has not started"));
            run.Started = StartRunAsync(run, settings, key);
            return run.Started;
        }
    }
    private async Task StartRunAsync(Run run, ListeningSettings settings, string key)
    {
        try
        {
            await run.Speech.StartAsync(key, run.Lifetime.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_current != run || run.Lifetime.IsCancellationRequested) return;
                CaptureStarted?.Invoke(DateTimeOffset.UtcNow);
                run.Audio.Start(settings.OutputDeviceId);
                StateChanged?.Invoke(AppText.T("正在转写电脑声音 · 音频上传至 OpenAI", "Transcribing computer audio · Audio is sent to OpenAI"));
            }
        }
        catch (OperationCanceledException) when (run.Lifetime.IsCancellationRequested) { }
        catch { Fail(run, AppText.T("连接或设备启动失败，请检查设置、网络和 API 额度后重新开始。", "The connection or device could not start. Check settings, network, and API balance before restarting.")); }
    }
    private void OnTranscript(Run run, TranscriptUpdate update)
    {
        lock (_gate)
        {
            if (_current != run || run.Lifetime.IsCancellationRequested || string.IsNullOrWhiteSpace(update.Text)) return;
            if (update.Text.Length > 5000) { Fail(run, AppText.T("单段字幕过长，已停止。请重新开始。", "A subtitle segment is too long. The session has stopped; start again.")); return; }
            var id = run.Generation + ":" + update.ItemId;
            var needs = update.IsFinal && run.Translations is not null && TranscriptTranslationQueue.NeedsTranslation(update.Text);
            CaptionChanged?.Invoke(new(id, run.Generation, update.Sequence, update.Timestamp, update.Text, update.IsFinal,
                TranslationStatus: needs ? "queued" : "", EndTime: update.EndTime, TranslationLanguage: run.TargetLanguage, SourceLanguages: run.SourceLanguages));
            if (needs && !run.Translations!.TryEnqueue(id, update.Text))
                TranslationChanged?.Invoke(new(id, update.Text, "", "busy", run.TargetLanguage));
        }
    }
    private void Fail(Run run, string message)
    {
        // Fault callbacks can originate inside StartAsync; cleanup must await startup off that stack.
        _ = Task.Run(async () =>
        {
            Task stopped;
            lock (_gate)
            {
                if (_current != run) return;
                stopped = StopAsync();
            }
            try { await stopped.ConfigureAwait(false); }
            catch { message = AppText.T("停止时遇到错误，请退出应用后重新打开。", "An error occurred while stopping. Exit and reopen the app."); }
            lock (_gate) if (_current is null && _generation == run.Generation) StateChanged?.Invoke(message);
        });
    }
    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_current is not { } run) return _stopping;
            _current = null;
            run.Lifetime.Cancel();
            _stopping = Task.Run(async () =>
            {
                var failed = false;
                try { run.Audio.Stop(); } catch { failed = true; }
                try { if (run.Translations is not null) await run.Translations.DisposeAsync().ConfigureAwait(false); } catch { failed = true; }
                try { await run.Started.ConfigureAwait(false); } catch { failed = true; }
                try { await run.Speech.DisposeAsync().ConfigureAwait(false); } catch { failed = true; }
                try { run.Audio.Dispose(); } catch { failed = true; }
                run.Lifetime.Dispose();
                if (failed) throw new InvalidOperationException(AppText.T("部分资源释放失败，请退出应用后重新打开。", "Some resources could not be released. Exit and reopen the app."));
            });
            StateChanged?.Invoke(AppText.T("已停止 · 可回看本次记录", "Stopped · This session is available in history"));
            LevelChanged?.Invoke(0);
            return _stopping;
        }
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
    private sealed class Run(long generation, IAudioInput audio, ITranscriber speech, string sourceLanguages, string targetLanguage)
    {
        public long Generation { get; } = generation;
        public IAudioInput Audio { get; } = audio;
        public ITranscriber Speech { get; } = speech;
        public string SourceLanguages { get; } = sourceLanguages;
        public string TargetLanguage { get; } = targetLanguage;
        public CancellationTokenSource Lifetime { get; } = new();
        public TranscriptTranslationQueue? Translations;
        public Task Started = Task.CompletedTask;
    }
}

using ListeningSubtitles.Core;
using System.Diagnostics;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ListeningSubtitles.Audio;

/// <summary>
/// Starts only on explicit request. Automatic sources follow Multimedia default endpoints within
/// the existing session. All consumer events share one dispatcher. EndpointChanging retires the
/// old stream after its in-flight callback; EndpointChanged precedes the replacement's first chunk.
/// Delivered arrays belong to the consumer. Queued stale arrays are cleared, never replayed.
/// </summary>
public sealed class SystemAudioCapture : ListeningSubtitles.Core.IAudioInput
{
    private readonly object controlGate = new();
    private readonly Func<AudioSourceKind, string?, IWaveIn> factory;
    private readonly Func<IAudioEndpointMonitor>? monitorFactory;
    private readonly TimeSpan recoveryWindow;
    private readonly TimeSpan retryDelay;
    private Session? current;
    private bool disposed;
    public event Action<PcmChunk>? ChunkAvailable;
    public event Action<string>? Failed;
    public event Action<AudioSourceKind>? EndpointChanging;
    public event Action<AudioSourceKind>? EndpointChanged;

    public SystemAudioCapture() : this(CreateEndpoint, () => new WindowsAudioEndpointMonitor()) { }
    internal SystemAudioCapture(Func<AudioSourceKind, string?, IWaveIn> factory) : this(factory, null) { }
    internal SystemAudioCapture(Func<AudioSourceKind, string?, IWaveIn> factory,
        Func<IAudioEndpointMonitor>? monitorFactory, TimeSpan? recoveryWindow = null, TimeSpan? retryDelay = null)
    {
        this.factory = factory; this.monitorFactory = monitorFactory;
        this.recoveryWindow = recoveryWindow ?? TimeSpan.FromMilliseconds(2500);
        this.retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(75);
    }
    public bool IsRunning { get { lock (controlGate) return current is { Started: true, Active: 1 }; } }
    public static IReadOnlyList<AudioDeviceInfo> GetRenderDevices() => Enumerate(DataFlow.Render);

    public void Start(string? outputDeviceId = null)
    {
        lock (controlGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (current is not null) throw new InvalidOperationException(AppText.T("电脑声音采集已经启动，请先停止。", "System audio capture is already running. Stop it first."));
            var session = new Session();
            session.Automatic[0] = monitorFactory is not null && string.IsNullOrWhiteSpace(outputDeviceId);
            Volatile.Write(ref current, session);
            try
            {
                if (session.Automatic.Any(value => value))
                {
                    session.Monitor = monitorFactory!();
                    session.MonitorHandler = source => RequestSwitch(session, source);
                    session.Monitor.DefaultChanged += session.MonitorHandler;
                }
                foreach (var source in new[] { AudioSourceKind.System })
                {
                    var id = outputDeviceId;
                    if (session.Automatic[(int)source]) id = ReadDefault(session, source);
                    session.Inputs[(int)source] = CreateInput(session, source, id);
                }
                foreach (var input in session.Inputs)
                {
                    input!.Endpoint.StartRecording();
                    if (Volatile.Read(ref input.Faulted) != 0) throw new InvalidOperationException(AppText.T("音频设备启动中断。", "Audio device startup was interrupted."));
                }
                foreach (var input in session.Inputs)
                {
                    input!.ActivatedAt = Stopwatch.GetTimestamp();
                    if (Volatile.Read(ref input!.Faulted) != 0 || Interlocked.CompareExchange(ref input.Active, 1, 0) != 0)
                        throw new InvalidOperationException(AppText.T("音频设备启动中断。", "Audio device startup was interrupted."));
                }
                session.Started = true;
                _ = Task.Run(() => DispatchAsync(session));
                if (session.Monitor is not null)
                {
                    _ = Task.Run(() => SwitchLoopAsync(session));
                    // Re-read after registration and starting capture, closing the initialization race.
                    RequestSwitch(session, AudioSourceKind.System);
                }
            }
            catch
            {
                Volatile.Write(ref current, null); ReleaseSession(session);
                throw new InvalidOperationException(AppText.T("无法启动电脑声音采集。请检查 Windows 默认或手选输出设备，以及其他应用的独占占用，然后重新开始。", "Unable to capture system audio. Check the selected or default Windows playback device and any exclusive use by another app, then start again."));
            }
        }
    }
    public void Stop()
    {
        Session? session;
        lock (controlGate)
        {
            session = current; Volatile.Write(ref current, null);
            if (session is not null) Interlocked.Exchange(ref session.Active, 0);
        }
        if (session is not null) ReleaseSession(session);
    }
    public void Dispose()
    {
        lock (controlGate) { if (disposed) return; disposed = true; }
        Stop(); GC.SuppressFinalize(this);
    }
    private CaptureInput CreateInput(Session session, AudioSourceKind source, string? id)
    {
        var endpoint = factory(source, id);
        try
        {
            var input = new CaptureInput(source, id ?? "", endpoint, new PcmStreamConverter(endpoint.WaveFormat, source));
            input.DataHandler = (_, args) => OnData(session, input, args);
            input.StoppedHandler = (_, _) =>
            {
                Interlocked.CompareExchange(ref input.StoppedAt, Stopwatch.GetTimestamp(), 0);
                Interlocked.Exchange(ref input.Faulted, 1); Interlocked.Exchange(ref input.Active, -1);
                if (!IsInputOwned(session, input)) return;
                if (session.Automatic[(int)source]) RequestSwitch(session, source);
                else RequestFailure(session, AppText.T("手选输出设备已中断，电脑声音采集已停止。请检查设备后重新开始。", "The selected playback device disconnected. Capture stopped; check the device and start again."));
            };
            endpoint.DataAvailable += input.DataHandler; endpoint.RecordingStopped += input.StoppedHandler;
            return input;
        }
        catch { try { endpoint.Dispose(); } catch { } throw; }
    }
    private bool IsCurrent(Session session) => Volatile.Read(ref session.Active) != 0 && ReferenceEquals(Volatile.Read(ref current), session);
    private bool IsInputOwned(Session session, CaptureInput input) => IsCurrent(session)
        && ReferenceEquals(Volatile.Read(ref session.Inputs[(int)input.Source]), input);
    private bool CanDeliver(Session session, CaptureInput input) => Volatile.Read(ref input.Active) == 1 && IsInputOwned(session, input);

    private void OnData(Session session, CaptureInput input, WaveInEventArgs args)
    {
        if (!CanDeliver(session, input)) return;
        try
        {
            if (args.BytesRecorded < 0 || args.BytesRecorded > args.Buffer.Length
                || args.BytesRecorded > (long)input.Endpoint.WaveFormat.AverageBytesPerSecond * 2)
            { RequestFailure(session, AppText.T("音频缓冲区溢出，电脑声音采集已停止。请减轻电脑负载后重新开始。", "Audio buffer overflow. Capture stopped; reduce system load and start again.")); return; }
            lock (input.ConvertGate)
            {
                if (!CanDeliver(session, input)) return;
                var overflow = false;
                foreach (var chunk in input.Converter.Consume(args.Buffer.AsSpan(0, args.BytesRecorded)))
                {
                    if (overflow || !CanDeliver(session, input)) { Array.Clear(chunk.Data); continue; }
                    input.PendingChunks.Add(chunk.Data);
                    if (!session.Queue.Writer.TryWrite(new DispatchItem(input, chunk)))
                    {
                        input.PendingChunks.Remove(chunk.Data); Array.Clear(chunk.Data); overflow = true;
                        RequestFailure(session, AppText.T("音频处理队列已满，电脑声音采集已停止。请检查网络与电脑负载后重新开始。", "The audio queue is full. Capture stopped; check your network and system load, then start again."));
                    }
                    else Volatile.Write(ref input.ProducedData, 1);
                }
            }
        }
        catch { RequestFailure(session, AppText.T("音频格式转换失败，电脑声音采集已停止。请重新选择设备后再试。", "Audio conversion failed. Capture stopped; select a playback device and try again.")); }
    }
    private async Task DispatchAsync(Session session)
    {
        try
        {
            await foreach (var item in session.Queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                bool deliver;
                if (item.Changing) deliver = IsCurrent(session);
                else lock (item.Input.ConvertGate)
                {
                    if (item.Chunk is not null) item.Input.PendingChunks.Remove(item.Chunk.Data);
                    deliver = CanDeliver(session, item.Input);
                }
                if (!deliver) { if (item.Chunk is not null) Array.Clear(item.Chunk.Data); continue; }
                try
                {
                    if (item.Changing) EndpointChanging?.Invoke(item.Input.Source);
                    else if (item.Chunk is null) EndpointChanged?.Invoke(item.Input.Source);
                    else ChunkAvailable?.Invoke(item.Chunk);
                }
                catch { FailSession(session, AppText.T("音频处理回调失败，电脑声音采集已停止。请重新开始。", "Audio processing failed. Capture stopped; start again.")); return; }
            }
        }
        catch { FailSession(session, AppText.T("音频处理已中断，电脑声音采集已停止。请重新开始。", "Audio processing was interrupted. Capture stopped; start again.")); }
    }
    // COM/NAudio callbacks only mark dirty state; no waits, releases or observer calls here.
    private void RequestSwitch(Session session, AudioSourceKind source)
    {
        if (!IsCurrent(session) || source != AudioSourceKind.System
            || !session.Automatic[(int)source]) return;
        Interlocked.Or(ref session.DirtySources, 1 << (int)source); session.SwitchSignal.Writer.TryWrite(true);
    }
    private async Task SwitchLoopAsync(Session session)
    {
        try
        {
            await foreach (var _ in session.SwitchSignal.Reader.ReadAllAsync(session.Lifetime.Token).ConfigureAwait(false))
            {
                int dirty;
                while (IsCurrent(session) && (dirty = Interlocked.Exchange(ref session.DirtySources, 0)) != 0)
                    foreach (var source in new[] { AudioSourceKind.System })
                        if ((dirty & (1 << (int)source)) != 0 && IsCurrent(session)) await ReconcileAsync(session, source).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!IsCurrent(session)) { }
        catch { FailSession(session, AppText.T("音频设备切换失败，电脑声音采集已停止。请检查 Windows 默认设备后重新开始。", "Unable to switch audio devices. Capture stopped; check the Windows default device and start again.")); }
    }
    private string ReadDefault(Session session, AudioSourceKind source)
    {
        var id = session.Monitor!.GetDefaultId(source);
        return !string.IsNullOrWhiteSpace(id) ? id : throw new InvalidOperationException(AppText.T("当前无可用默认音频设备。", "No default audio device is currently available."));
    }
    private async Task ReconcileAsync(Session session, AudioSourceKind source)
    {
        var index = (int)source;
        if (session.RecoveryStartedAt[index] != 0)
            await Task.Delay(retryDelay, session.Lifetime.Token).ConfigureAwait(false);
        while (IsCurrent(session))
        {
            string? desired = null;
            try { desired = ReadDefault(session, source); } catch { }
            var old = Volatile.Read(ref session.Inputs[index]);
            // Immediate failures after a successful Start are still the same recovery attempt.
            // A silent but stable loopback also earns a fresh budget for a later, unrelated unplug.
            if (old is not null && WasStable(old)) session.RecoveryStartedAt[index] = 0;
            if (old is not null && old.Id == desired && Volatile.Read(ref old.Faulted) == 0 && Volatile.Read(ref old.Active) == 1) return;
            if (session.RecoveryStartedAt[index] == 0) session.RecoveryStartedAt[index] = Stopwatch.GetTimestamp();
            if (RecoveryExpired(session, source)) { FailRecovery(session, source); return; }
            if (old is not null)
            {
                Interlocked.Exchange(ref old.Active, -1);
                Interlocked.CompareExchange(ref session.Inputs[(int)source], null, old);
                // Queue the boundary before releasing/opening drivers, which can take time during
                // unplug. This marker belongs to the session, not the already retired input.
                var boundaryQueued = session.Queue.Writer.TryWrite(new DispatchItem(old, null, Changing: true));
                ReleaseInput(old);
                if (!boundaryQueued)
                {
                    FailSession(session, AppText.T("音频切换边界队列已满，电脑声音采集已停止。请重新开始。", "The device-switch queue is full. Capture stopped; start again.")); return;
                }
            }
            if (!IsCurrent(session)) return;
            if (desired is not null)
            {
                CaptureInput? candidate = null;
                var committed = false;
                try
                {
                    candidate = CreateInput(session, source, desired);
                    // No control lock around driver operations: cancellation wins a slow open/start.
                    if (!IsCurrent(session)) return;
                    if (ReadDefault(session, source) == desired)
                    {
                        if (!IsCurrent(session)) return;
                        candidate.Endpoint.StartRecording();
                        if (!IsCurrent(session)) return;
                        if (Volatile.Read(ref candidate.Faulted) == 0 && ReadDefault(session, source) == desired)
                        {
                            lock (controlGate)
                            {
                                if (!IsCurrent(session)) return;
                                lock (candidate.ConvertGate)
                                {
                                    Volatile.Write(ref session.Inputs[(int)source], candidate);
                                    if (!session.Queue.Writer.TryWrite(new DispatchItem(candidate, null))) throw new InvalidOperationException(AppText.T("切换事件队列已满。", "The device-switch event queue is full."));
                                    // CAS cannot resurrect a candidate whose worker has already stopped.
                                    candidate.ActivatedAt = Stopwatch.GetTimestamp();
                                    if (Volatile.Read(ref candidate.Faulted) != 0 || Interlocked.CompareExchange(ref candidate.Active, 1, 0) != 0)
                                        throw new InvalidOperationException(AppText.T("新音频设备启动中断。", "Startup of the replacement audio device was interrupted."));
                                    committed = true;
                                }
                            }
                            return;
                        }
                    }
                }
                catch { /* Bluetooth/unplug transitions can briefly have no usable endpoint. */ }
                finally { if (!committed && candidate is not null) ReleaseInput(candidate); }
            }
            if (!IsCurrent(session)) return;
            if (RecoveryExpired(session, source)) { FailRecovery(session, source); return; }
            await Task.Delay(retryDelay, session.Lifetime.Token).ConfigureAwait(false);
        }
    }
    private static bool WasStable(CaptureInput input)
    {
        if (Volatile.Read(ref input.ProducedData) != 0) return true;
        var activated = Volatile.Read(ref input.ActivatedAt);
        if (activated == 0) return false;
        var stopped = Volatile.Read(ref input.StoppedAt);
        return Stopwatch.GetElapsedTime(activated, stopped == 0 ? Stopwatch.GetTimestamp() : stopped) >= TimeSpan.FromMilliseconds(500);
    }
    private bool RecoveryExpired(Session session, AudioSourceKind source) =>
        Stopwatch.GetElapsedTime(session.RecoveryStartedAt[(int)source]) >= recoveryWindow;
    private void FailRecovery(Session session, AudioSourceKind source) => FailSession(session,
        AppText.T("未能在短暂等待后连接 Windows 新默认输出，电脑声音采集已停止。请检查耳机或扬声器后重新开始。", "Unable to connect to the new Windows default output after a short wait. Capture stopped; check your headphones or speakers and start again."));
    private void RequestFailure(Session session, string message)
    {
        if (!IsCurrent(session) || Interlocked.Exchange(ref session.FailureRequested, 1) != 0) return;
        _ = Task.Run(() => FailSession(session, message));
    }
    private void FailSession(Session session, string message)
    {
        lock (controlGate)
        {
            if (!ReferenceEquals(current, session)) return;
            Volatile.Write(ref current, null); Interlocked.Exchange(ref session.Active, 0);
        }
        ReleaseSession(session);
        try { Failed?.Invoke(message); } catch { }
    }
    private static void ReleaseSession(Session session)
    {
        Interlocked.Exchange(ref session.Active, 0);
        if (Interlocked.Exchange(ref session.Released, 1) != 0) return;
        session.Started = false; session.Lifetime.Cancel();
        session.SwitchSignal.Writer.TryComplete(); session.Queue.Writer.TryComplete();
        var monitor = session.Monitor;
        if (monitor is not null)
        {
            try { monitor.DefaultChanged -= session.MonitorHandler; } catch { }
            try { monitor.Dispose(); } catch { }
        }
        for (var index = 0; index < session.Inputs.Length; index++) ReleaseInput(Interlocked.Exchange(ref session.Inputs[index], null));
        while (session.Queue.Reader.TryRead(out var item)) if (item.Chunk is not null) Array.Clear(item.Chunk.Data);
    }
    private static void ReleaseInput(CaptureInput? input)
    {
        if (input is null) return;
        Interlocked.Exchange(ref input.Active, -1);
        if (Interlocked.Exchange(ref input.Released, 1) != 0) return;
        lock (input.ConvertGate)
        {
            foreach (var pending in input.PendingChunks) Array.Clear(pending);
            input.PendingChunks.Clear();
        }
        try { input.Endpoint.DataAvailable -= input.DataHandler; } catch { }
        try { input.Endpoint.RecordingStopped -= input.StoppedHandler; } catch { }
        try { input.Endpoint.StopRecording(); } catch { }
        try { input.Endpoint.Dispose(); } catch { }
        lock (input.ConvertGate) input.Converter.Discard();
    }
    private static IReadOnlyList<AudioDeviceInfo> Enumerate(DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator(); var result = new List<AudioDeviceInfo>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)) using (device) result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
            return result;
        }
        catch { throw new InvalidOperationException(AppText.T("无法列出音频设备，请检查 Windows 音频服务和设备连接。", "Unable to list audio devices. Check the Windows audio service and device connections.")); }
    }
    private static IWaveIn CreateEndpoint(AudioSourceKind source, string? id)
    {
        if (source != AudioSourceKind.System) throw new ArgumentException(AppText.T("听力字幕只采集电脑播放声音。", "Listening Subtitles captures system playback audio only."));
        using var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrWhiteSpace(id) ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(id);
        try
        {
            if (device.State != DeviceState.Active || device.DataFlow != DataFlow.Render) throw new InvalidOperationException(AppText.T("所选输出设备不可用。", "The selected playback device is unavailable."));
            return new OwnedEndpoint(device, new SafeWasapiCapture(device, loopback: true));
        }
        catch { device.Dispose(); throw; }
    }
    private sealed class OwnedEndpoint(MMDevice device, IWaveIn capture) : IWaveIn
    {
        public WaveFormat WaveFormat { get => capture.WaveFormat; set => capture.WaveFormat = value; }
        public event EventHandler<WaveInEventArgs>? DataAvailable { add => capture.DataAvailable += value; remove => capture.DataAvailable -= value; }
        public event EventHandler<StoppedEventArgs>? RecordingStopped { add => capture.RecordingStopped += value; remove => capture.RecordingStopped -= value; }
        public void StartRecording() => capture.StartRecording();
        public void StopRecording() => capture.StopRecording();
        public void Dispose() { try { capture.Dispose(); } finally { device.Dispose(); } }
    }
    private sealed record DispatchItem(CaptureInput Input, PcmChunk? Chunk, bool Changing = false);
    private sealed class Session
    {
        public int Active = 1, Released, FailureRequested, DirtySources;
        public bool Started;
        public readonly bool[] Automatic = new bool[1];
        public readonly CaptureInput?[] Inputs = new CaptureInput?[1];
        public readonly long[] RecoveryStartedAt = new long[1]; // Only the serial switch worker writes these.
        public IAudioEndpointMonitor? Monitor;
        public Action<AudioSourceKind>? MonitorHandler;
        public readonly CancellationTokenSource Lifetime = new();
        public Channel<DispatchItem> Queue { get; } = Channel.CreateBounded<DispatchItem>(new BoundedChannelOptions(80)
            { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });
        public Channel<bool> SwitchSignal { get; } = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    }
    private sealed class CaptureInput(AudioSourceKind source, string id, IWaveIn endpoint, PcmStreamConverter converter)
    {
        public readonly AudioSourceKind Source = source;
        public readonly string Id = id;
        public readonly IWaveIn Endpoint = endpoint;
        public readonly PcmStreamConverter Converter = converter;
        public readonly object ConvertGate = new();
        public readonly HashSet<byte[]> PendingChunks = new();
        public int Active, Faulted, Released, ProducedData;
        public long ActivatedAt, StoppedAt;
        public EventHandler<WaveInEventArgs>? DataHandler;
        public EventHandler<StoppedEventArgs>? StoppedHandler;
    }
}

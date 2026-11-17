using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ListeningSubtitles.Audio;

internal interface IAudioEndpointMonitor : IDisposable
{
    event Action<AudioSourceKind>? DefaultChanged;
    string? GetDefaultId(AudioSourceKind source);
}

/// <summary>Reads endpoint metadata and subscribes to notifications; never opens an audio client.</summary>
internal sealed class WindowsAudioEndpointMonitor : IAudioEndpointMonitor
{
    private readonly object queryGate = new();
    private readonly MMDeviceEnumerator enumerator;
    private readonly EndpointNotificationClient callback;
    private bool disposed;
    public event Action<AudioSourceKind>? DefaultChanged;

    public WindowsAudioEndpointMonitor()
    {
        enumerator = new MMDeviceEnumerator();
        callback = new EndpointNotificationClient(source => DefaultChanged?.Invoke(source));
        try { Marshal.ThrowExceptionForHR(enumerator.RegisterEndpointNotificationCallback(callback)); }
        catch { callback.Cancel(); enumerator.Dispose(); throw; }
    }

    public string? GetDefaultId(AudioSourceKind source)
    {
        lock (queryGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var device = enumerator.GetDefaultAudioEndpoint(
                source == AudioSourceKind.System ? DataFlow.Render : DataFlow.Capture, Role.Multimedia);
            return device.State == DeviceState.Active ? device.ID : null;
        }
    }

    public void Dispose()
    {
        lock (queryGate)
        {
            if (disposed) return;
            disposed = true;
            callback.Cancel();
            DefaultChanged = null;
            try { Marshal.ThrowExceptionForHR(enumerator.UnregisterEndpointNotificationCallback(callback)); }
            finally { enumerator.Dispose(); }
        }
    }
}

/// <summary>COM callbacks only enqueue metadata invalidation; they never wait, release or open devices.</summary>
internal sealed class EndpointNotificationClient(Action<AudioSourceKind> notify) : IMMNotificationClient
{
    private int cancelled;
    public void Cancel() => Interlocked.Exchange(ref cancelled, 1);
    private void Signal(AudioSourceKind source)
    {
        if (Volatile.Read(ref cancelled) != 0) return;
        try { notify(source); } catch { /* Never throw across a Windows notification callback. */ }
    }
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string deviceId)
    {
        if (role != Role.Multimedia) return;
        if (flow == DataFlow.Render) Signal(AudioSourceKind.System);
        else if (flow == DataFlow.Capture) Signal(AudioSourceKind.Microphone);
    }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => SignalBoth();
    public void OnDeviceRemoved(string deviceId) => SignalBoth();
    public void OnDeviceAdded(string deviceId) => SignalBoth();
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    private void SignalBoth() { Signal(AudioSourceKind.System); Signal(AudioSourceKind.Microphone); }
}

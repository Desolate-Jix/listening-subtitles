using System.Net.WebSockets;

namespace ListeningSubtitles.Speech;

/// <summary>Frame-level boundary for deterministic tests; implementations must honor cancellation.</summary>
public interface IRealtimeTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken);
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);
    ValueTask<RealtimeReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    void Abort();
}

public readonly record struct RealtimeReceiveResult(int Count, bool EndOfMessage, bool IsText, bool IsClose);

internal sealed class OpenAiWebSocketTransport : IRealtimeTransport
{
    private readonly ClientWebSocket _socket = new();
    private readonly HttpMessageInvoker _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

    public Task ConnectAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken)
    {
        _socket.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        return _socket.ConnectAsync(endpoint, _http, cancellationToken);
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
        => _socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    public async ValueTask<RealtimeReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new(result.Count, result.EndOfMessage, result.MessageType == WebSocketMessageType.Text, result.MessageType == WebSocketMessageType.Close);
    }

    public void Abort() => _socket.Abort();

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}

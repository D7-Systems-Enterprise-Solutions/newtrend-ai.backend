using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace NewtrendAi.Core.Live;

public sealed class LiveBroadcaster
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILiveStore _store;
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public LiveBroadcaster(ILiveStore store) => _store = store;

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket connection required.");
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var id = Guid.NewGuid();
        _clients[id] = socket;

        try
        {
            await SendSnapshotAsync(socket, context.RequestAborted);
            var buffer = new byte[64];
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            _clients.TryRemove(id, out _);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                }
                catch (WebSocketException)
                {
                }
            }

            socket.Dispose();
        }
    }

    public async Task BroadcastAsync()
    {
        var payload = SnapshotBytes();
        foreach (var (id, socket) in _clients)
        {
            if (socket.State != WebSocketState.Open)
            {
                _clients.TryRemove(id, out _);
                continue;
            }

            try
            {
                await SendAsync(socket, payload);
            }
            catch (WebSocketException)
            {
                _clients.TryRemove(id, out _);
            }
        }
    }

    private async Task SendSnapshotAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        await SendAsync(socket, SnapshotBytes(), cancellationToken);
    }

    private byte[] SnapshotBytes()
    {
        var snapshot = new LiveSnapshot("snapshot", _store.Snapshot());
        return JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
    }

    private async Task SendAsync(WebSocket socket, byte[] payload, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private sealed record LiveSnapshot(string Type, IReadOnlyList<Execution> Executions);
}

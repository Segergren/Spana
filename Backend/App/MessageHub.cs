using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Spana.Backend.Core;

namespace Spana.Backend.App;

public static class MessageHub
{
    private static readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> Sockets = new();

    public static async Task RunAsync(WebSocket socket)
    {
        var gate = new SemaphoreSlim(1, 1);
        Sockets[socket] = gate;
        var buffer = new byte[4096];
        try
        {
            await Send("player", Player.PlayerService.GetState());
            await Send("cast", Cast.CastService.GetState());
            await Send("update", UpdateService.GetState());
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            Sockets.TryRemove(socket, out _);
        }
    }

    public static async Task Send(string type, object data)
    {
        if (Sockets.IsEmpty) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, data }, Storage.Json));
        foreach (var (socket, gate) in Sockets)
        {
            if (socket.State != WebSocketState.Open) continue;
            await gate.WaitAsync();
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
            }
            finally
            {
                gate.Release();
            }
        }
    }
}

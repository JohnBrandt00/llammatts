using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace LlamaTts.Web.Services;

/// <summary>
/// Fan-out hub for server-sent events. Publishers push typed JSON payloads; each connected
/// browser gets its own bounded channel (slow clients drop oldest events rather than block).
/// </summary>
public sealed class EventHub
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ConcurrentDictionary<Guid, Channel<(string Type, string Json)>> _clients = new();

    public int ClientCount => _clients.Count;

    public void Publish(string type, object payload)
    {
        if (_clients.IsEmpty) return;
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        foreach (var channel in _clients.Values)
            channel.Writer.TryWrite((type, json));
    }

    public async Task SubscribeAsync(HttpContext context)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<(string Type, string Json)>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        _clients[id] = channel;

        var response = context.Response;
        var ct = context.RequestAborted;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            await response.WriteAsync(": connected\n\n", ct);
            await response.Body.FlushAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                var read = channel.Reader.WaitToReadAsync(ct).AsTask();
                var winner = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(15), ct));
                if (winner != read)
                {
                    await response.WriteAsync(": ping\n\n", ct);
                    await response.Body.FlushAsync(ct);
                    continue;
                }
                if (!await read) break;

                while (channel.Reader.TryRead(out var msg))
                    await response.WriteAsync($"event: {msg.Type}\ndata: {msg.Json}\n\n", ct);
                await response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected
        }
        finally
        {
            _clients.TryRemove(id, out _);
        }
    }
}

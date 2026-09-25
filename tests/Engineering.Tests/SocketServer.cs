using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Engineering.Tests;

internal sealed class SocketServer(WebApplication app, Uri endpoint, Task completion) : IAsyncDisposable
{
    public Uri Endpoint { get; } = endpoint;
    public Task Completion { get; } = completion;

    public static async Task<SocketServer> Start(Func<WebSocket, Task> handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.UseWebSockets();
        app.Run(async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try { await handler(socket); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new(app, new Uri(address.Replace("http:", "ws:")), completion.Task);
    }

    public static async Task<JsonElement> Read(WebSocket socket)
    {
        using var stream = new MemoryStream();
        var bytes = new byte[4096];
        WebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), CancellationToken.None);
            if (frame.MessageType == WebSocketMessageType.Close) throw new IOException("Closed");
            stream.Write(bytes, 0, frame.Count);
        } while (!frame.EndOfMessage);
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    public static async Task Write(WebSocket socket, object value, int fragmentSize = int.MaxValue)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        for (var offset = 0; offset < bytes.Length;)
        {
            var count = Math.Min(fragmentSize, bytes.Length - offset);
            await socket.SendAsync(new ArraySegment<byte>(bytes, offset, count), WebSocketMessageType.Text,
                offset + count == bytes.Length, CancellationToken.None);
            offset += count;
        }
    }

    public static Task Reply(WebSocket socket, JsonElement command, object result) => Write(socket,
        new { id = command.GetProperty("id").GetInt32(), sessionId = command.TryGetProperty("sessionId", out var session) ? session.GetString() : null, result });

    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
}

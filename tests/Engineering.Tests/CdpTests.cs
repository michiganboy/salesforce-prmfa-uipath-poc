using System.Net.WebSockets;
using Salesforce.Prmfa.Cdp;
using Xunit;

namespace Engineering.Tests;

public class CdpTests
{
    [Fact]
    public async Task ConcurrentCommands_OutOfOrderResponses_Events_FragmentedLargeUnicode()
    {
        var payload = string.Concat(Enumerable.Repeat("\u20ac", 40000));
        await using var server = await SocketServer.Start(async socket =>
        {
            var commands = new List<System.Text.Json.JsonElement>();
            for (var i = 0; i < 32; i++) commands.Add(await SocketServer.Read(socket));
            foreach (var command in commands.AsEnumerable().Reverse())
            {
                await SocketServer.Write(socket, new { method = "Page.loadEventFired", @params = new { timestamp = 1 } });
                await SocketServer.Write(socket, new { id = command.GetProperty("id").GetInt32(), sessionId = "page", result = new { method = command.GetProperty("method").GetString(), payload } }, 997);
            }
            await Task.Delay(100);
        });
        await using var client = new CdpClient();
        await client.ConnectAsync(server.Endpoint);
        var tasks = Enumerable.Range(0, 32).Select(async i =>
        {
            var result = await client.SendAsync($"test.{i}", sessionId: "page");
            Assert.Equal($"test.{i}", result.GetProperty("method").GetString());
            Assert.Equal(payload, result.GetProperty("payload").GetString());
        });
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        await server.Completion;
    }

    [Fact]
    public async Task CancellationAndLateResponse_DoNotConsumeNextResponse()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketServer.Start(async socket =>
        {
            var first = await SocketServer.Read(socket);
            received.SetResult();
            await release.Task;
            var second = await SocketServer.Read(socket);
            await SocketServer.Reply(socket, first, new { stale = true });
            await SocketServer.Reply(socket, second, new { fresh = true });
            await Task.Delay(100);
        });
        await using var client = new CdpClient();
        await client.ConnectAsync(server.Endpoint);
        using var cancel = new CancellationTokenSource();
        var firstTask = client.SendAsync("first", cancellationToken: cancel.Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstTask);
        release.SetResult();
        Assert.True((await client.SendAsync("second")).GetProperty("fresh").GetBoolean());
        await server.Completion;
    }

    [Theory]
    [InlineData("close")]
    [InlineData("abort")]
    [InlineData("malformed")]
    [InlineData("binary")]
    [InlineData("oversize")]
    [InlineData("wrong-session")]
    public async Task BrokenTransportFailsAllPending(string mode)
    {
        await using var server = await SocketServer.Start(async socket =>
        {
            var first = await SocketServer.Read(socket);
            await SocketServer.Read(socket);
            switch (mode)
            {
                case "close": await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); break;
                case "abort": socket.Abort(); break;
                case "malformed": await socket.SendAsync(new ArraySegment<byte>("{"u8.ToArray()), WebSocketMessageType.Text, true, CancellationToken.None); break;
                case "binary": await socket.SendAsync(new ArraySegment<byte>(new byte[] { 1 }), WebSocketMessageType.Binary, true, CancellationToken.None); break;
                case "oversize": await SocketServer.Write(socket, new { text = new string('a', 2000) }); break;
                case "wrong-session": await SocketServer.Write(socket, new { id = first.GetProperty("id").GetInt32(), sessionId = "other", result = new { } }); break;
            }
            await Task.Delay(100);
        });
        await using var client = new CdpClient(maxMessageBytes: 1024);
        await client.ConnectAsync(server.Endpoint);
        var firstTask = client.SendAsync("first");
        var secondTask = client.SendAsync("second");
        Assert.NotNull(await Record.ExceptionAsync(() => firstTask.WaitAsync(TimeSpan.FromSeconds(3))));
        Assert.NotNull(await Record.ExceptionAsync(() => secondTask.WaitAsync(TimeSpan.FromSeconds(3))));
        Assert.True(firstTask.IsCompleted);
        Assert.True(secondTask.IsCompleted);
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync("after"));
        await server.Completion;
    }

    [Fact]
    public async Task DisposalIsIdempotentAndFailsPending()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketServer.Start(async socket => { await SocketServer.Read(socket); received.SetResult(); await release.Task; });
        var client = new CdpClient();
        await client.ConnectAsync(server.Endpoint);
        var pending = client.SendAsync("pending");
        await received.Task;
        await Task.WhenAll(client.DisposeAsync().AsTask(), client.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(await Record.ExceptionAsync(() => pending));
        release.SetResult();
        await server.Completion;
    }

    [Fact]
    public async Task TimeoutDoesNotBreakConnection_AndErrorsAreCorrelated()
    {
        await using var server = await SocketServer.Start(async socket =>
        {
            await SocketServer.Read(socket);
            var next = await SocketServer.Read(socket);
            await SocketServer.Write(socket, new { id = next.GetProperty("id").GetInt32(), error = new { code = -32601, message = "secret" } });
            var final = await SocketServer.Read(socket);
            await SocketServer.Reply(socket, final, new { ok = true });
            await Task.Delay(100);
        });
        await using var client = new CdpClient(TimeSpan.FromMilliseconds(500));
        await client.ConnectAsync(server.Endpoint);
        await Assert.ThrowsAsync<TimeoutException>(() => client.SendAsync("timeout"));
        var error = await Assert.ThrowsAsync<CdpCommandException>(() => client.SendAsync("unknown"));
        Assert.Equal(-32601, error.Code);
        Assert.DoesNotContain("secret", error.Message);
        Assert.True((await client.SendAsync("final")).GetProperty("ok").GetBoolean());
        await server.Completion;
    }
    [Fact]
    public async Task TargetDetachFailsItsPendingCommand()
    {
        await using var server = await SocketServer.Start(async socket =>
        {
            await SocketServer.Read(socket);
            await SocketServer.Write(socket, new { method = "Target.detachedFromTarget", @params = new { sessionId = "page" } });
            var next = await SocketServer.Read(socket);
            await SocketServer.Reply(socket, next, new { ok = true });
            await Task.Delay(100);
        });
        await using var client = new CdpClient();
        await client.ConnectAsync(server.Endpoint);
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync("pending", sessionId: "page"));
        Assert.True((await client.SendAsync("browser-command")).GetProperty("ok").GetBoolean());
        await server.Completion;
    }

    [Fact]
    public async Task PreCancellationAndSerializationFailureDoNotDamageConnection()
    {
        await using var server = await SocketServer.Start(async socket =>
        {
            var onlyCommand = await SocketServer.Read(socket);
            Assert.Equal("valid", onlyCommand.GetProperty("method").GetString());
            await SocketServer.Reply(socket, onlyCommand, new { ok = true });
            await Task.Delay(100);
        });
        await using var client = new CdpClient();
        await client.ConnectAsync(server.Endpoint);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync("cancelled", cancellationToken: new CancellationToken(true)));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.SendAsync("invalid", new { callback = (Action)(() => { }) }));
        Assert.True((await client.SendAsync("valid")).GetProperty("ok").GetBoolean());
        await server.Completion;
    }

}

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Salesforce.Prmfa.Cdp;

/// <summary>One connection, one reader, serialized writes, independently correlated commands.
/// Disposal aborts this connection, not the browser. Instances cannot reconnect.</summary>
public sealed class CdpClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<int, PendingCommand> _pending = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateLock = new();
    private readonly TimeSpan _commandTimeout;
    private readonly int _maxMessageBytes;
    private Task? _receiveLoop;
    private Task? _disposeTask;
    private Exception? _terminalError;
    private bool _connectStarted;
    private int _nextId;
    private sealed record PendingCommand(string? SessionId, TaskCompletionSource<JsonElement> Completion);

    public CdpClient(TimeSpan? commandTimeout = null, int maxMessageBytes = 16 * 1024 * 1024)
    {
        _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(30);
        if (_commandTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        if (maxMessageBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
        _maxMessageBytes = maxMessageBytes;
    }

    public async Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            lock (_stateLock)
            {
                ThrowIfStopped();
                if (_connectStarted) throw new InvalidOperationException("CDP clients cannot reconnect.");
                _connectStarted = true;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            try { await _socket.ConnectAsync(endpoint, linked.Token); }
            catch (Exception ex) { Stop(ex); throw; }
            _receiveLoop = ReceiveLoopAsync(_lifetime.Token);
        }
        finally { _sendGate.Release(); }
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters = null,
        string? sessionId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        cancellationToken.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var message = new Dictionary<string, object?> { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new { } };
        if (sessionId is not null) message["sessionId"] = sessionId;
        // Serialize before registration so serialization failures cannot leak pending commands.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        lock (_stateLock)
        {
            ThrowIfStopped();
            if (_socket.State != WebSocketState.Open) throw new InvalidOperationException("CDP socket is not connected.");
            _pending[id] = new(sessionId, completion);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(_commandTimeout);
        try
        {
            await _sendGate.WaitAsync(deadline.Token);
            try
            {
                lock (_stateLock) ThrowIfStopped();
                try
                {
                    await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, deadline.Token);
                }
                catch (Exception ex)
                {
                    // A failed/cancelled write may have sent part of a frame. Do not reuse it.
                    Stop(ex);
                    throw;
                }
            }
            finally { _sendGate.Release(); }
            return await completion.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw new IOException("CDP connection ended.", _terminalError);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"CDP command {method} timed out.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
            // Observe a simultaneous disconnect exception even if cancellation won the race.
            _ = completion.Task.Exception;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                message.SetLength(0);
                WebSocketReceiveResult frame;
                do
                {
                    frame = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (frame.MessageType == WebSocketMessageType.Close) throw new IOException("Browser closed the CDP connection.");
                    if (frame.MessageType != WebSocketMessageType.Text) throw new IOException("Expected a CDP text message.");
                    if (message.Length + frame.Count > _maxMessageBytes) throw new IOException("CDP message exceeds configured size limit.");
                    message.Write(buffer, 0, frame.Count);
                } while (!frame.EndOfMessage);
                using var document = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var responseId))
                {
                    // Ordinary unsolicited events cannot consume a command response.
                    if (root.TryGetProperty("method", out var method) && method.GetString() == "Target.detachedFromTarget" &&
                        root.TryGetProperty("params", out var data) && data.TryGetProperty("sessionId", out var detached))
                    {
                        foreach (var item in _pending)
                            if (item.Value.SessionId == detached.GetString() && _pending.TryRemove(item.Key, out var pending))
                                pending.Completion.TrySetException(new IOException("CDP target session detached."));
                    }
                    continue;
                }
                if (!_pending.TryGetValue(responseId.GetInt32(), out var command)) continue; // Late/cancelled response.
                var responseSession = root.TryGetProperty("sessionId", out var session) ? session.GetString() : null;
                if (responseSession != command.SessionId) throw new IOException("CDP response session does not match command.");
                var errorCode = root.TryGetProperty("error", out var error) ? error.GetProperty("code").GetInt32() : (int?)null;
                if (!_pending.TryRemove(responseId.GetInt32(), out command)) continue;
                if (errorCode is not null)
                    command.Completion.TrySetException(new CdpCommandException(errorCode.Value));
                else
                    command.Completion.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            }
        }
        catch (Exception ex) { Stop(ex); }
    }

    private void ThrowIfStopped()
    {
        if (_terminalError is not null) throw new IOException("CDP connection is closed.", _terminalError);
    }

    private void Stop(Exception error)
    {
        lock (_stateLock)
        {
            if (_terminalError is not null) return;
            _terminalError = error;
            foreach (var item in _pending)
                if (_pending.TryRemove(item.Key, out var command)) command.Completion.TrySetException(error);
            _lifetime.Cancel();
            _socket.Abort();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateLock) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Stop(new ObjectDisposedException(nameof(CdpClient)));
        await _sendGate.WaitAsync();
        try
        {
            if (_receiveLoop is not null) await _receiveLoop;
            _socket.Dispose();
            // Keep synchronization objects alive for callers concurrently unwinding SendAsync.
        }
        finally { _sendGate.Release(); }
    }
}

// Do not include arbitrary CDP error text: it can echo URLs containing bearer tokens.
public sealed class CdpCommandException(int code) : Exception($"CDP command failed (code {code}).")
{
    public int Code { get; } = code;
}

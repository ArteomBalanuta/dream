using System.Collections.Concurrent;
using System.Diagnostics;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Simulation.Players;

namespace Dream.Client.Networking;


public sealed class RemoteClientSession : IAsyncDisposable
{
    private readonly ITransportConnection _connection;
    private readonly IProtocolCodec _codec;
    private readonly IMessageFramer _framer;
    private readonly byte[] _receiveChunk = new byte[16 * 1024];
    private readonly FrameReceiveBuffer _receiveBuffer = new();
    private readonly ConcurrentQueue<IServerToClientMessage> _inboundMessages = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _receiveLoopTask;
    // Outbound traffic from the render thread is only enqueued; one writer task sends it, so a
    // full socket buffer can no longer block a frame. Commands collapse into one merged pending
    // frame (one-shot inputs are OR-ed), actions keep their order.
    private readonly LatestPlayerCommandInbox _pendingCommands = new();
    private readonly ConcurrentQueue<IClientToServerMessage> _pendingMessages = new();
    private readonly SemaphoreSlim _outboundSignal = new(0, int.MaxValue);
    private readonly object _sendLoopSync = new();
    private Task? _sendLoopTask;
    // One reusable pooled buffer for every outbound frame; only touched while _sendLock is held,
    // including its Dispose in DisposeAsync.
    private readonly PooledFrameWriter _frameWriter = new();
    // 0 until DisposeAsync starts, then 1. Checked by senders before and after taking _sendLock.
    private int _disposeState;
    private readonly string _commandLogKey;
    private readonly string _slowCommandLogKey;
    private long _sentCommandCount;
    // Assigned under _sendLock together with the encode, so sequences follow wire order.
    private ulong _nextOutboundSequence = 1;
    // Completed by the receive loop: with the server's hello when it arrives, with a
    // ProtocolVersionMismatchException when the server speaks another version, or with null when
    // the loop ends first. ConnectAsync waits on it briefly.
    private readonly TaskCompletionSource<ServerHelloMessage?> _serverHello =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // How long ConnectAsync waits for the server's hello before returning the session anyway.
    public static readonly TimeSpan DefaultServerHelloTimeout = TimeSpan.FromSeconds(2);

    public RemoteClientSession(
        ITransportConnection connection,
        IProtocolCodec codec,
        IMessageFramer framer,
        RemoteClientOptions options)
    {
        _connection = connection;
        _codec = codec;
        _framer = framer;
        Options = options;
        _commandLogKey = $"client.tx.command.{options.SessionId}";
        _slowCommandLogKey = $"client.tx.command.slow.{options.SessionId}";
    }

    public RemoteClientOptions Options { get; }

    public string SessionId => Options.SessionId;

    public bool IsConnected { get; private set; } = true;

    // Why the receive loop stopped, when it stopped for a reason other than the server closing
    // the connection; for example "Server uses protocol v1; this client uses v2.".
    public string? DisconnectReason { get; private set; }

    public static async ValueTask<RemoteClientSession> ConnectAsync(
        RemoteClientOptions options,
        IClientTransport? transport = null,
        IProtocolCodec? codec = null,
        IMessageFramer? framer = null,
        CancellationToken cancellationToken = default,
        TimeSpan? serverHelloTimeout = null)
    {
        transport ??= new TcpClientConnector();
        codec ??= new TransportJsonCodec();
        framer ??= new LengthPrefixedMessageFramer();

        NetworkDiagnosticsLog.Write(
            "client.connect.start",
            $"endpoint={options.EndpointLabel} session={options.SessionId} name={options.DisplayName}");
        Stopwatch? stopwatch = NetworkDiagnosticsLog.IsEnabled ? Stopwatch.StartNew() : null;
        ITransportConnection connection = await transport.ConnectAsync(
            options.Host,
            options.Port,
            cancellationToken);
        stopwatch?.Stop();
        NetworkDiagnosticsLog.Write(
            "client.connect.transport",
            $"endpoint={options.EndpointLabel} connection={connection.ConnectionId} connectMs={stopwatch?.Elapsed.TotalMilliseconds ?? 0d:0.00}");
        RemoteClientSession session = new(connection, codec, framer, options);
        await session.SendHelloAsync(cancellationToken);
        await session.SendJoinRequestAsync(cancellationToken);
        session.StartBackgroundReceive();
        await session.AwaitCompatibleServerAsync(serverHelloTimeout ?? DefaultServerHelloTimeout, cancellationToken);
        NetworkDiagnosticsLog.Write(
            "client.connect.ready",
            $"endpoint={options.EndpointLabel} connection={connection.ConnectionId} session={options.SessionId}");
        return session;
    }

    // Fails the connect when the server speaks another protocol version, so the connection
    // screen shows that reason instead of a bare disconnect. A server that closes without a
    // hello, or answers slower than the timeout, still yields the session as before; a mismatch
    // found later still stops the receive loop and sets DisconnectReason.
    private async ValueTask AwaitCompatibleServerAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _serverHello.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            NetworkDiagnosticsLog.Write(
                "client.connect.hello-timeout",
                $"endpoint={Options.EndpointLabel} timeoutMs={timeout.TotalMilliseconds:0}");
        }
        catch (Exception ex) when (ex is ProtocolVersionMismatchException or OperationCanceledException)
        {
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask SendHelloAsync(CancellationToken cancellationToken = default)
    {
        await SendMessageAsync(new ClientHelloMessage(Options.ClientName), cancellationToken);
    }

    public async ValueTask SendJoinRequestAsync(CancellationToken cancellationToken = default)
    {
        await SendMessageAsync(
            new ClientJoinRequestMessage(Options.SessionId, Options.DisplayName),
            cancellationToken);
    }

    public async ValueTask SendCommandFrameAsync(PlayerCommandFrame commandFrame, CancellationToken cancellationToken = default)
    {
        await SendMessageAsync(new PlayerCommandMessage(commandFrame), cancellationToken);
    }

    public async ValueTask SendPlayerActionAsync(PlayerActionMessage actionMessage, CancellationToken cancellationToken = default)
    {
        await SendMessageAsync(actionMessage, cancellationToken);
    }

    public long SentCommandCount => Interlocked.Read(ref _sentCommandCount);

    public void QueueCommandFrame(PlayerCommandFrame commandFrame)
    {
        _pendingCommands.Enqueue(commandFrame);
        SignalSendLoop();
    }

    public void QueueWorldResyncRequest(ulong clientChecksum)
    {
        _pendingMessages.Enqueue(new ClientWorldResyncRequestMessage(clientChecksum));
        SignalSendLoop();
    }

    public void QueuePlayerAction(PlayerActionMessage actionMessage)
    {
        ArgumentNullException.ThrowIfNull(actionMessage);
        _pendingMessages.Enqueue(actionMessage);
        SignalSendLoop();
    }

    // Waits until everything queued so far has been written, or the timeout expires.
    public async ValueTask<bool> FlushOutboundAsync(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_pendingMessages.IsEmpty && !_pendingCommands.HasPending && _outboundSignal.CurrentCount == 0 && !_sending)
            {
                return true;
            }

            await Task.Delay(1);
        }

        return false;
    }

    private volatile bool _sending;

    private void SignalSendLoop()
    {
        lock (_sendLoopSync)
        {
            _sendLoopTask ??= Task.Run(() => SendLoopAsync(_shutdown.Token));
        }

        _outboundSignal.Release();
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        PlayerSessionId[] sessionIds = [new PlayerSessionId(SessionId)];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _outboundSignal.WaitAsync(cancellationToken);
                _sending = true;
                try
                {
                    while (_pendingMessages.TryDequeue(out IClientToServerMessage? message))
                    {
                        await SendMessageAsync(message, cancellationToken);
                    }

                    foreach (PlayerCommandFrame command in _pendingCommands.Drain(sessionIds))
                    {
                        await SendMessageAsync(new PlayerCommandMessage(command), cancellationToken);
                        Interlocked.Increment(ref _sentCommandCount);
                    }
                }
                finally
                {
                    _sending = false;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            // DisposeAsync started between the loop's wait and its send; not a transport error.
        }
        catch (Exception ex)
        {
            IsConnected = false;
            NetworkDiagnosticsLog.Write(
                "client.send.error",
                $"endpoint={Options.EndpointLabel} session={Options.SessionId} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    public bool TryDequeueInbound(out IServerToClientMessage? message)
    {
        return _inboundMessages.TryDequeue(out message);
    }

    public async ValueTask<bool> ProcessInboundOnceAsync(CancellationToken cancellationToken = default)
    {
        int bytesRead = await _connection.ReceiveAsync(_receiveChunk, cancellationToken);
        if (bytesRead <= 0)
        {
            IsConnected = false;
            return false;
        }

        _receiveBuffer.Append(_receiveChunk.AsSpan(0, bytesRead));
        DrainInboundFrames();
        return true;
    }

    private void DrainInboundFrames()
    {
        while (_receiveBuffer.TryReadPayload(_framer, out ReadOnlySpan<byte> payload))
        {
            if (payload.IsEmpty)
            {
                continue;
            }

            DecodedProtocolMessage decoded;
            try
            {
                decoded = _codec.Decode(payload);
            }
            catch (Exception ex) when (ProtocolVersionMismatchException.ReadForeignProtocolVersion(payload) is int serverVersion)
            {
                throw ProtocolVersionMismatchException.FromServer(serverVersion, ex);
            }

            IServerToClientMessage serverMessage = decoded.Message as IServerToClientMessage
                ?? throw new InvalidOperationException(
                    $"Received non-server message kind '{decoded.Message.MessageKind}' on client transport.");
            if (serverMessage is ServerHelloMessage hello)
            {
                if (hello.ProtocolVersion != TransportEnvelope.CurrentProtocolVersion)
                {
                    throw ProtocolVersionMismatchException.FromServer(hello.ProtocolVersion);
                }

                _serverHello.TrySetResult(hello);
            }

            _inboundMessages.Enqueue(serverMessage);
            LogInbound(decoded.Envelope, serverMessage);
        }
    }

    public void StartBackgroundReceive()
    {
        _receiveLoopTask ??= Task.Run(() => ReceiveLoopAsync(_shutdown.Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        if (_sendLoopTask != null)
        {
            try
            {
                await _sendLoopTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_receiveLoopTask != null)
        {
            try
            {
                await _receiveLoopTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        // The send loop has exited, but a direct Send*Async call (SendHelloAsync and friends) does
        // not run on it and may still be writing the pooled frame buffer to the transport. Take
        // _sendLock so the buffer goes back to ArrayPool<byte>.Shared only after that send ends.
        // Senders that get the lock after this see _disposeState and throw ObjectDisposedException
        // instead of encoding into the returned buffer.
        await _sendLock.WaitAsync();
        try
        {
            _frameWriter.Dispose();
        }
        finally
        {
            _sendLock.Release();
        }

        // _sendLock is deliberately not disposed: a sender still queued on WaitAsync would never be
        // woken by a disposed SemaphoreSlim. It owns no unmanaged handle unless AvailableWaitHandle
        // is read, which this class never does.
        _shutdown.Dispose();
        await _connection.DisposeAsync();
    }

    private async ValueTask SendMessageAsync(IClientToServerMessage message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        Stopwatch? stopwatch = NetworkDiagnosticsLog.IsEnabled ? Stopwatch.StartNew() : null;
        int frameLength;
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            // Disposal may have started while this call waited for the lock; the frame writer is
            // then gone (or about to be), so fail cleanly rather than touch its buffer.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            ReadOnlyMemory<byte> frame = EncodeClientFrame(message);
            frameLength = frame.Length;
            // frame aliases _frameWriter's pooled buffer. Passing it to the transport is safe only
            // because the send is awaited here, inside _sendLock, before the next encode reuses the
            // buffer; transports must not keep the memory after SendAsync completes.
            await _connection.SendAsync(frame, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
            stopwatch?.Stop();
        }

        LogOutbound(message, frameLength, stopwatch?.Elapsed.TotalMilliseconds ?? 0d);
    }

    // Caller holds _sendLock.
    private ReadOnlyMemory<byte> EncodeClientFrame(IClientToServerMessage message)
    {
        TransportEnvelope envelope = new(
            TransportEnvelope.CurrentProtocolVersion,
            message.MessageKind,
            _nextOutboundSequence++);

        _frameWriter.BeginFrame(_framer);
        switch (message)
        {
            case ClientHelloMessage hello:
                _codec.Encode(envelope, hello, _frameWriter);
                break;
            case ClientJoinRequestMessage joinRequest:
                _codec.Encode(envelope, joinRequest, _frameWriter);
                break;
            case PlayerCommandMessage command:
                _codec.Encode(envelope, command, _frameWriter);
                break;
            case PlayerActionMessage action:
                _codec.Encode(envelope, action, _frameWriter);
                break;
            case ClientWorldResyncRequestMessage resync:
                _codec.Encode(envelope, resync, _frameWriter);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported client message type '{message.GetType().Name}' for remote client encoding.");
        }

        return _frameWriter.CompleteFrame(_framer);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await ProcessInboundOnceAsync(cancellationToken))
            {
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ProtocolVersionMismatchException ex)
        {
            DisconnectReason = ex.Message;
            if (_serverHello.TrySetException(ex))
            {
                // Mark the fault observed: nobody awaits it once ConnectAsync has returned.
                _ = _serverHello.Task.Exception;
            }
        }
        catch (Exception ex)
        {
            // A malformed frame or a transport error; previously this faulted the loop task and
            // resurfaced from DisposeAsync.
            DisconnectReason = $"Connection lost: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsConnected = false;
            _serverHello.TrySetResult(null);
            NetworkDiagnosticsLog.Write(
                "client.disconnect",
                $"endpoint={Options.EndpointLabel} connection={_connection.ConnectionId} session={Options.SessionId} reason={DisconnectReason ?? "closed"}");
        }
    }

    private void LogInbound(TransportEnvelope envelope, IServerToClientMessage message)
    {
        if (!NetworkDiagnosticsLog.IsEnabled)
        {
            return;
        }

        string detail = message switch
        {
            JoinAcceptedMessage accepted => NetworkDiagnosticsLog.DescribeJoin(accepted),
            JoinRejectedMessage rejected => $"reason={rejected.Reason}",
            ServerSnapshotMessage snapshot => NetworkDiagnosticsLog.DescribeSnapshot(snapshot.Snapshot),
            ServerActionMessage action => $"session={action.Result.SessionId} actionSeq={action.Result.ActionSequence} accepted={action.Result.Accepted} payload={action.Result.Payload?.GetType().Name ?? "none"}",
            ServerHelloMessage hello => $"server={hello.ServerName} protocol={hello.ProtocolVersion} tick={hello.CurrentServerTick}",
            _ => string.Empty
        };

        string log = $"endpoint={Options.EndpointLabel} seq={envelope.Sequence} kind={message.MessageKind} queued={_inboundMessages.Count} {detail}";
        if (message is ServerSnapshotMessage)
        {
            NetworkDiagnosticsLog.WriteEvery(
                $"client.rx.snapshot.{Options.SessionId}",
                TimeSpan.FromSeconds(1),
                "client.rx",
                log);
            return;
        }

        NetworkDiagnosticsLog.Write("client.rx", log);
    }

    private void LogOutbound(IClientToServerMessage message, int byteCount, double elapsedMilliseconds)
    {
        if (!NetworkDiagnosticsLog.IsEnabled)
        {
            return;
        }

        if (message is PlayerCommandMessage)
        {
            // Commands are rate-limited; build the log line only when a limiter lets it through.
            bool logCommand = NetworkDiagnosticsLog.ShouldLogEvery(_commandLogKey, TimeSpan.FromSeconds(1));
            bool logSlow = elapsedMilliseconds > 4d
                && NetworkDiagnosticsLog.ShouldLogEvery(_slowCommandLogKey, TimeSpan.FromSeconds(1));
            if (!logCommand && !logSlow)
            {
                return;
            }

            string commandLog = DescribeOutbound(message, byteCount, elapsedMilliseconds);
            if (logCommand)
            {
                NetworkDiagnosticsLog.Write("client.tx", commandLog);
            }

            if (logSlow)
            {
                NetworkDiagnosticsLog.Write("client.tx.slow", commandLog);
            }

            return;
        }

        NetworkDiagnosticsLog.Write("client.tx", DescribeOutbound(message, byteCount, elapsedMilliseconds));
    }

    private string DescribeOutbound(IClientToServerMessage message, int byteCount, double elapsedMilliseconds)
    {
        string detail = message switch
        {
            ClientJoinRequestMessage join => $"session={join.RequestedSessionId} name={join.DisplayName}",
            PlayerCommandMessage command => $"session={command.CommandFrame.SessionId.Value} commandSeq={command.CommandFrame.Sequence}",
            PlayerActionMessage action => $"session={action.SessionId.Value} actionSeq={action.ActionSequence} payload={action.Payload.GetType().Name}",
            ClientHelloMessage hello => $"client={hello.ClientName}",
            _ => string.Empty
        };

        return $"endpoint={Options.EndpointLabel} kind={message.MessageKind} bytes={byteCount} sendMs={elapsedMilliseconds:0.00} {detail}";
    }
}

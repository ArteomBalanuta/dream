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
    private long _sentCommandCount;
    private ulong _nextOutboundSequence = 1;

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
    }

    public RemoteClientOptions Options { get; }

    public string SessionId => Options.SessionId;

    public bool IsConnected { get; private set; } = true;

    public static async ValueTask<RemoteClientSession> ConnectAsync(
        RemoteClientOptions options,
        IClientTransport? transport = null,
        IProtocolCodec? codec = null,
        IMessageFramer? framer = null,
        CancellationToken cancellationToken = default)
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
        NetworkDiagnosticsLog.Write(
            "client.connect.ready",
            $"endpoint={options.EndpointLabel} connection={connection.ConnectionId} session={options.SessionId}");
        return session;
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
        while (_receiveBuffer.TryReadFrame(_framer, out byte[]? payload))
        {
            if (payload == null)
            {
                continue;
            }

            DecodedProtocolMessage decoded = _codec.Decode(payload);
            IServerToClientMessage serverMessage = decoded.Message as IServerToClientMessage
                ?? throw new InvalidOperationException(
                    $"Received non-server message kind '{decoded.Message.MessageKind}' on client transport.");
            _inboundMessages.Enqueue(serverMessage);
            LogInbound(decoded.Envelope, serverMessage);
        }

        return true;
    }

    public void StartBackgroundReceive()
    {
        _receiveLoopTask ??= Task.Run(() => ReceiveLoopAsync(_shutdown.Token));
    }

    public async ValueTask DisposeAsync()
    {
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

        _shutdown.Dispose();
        _sendLock.Dispose();
        await _connection.DisposeAsync();
    }

    private async ValueTask SendMessageAsync(IClientToServerMessage message, CancellationToken cancellationToken)
    {
        byte[] payload = EncodeClientMessage(message);
        byte[] framed = _framer.Frame(payload);
        Stopwatch? stopwatch = NetworkDiagnosticsLog.IsEnabled ? Stopwatch.StartNew() : null;
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _connection.SendAsync(framed, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
            stopwatch?.Stop();
        }

        LogOutbound(message, framed.Length, stopwatch?.Elapsed.TotalMilliseconds ?? 0d);
    }

    private byte[] EncodeClientMessage(IClientToServerMessage message)
    {
        TransportEnvelope envelope = new(
            TransportEnvelope.CurrentProtocolVersion,
            message.MessageKind,
            _nextOutboundSequence++);

        return message switch
        {
            ClientHelloMessage hello => _codec.Encode(envelope, hello),
            ClientJoinRequestMessage joinRequest => _codec.Encode(envelope, joinRequest),
            PlayerCommandMessage command => _codec.Encode(envelope, command),
            PlayerActionMessage action => _codec.Encode(envelope, action),
            ClientWorldResyncRequestMessage resync => _codec.Encode(envelope, resync),
            _ => throw new InvalidOperationException(
                $"Unsupported client message type '{message.GetType().Name}' for remote client encoding.")
        };
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
        finally
        {
            IsConnected = false;
            NetworkDiagnosticsLog.Write(
                "client.disconnect",
                $"endpoint={Options.EndpointLabel} connection={_connection.ConnectionId} session={Options.SessionId}");
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

        string detail = message switch
        {
            ClientJoinRequestMessage join => $"session={join.RequestedSessionId} name={join.DisplayName}",
            PlayerCommandMessage command => $"session={command.CommandFrame.SessionId.Value} commandSeq={command.CommandFrame.Sequence}",
            PlayerActionMessage action => $"session={action.SessionId.Value} actionSeq={action.ActionSequence} payload={action.Payload.GetType().Name}",
            ClientHelloMessage hello => $"client={hello.ClientName}",
            _ => string.Empty
        };

        string log = $"endpoint={Options.EndpointLabel} kind={message.MessageKind} bytes={byteCount} sendMs={elapsedMilliseconds:0.00} {detail}";
        if (message is PlayerCommandMessage)
        {
            NetworkDiagnosticsLog.WriteEvery(
                $"client.tx.command.{Options.SessionId}",
                TimeSpan.FromSeconds(1),
                "client.tx",
                log);
            if (elapsedMilliseconds > 4d)
            {
                NetworkDiagnosticsLog.WriteEvery(
                    $"client.tx.command.slow.{Options.SessionId}",
                    TimeSpan.FromSeconds(1),
                    "client.tx.slow",
                    log);
            }

            return;
        }

        NetworkDiagnosticsLog.Write("client.tx", log);
    }
}

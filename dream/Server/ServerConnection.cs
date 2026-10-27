using System.Diagnostics;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Simulation.World;

namespace Dream.Server;

public sealed class ServerConnection : IAsyncDisposable
{
    private readonly ITransportConnection _transport;
    private readonly DedicatedServerHost _host;
    private readonly IProtocolCodec _codec;
    private readonly IMessageFramer _framer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _receiveChunk = new byte[16 * 1024];
    private readonly FrameReceiveBuffer _receiveBuffer = new();
    private ulong _nextOutboundSequence = 1;

    // Ordered outbound queue drained by one writer task per connection. The tick loop only
    // enqueues, so one slow client can no longer stall the authoritative simulation. A snapshot
    // queued directly behind another unsent snapshot is merged into it (latest actor state,
    // concatenated world mutations and damage), so a lagging client's backlog stays bounded.
    public const int MaxPendingOutboundMessages = 512;
    private readonly object _outboundSync = new();
    private readonly LinkedList<IServerToClientMessage> _outbound = new();
    private readonly SemaphoreSlim _outboundSignal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _writerShutdown = new();
    private Task? _writerTask;
    private bool _writing;
    private long _coalescedSnapshots;

    public ServerConnection(
        ITransportConnection transport,
        DedicatedServerHost host,
        IProtocolCodec codec,
        IMessageFramer framer)
    {
        _transport = transport;
        _host = host;
        _codec = codec;
        _framer = framer;
    }

    public string ConnectionId => _transport.ConnectionId;

    public int PendingOutboundCount
    {
        get
        {
            lock (_outboundSync)
            {
                return _outbound.Count;
            }
        }
    }

    public long CoalescedSnapshots => Interlocked.Read(ref _coalescedSnapshots);

    // Set when the writer failed or the backlog overflowed; the service drops faulted connections.
    public bool IsFaulted { get; private set; }

    public void EnqueueSnapshot(ServerSnapshotMessage snapshotMessage)
    {
        ArgumentNullException.ThrowIfNull(snapshotMessage);
        lock (_outboundSync)
        {
            if (_outbound.Last?.Value is ServerSnapshotMessage pending &&
                ServerSnapshotEnvelope.TryCoalesce(pending.Snapshot, snapshotMessage.Snapshot, out ServerSnapshotEnvelope merged))
            {
                _outbound.Last.Value = new ServerSnapshotMessage(merged);
                _coalescedSnapshots++;
                return;
            }
        }

        Enqueue(snapshotMessage);
    }

    public void Enqueue(IServerToClientMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_outboundSync)
        {
            if (IsFaulted)
            {
                return;
            }

            if (_outbound.Count >= MaxPendingOutboundMessages)
            {
                Fault($"outbound backlog exceeded {MaxPendingOutboundMessages} messages");
                return;
            }

            _outbound.AddLast(message);
            _writerTask ??= Task.Run(() => WriteLoopAsync(_writerShutdown.Token));
        }

        _outboundSignal.Release();
    }

    public async ValueTask<bool> FlushOutboundAsync(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_outboundSync)
            {
                if (IsFaulted || (_outbound.Count == 0 && !_writing))
                {
                    return !IsFaulted;
                }
            }

            await Task.Delay(1);
        }

        return false;
    }

    public async ValueTask<bool> ProcessIncomingAsync(CancellationToken cancellationToken = default)
    {
        int bytesRead = await _transport.ReceiveAsync(_receiveChunk, cancellationToken);
        if (bytesRead <= 0)
        {
            return false;
        }

        _receiveBuffer.Append(_receiveChunk.AsSpan(0, bytesRead));
        FlushInboundFrames();
        return !IsFaulted;
    }

    public ValueTask SendSnapshotAsync(ServerSnapshotMessage snapshotMessage, CancellationToken cancellationToken = default)
    {
        return SendMessageAsync(snapshotMessage, cancellationToken);
    }

    public ValueTask SendActionAsync(ServerActionMessage actionMessage, CancellationToken cancellationToken = default)
    {
        return SendMessageAsync(actionMessage, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _writerShutdown.Cancel();
        if (_writerTask != null)
        {
            try
            {
                await _writerTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _writerShutdown.Dispose();
        _sendLock.Dispose();
        await _transport.DisposeAsync();
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _outboundSignal.WaitAsync(cancellationToken);
                while (true)
                {
                    IServerToClientMessage message;
                    lock (_outboundSync)
                    {
                        if (_outbound.First == null)
                        {
                            break;
                        }

                        message = _outbound.First.Value;
                        _outbound.RemoveFirst();
                        _writing = true;
                    }

                    try
                    {
                        await SendMessageAsync(message, cancellationToken);
                    }
                    finally
                    {
                        lock (_outboundSync)
                        {
                            _writing = false;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (_outboundSync)
            {
                Fault($"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // Caller holds _outboundSync.
    private void Fault(string reason)
    {
        if (IsFaulted)
        {
            return;
        }

        IsFaulted = true;
        _outbound.Clear();
        NetworkDiagnosticsLog.Write("server.connection.fault", $"connection={ConnectionId} reason={reason}");
    }

    private void FlushInboundFrames()
    {
        while (_receiveBuffer.TryReadFrame(_framer, out byte[]? payload))
        {
            if (payload == null)
            {
                continue;
            }

            DecodedProtocolMessage decoded = _codec.Decode(payload);
            IClientToServerMessage clientMessage = decoded.Message as IClientToServerMessage
                ?? throw new InvalidOperationException(
                    $"Received non-client message kind '{decoded.Message.MessageKind}' on server connection '{ConnectionId}'.");
            LogInbound(decoded.Envelope, clientMessage);
            IReadOnlyList<IServerToClientMessage> replies = _host.HandleClientMessage(ConnectionId, clientMessage);
            foreach (IServerToClientMessage reply in replies)
            {
                Enqueue(reply);
            }
        }
    }

    private async ValueTask SendMessageAsync(IServerToClientMessage message, CancellationToken cancellationToken)
    {
        byte[] payload = EncodeServerMessage(message);
        byte[] framed = _framer.Frame(payload);
        Stopwatch? stopwatch = NetworkDiagnosticsLog.IsEnabled ? Stopwatch.StartNew() : null;
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _transport.SendAsync(framed, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
            stopwatch?.Stop();
        }

        LogOutbound(message, framed.Length, stopwatch?.Elapsed.TotalMilliseconds ?? 0d);
    }

    private byte[] EncodeServerMessage(IServerToClientMessage message)
    {
        TransportEnvelope envelope = new(
            TransportEnvelope.CurrentProtocolVersion,
            message.MessageKind,
            _nextOutboundSequence++);

        return message switch
        {
            ServerHelloMessage serverHello => _codec.Encode(envelope, serverHello),
            JoinAcceptedMessage joinAccepted => _codec.Encode(envelope, joinAccepted),
            JoinRejectedMessage joinRejected => _codec.Encode(envelope, joinRejected),
            ServerSnapshotMessage snapshot => _codec.Encode(envelope, snapshot),
            ServerActionMessage action => _codec.Encode(envelope, action),
            _ => throw new InvalidOperationException(
                $"Unsupported server message type '{message.GetType().Name}' for connection encoding.")
        };
    }

    private void LogInbound(TransportEnvelope envelope, IClientToServerMessage message)
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

        string log = $"connection={ConnectionId} seq={envelope.Sequence} kind={message.MessageKind} {detail}";
        if (message is PlayerCommandMessage)
        {
            NetworkDiagnosticsLog.WriteEvery(
                $"server.rx.command.{ConnectionId}",
                TimeSpan.FromSeconds(1),
                "server.rx",
                log);
            return;
        }

        NetworkDiagnosticsLog.Write("server.rx", log);
    }

    private void LogOutbound(IServerToClientMessage message, int byteCount, double elapsedMilliseconds)
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
            ServerHelloMessage hello => $"server={hello.ServerName}",
            _ => string.Empty
        };

        string log = $"connection={ConnectionId} kind={message.MessageKind} bytes={byteCount} sendMs={elapsedMilliseconds:0.00} {detail}";
        if (message is ServerSnapshotMessage)
        {
            NetworkDiagnosticsLog.WriteEvery(
                $"server.tx.snapshot.{ConnectionId}",
                TimeSpan.FromSeconds(1),
                "server.tx",
                log);
            return;
        }

        NetworkDiagnosticsLog.Write("server.tx", log);
    }
}

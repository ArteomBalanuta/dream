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
    // One reusable pooled buffer for every outbound frame; only touched while _sendLock is held,
    // including its Dispose in DisposeAsync.
    private readonly PooledFrameWriter _frameWriter = new();
    // Assigned under _sendLock together with the encode, so sequences follow wire order. Public
    // Send*Async calls and the writer task both go through that lock.
    private ulong _nextOutboundSequence = 1;
    // 0 until DisposeAsync starts, then 1. Checked by senders before and after taking _sendLock.
    private int _disposeState;
    private readonly string _snapshotTxLogKey;
    private readonly string _commandRxLogKey;

    // Ordered outbound queue drained by one writer task per connection. The tick loop only
    // enqueues, so one slow client can no longer stall the authoritative simulation. A snapshot
    // queued directly behind another unsent snapshot is merged into it (latest actor state,
    // concatenated world mutations and damage), so a lagging client's backlog stays bounded.
    // A broadcast snapshot carries the SharedEncodedPayload of its tick, so its JSON is serialized
    // once for all connections; a merged snapshot drops it and is encoded for this connection.
    public const int MaxPendingOutboundMessages = 512;
    private readonly object _outboundSync = new();
    private readonly LinkedList<OutboundEntry> _outbound = new();
    // Detached queue nodes kept for reuse, so steady-state enqueues allocate nothing. Bounded by
    // MaxPendingOutboundMessages because a node is only freed after it was queued.
    private readonly Stack<LinkedListNode<OutboundEntry>> _freeNodes = new();
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
        _snapshotTxLogKey = $"server.tx.snapshot.{transport.ConnectionId}";
        _commandRxLogKey = $"server.rx.command.{transport.ConnectionId}";
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
        EnqueueSnapshotEntry(new OutboundEntry(snapshotMessage, Shared: null));
    }

    // Queues a snapshot whose payload JSON is shared with the other connections it is broadcast
    // to; the writer wraps those bytes in this connection's envelope instead of re-encoding.
    public void EnqueueSnapshot(ServerSnapshotMessage snapshotMessage, SharedEncodedPayload sharedPayload)
    {
        ArgumentNullException.ThrowIfNull(snapshotMessage);
        ArgumentNullException.ThrowIfNull(sharedPayload);
        if (!ReferenceEquals(sharedPayload.Message, snapshotMessage))
        {
            throw new ArgumentException("The shared payload was encoded from a different snapshot message.", nameof(sharedPayload));
        }

        EnqueueSnapshotEntry(new OutboundEntry(snapshotMessage, sharedPayload));
    }

    public void Enqueue(IServerToClientMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Enqueue(new OutboundEntry(message, Shared: null));
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

    // Writes straight to the transport, bypassing the queue. These share the sequence counter and
    // _sendLock with the writer task, so frames from both stay in sequence order on the wire.
    public ValueTask SendSnapshotAsync(ServerSnapshotMessage snapshotMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshotMessage);
        return SendMessageAsync(snapshotMessage, sharedPayload: null, cancellationToken);
    }

    public ValueTask SendActionAsync(ServerActionMessage actionMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actionMessage);
        return SendMessageAsync(actionMessage, sharedPayload: null, cancellationToken);
    }

    // Sends a rejection straight to the transport ahead of anything still queued; used just
    // before the service closes a connection it cannot serve.
    public ValueTask SendJoinRejectedAsync(JoinRejectedMessage rejection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        return SendMessageAsync(rejection, sharedPayload: null, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _writerShutdown.Cancel();
        Task? writerTask;
        lock (_outboundSync)
        {
            writerTask = _writerTask;
        }

        if (writerTask != null)
        {
            try
            {
                await writerTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        // The writer task has exited, but a direct Send*Async call does not run on it and may
        // still be writing the pooled frame buffer to the transport. Take _sendLock so the buffer
        // goes back to ArrayPool<byte>.Shared only after that send ends. Senders that get the lock
        // after this see _disposeState and throw ObjectDisposedException instead of encoding into
        // the returned buffer.
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
        _writerShutdown.Dispose();
        await _transport.DisposeAsync();
    }

    private void EnqueueSnapshotEntry(OutboundEntry entry)
    {
        ServerSnapshotMessage snapshotMessage = (ServerSnapshotMessage)entry.Message;
        lock (_outboundSync)
        {
            // A merged snapshot no longer matches any shared payload, so it is encoded for this
            // connection alone. That only happens while this client lags behind the tick rate.
            LinkedListNode<OutboundEntry>? last = _outbound.Last;
            if (last?.Value.Message is ServerSnapshotMessage pending &&
                ServerSnapshotEnvelope.TryCoalesce(pending.Snapshot, snapshotMessage.Snapshot, out ServerSnapshotEnvelope merged))
            {
                last.Value = new OutboundEntry(new ServerSnapshotMessage(merged), Shared: null);
                _coalescedSnapshots++;
                return;
            }
        }

        Enqueue(entry);
    }

    private void Enqueue(OutboundEntry entry)
    {
        lock (_outboundSync)
        {
            // After DisposeAsync has started no writer may be started, and none would drain the queue.
            if (IsFaulted || Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            if (_outbound.Count >= MaxPendingOutboundMessages)
            {
                Fault($"outbound backlog exceeded {MaxPendingOutboundMessages} messages");
                return;
            }

            if (_freeNodes.TryPop(out LinkedListNode<OutboundEntry>? node))
            {
                node.Value = entry;
                _outbound.AddLast(node);
            }
            else
            {
                _outbound.AddLast(entry);
            }

            _writerTask ??= Task.Run(() => WriteLoopAsync(_writerShutdown.Token));
        }

        _outboundSignal.Release();
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
                    OutboundEntry entry;
                    lock (_outboundSync)
                    {
                        LinkedListNode<OutboundEntry>? first = _outbound.First;
                        if (first == null)
                        {
                            break;
                        }

                        entry = first.Value;
                        _outbound.Remove(first);
                        first.Value = default;
                        _freeNodes.Push(first);
                        _writing = true;
                    }

                    try
                    {
                        await SendMessageAsync(entry.Message, entry.Shared, cancellationToken);
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
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            // DisposeAsync started between the loop's dequeue and its send; not a transport error.
        }
        catch (Exception ex)
        {
            lock (_outboundSync)
            {
                Fault($"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // Shared is set only for a snapshot broadcast unchanged; a merged snapshot is encoded alone.
    private readonly record struct OutboundEntry(IServerToClientMessage Message, SharedEncodedPayload? Shared);

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
            catch (Exception ex) when (ProtocolVersionMismatchException.ReadForeignProtocolVersion(payload) is int clientVersion)
            {
                throw ProtocolVersionMismatchException.FromClient(clientVersion, ex);
            }

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

    private async ValueTask SendMessageAsync(
        IServerToClientMessage message,
        SharedEncodedPayload? sharedPayload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        Stopwatch? stopwatch = NetworkDiagnosticsLog.IsEnabled ? Stopwatch.StartNew() : null;
        int frameLength;
        // The lock is taken before the sequence is assigned and the frame encoded, so a public
        // Send*Async call racing the writer task can neither lose an increment nor put a higher
        // sequence on the wire ahead of a lower one.
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            // Disposal may have started while this call waited for the lock; the frame writer is
            // then gone (or about to be), so fail cleanly rather than touch its buffer.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            ReadOnlyMemory<byte> frame = EncodeServerFrame(message, sharedPayload);
            frameLength = frame.Length;
            // frame aliases _frameWriter's pooled buffer. Passing it to the transport is safe only
            // because the send is awaited here, inside _sendLock, before the next encode reuses the
            // buffer; transports must not keep the memory after SendAsync completes.
            await _transport.SendAsync(frame, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
            stopwatch?.Stop();
        }

        LogOutbound(message, frameLength, stopwatch);
    }

    // Caller holds _sendLock. The sequence is consumed only once the frame is fully encoded, so a
    // message that fails to encode leaves no gap.
    private ReadOnlyMemory<byte> EncodeServerFrame(IServerToClientMessage message, SharedEncodedPayload? sharedPayload)
    {
        TransportEnvelope envelope = new(
            TransportEnvelope.CurrentProtocolVersion,
            message.MessageKind,
            _nextOutboundSequence);

        _frameWriter.BeginFrame(_framer);
        if (sharedPayload != null)
        {
            // The first writer to get here encodes the snapshot for every connection sharing it.
            _codec.WriteEnvelope(envelope, sharedPayload.Bytes, _frameWriter);
        }
        else
        {
            switch (message)
            {
                case ServerHelloMessage serverHello:
                    _codec.Encode(envelope, serverHello, _frameWriter);
                    break;
                case JoinAcceptedMessage joinAccepted:
                    _codec.Encode(envelope, joinAccepted, _frameWriter);
                    break;
                case JoinRejectedMessage joinRejected:
                    _codec.Encode(envelope, joinRejected, _frameWriter);
                    break;
                case ServerSnapshotMessage snapshot:
                    _codec.Encode(envelope, snapshot, _frameWriter);
                    break;
                case ServerActionMessage action:
                    _codec.Encode(envelope, action, _frameWriter);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported server message type '{message.GetType().Name}' for connection encoding.");
            }
        }

        ReadOnlyMemory<byte> frame = _frameWriter.CompleteFrame(_framer);
        _nextOutboundSequence++;
        return frame;
    }

    private void LogInbound(TransportEnvelope envelope, IClientToServerMessage message)
    {
        if (!NetworkDiagnosticsLog.IsEnabled)
        {
            return;
        }

        // Commands arrive every client tick; format one only when the rate limiter lets it through.
        if (message is PlayerCommandMessage &&
            !NetworkDiagnosticsLog.ShouldLogEvery(_commandRxLogKey, TimeSpan.FromSeconds(1)))
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

        NetworkDiagnosticsLog.Write(
            "server.rx",
            $"connection={ConnectionId} seq={envelope.Sequence} kind={message.MessageKind} {detail}");
    }

    private void LogOutbound(IServerToClientMessage message, int byteCount, Stopwatch? stopwatch)
    {
        if (!NetworkDiagnosticsLog.IsEnabled)
        {
            return;
        }

        // Snapshots go out every tick on every connection; describe one only when the rate limiter
        // lets it through.
        if (message is ServerSnapshotMessage &&
            !NetworkDiagnosticsLog.ShouldLogEvery(_snapshotTxLogKey, TimeSpan.FromSeconds(1)))
        {
            return;
        }

        double elapsedMilliseconds = stopwatch?.Elapsed.TotalMilliseconds ?? 0d;
        string detail = message switch
        {
            JoinAcceptedMessage accepted => NetworkDiagnosticsLog.DescribeJoin(accepted),
            JoinRejectedMessage rejected => $"reason={rejected.Reason}",
            ServerSnapshotMessage snapshot => NetworkDiagnosticsLog.DescribeSnapshot(snapshot.Snapshot),
            ServerActionMessage action => $"session={action.Result.SessionId} actionSeq={action.Result.ActionSequence} accepted={action.Result.Accepted} payload={action.Result.Payload?.GetType().Name ?? "none"}",
            ServerHelloMessage hello => $"server={hello.ServerName}",
            _ => string.Empty
        };

        NetworkDiagnosticsLog.Write(
            "server.tx",
            $"connection={ConnectionId} kind={message.MessageKind} bytes={byteCount} sendMs={elapsedMilliseconds:0.00} {detail}");
    }
}

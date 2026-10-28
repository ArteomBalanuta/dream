using System.Diagnostics;
using System.Text.Json;
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
    // The framer's write cap (int.MaxValue when it has none): a snapshot whose encoded payload
    // passes it is sent without its deltas instead of faulting the writer (EncodeServerFrame).
    private readonly int _maxWritePayloadBytes;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _receiveChunk = new byte[16 * 1024];
    private readonly FrameReceiveBuffer _receiveBuffer = new();
    // One reusable pooled buffer for every outbound frame; only touched while _sendLock is held,
    // including its Dispose in DisposeAsync.
    private readonly PooledFrameWriter _frameWriter = new();
    // Assigned under _sendLock together with the encode, so sequences follow wire order. Public
    // Send*Async calls and the writer task both go through that lock.
    private ulong _nextOutboundSequence = 1;
    // SCALE-2: the actor frame (ActorDeltaContext.Sequence) of the last snapshot this connection
    // put on the wire, or 0 when that snapshot carried no frame (a resync, a test-built envelope)
    // or none was sent yet. TCP delivers in order, so it is exactly the frame the client holds: a
    // delta frame whose base is anything else goes out as its keyframe twin instead. That covers a
    // join between keyframes, merged (coalesced) snapshots, a resync in between and a dropped
    // oversized delta, without the client ever having to ask. Under _sendLock, like the sequence.
    private ulong _lastActorFrameSequence;
    private long _actorKeyframesForced;
    // 0 until DisposeAsync starts, then 1. Checked by senders before and after taking _sendLock.
    private int _disposeState;
    private readonly string _snapshotTxLogKey;
    private readonly string _commandRxLogKey;
    // 0 until the transport is disposed, then 1; a faulted connection disposes it early.
    private int _transportDisposeState;

    // Liveness. Timestamps come from _timeProvider; a limit of 0 turns its check off. The receive
    // loop writes _lastInboundTimestamp on every complete frame, the sender brackets each socket
    // write with _sendInFlight, and CheckLiveness (the service's tick thread) reads both.
    private readonly ServerConnectionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly long _idleTimeoutTimestampTicks;
    private readonly long _sendStallTimestampTicks;
    private long _lastInboundTimestamp;
    private long _sendStartedTimestamp;
    // The current write's extra stall allowance for its size (frame bytes over
    // SendStallMinimumBytesPerSecond), in timestamp ticks; written before _sendInFlight is set.
    private long _sendStallAllowanceTicks;
    private int _sendInFlight;

    // Ordered outbound queue drained by one writer task per connection. The tick loop only
    // enqueues, so one slow client can no longer stall the authoritative simulation. A snapshot
    // queued directly behind another unsent snapshot is merged into it (latest actor state,
    // concatenated world mutations and damage), so a lagging client's backlog stays bounded.
    // The merge appends into a pooled ServerSnapshotCoalesceBuffer, so each merge costs only the
    // new snapshot's deltas and allocates nothing after warm-up; the writer builds the merged
    // message when it dequeues the entry. Past the options' delta caps the merged deltas are
    // dropped and the connection is flagged for a full world resync instead.
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
    // Merge buffers no longer referenced by a queued or in-flight entry. Guarded by _outboundSync.
    private readonly Stack<ServerSnapshotCoalesceBuffer> _freeCoalesceBuffers = new();
    // Set when a merge overflowed the delta caps; taken by the service, which requests a resync.
    private int _overflowResyncPending;
    private long _overflowResyncs;
    private long _oversizedSnapshotFrames;
    // Always-on traffic counters (frames include the length prefix). Updated with Interlocked by
    // the writer task and the receive loop, read by DedicatedServerService metrics.
    private long _bytesSent;
    private long _framesSent;
    private long _bytesReceived;
    private long _framesReceived;

    // SCALE-1 chunked world transfer. A queued JoinAccepted with a world, or a queued world
    // snapshot (a resync), is sent as its carrier (the message without the world, with a transfer
    // id) followed by server.world-chunk frames of about WorldChunkBytes, encoded here on the
    // writer task from the capture the tick took. While a transfer runs, the writer alternates one
    // chunk and one queued message, so the regular traffic keeps flowing and the backlog stays
    // bounded. The capture is released after the last chunk. Writer task only (and DisposeAsync
    // once the writer has ended).
    // Releasing on the final chunk is safe because nothing reads the capture after it: the final
    // chunk is fully encoded into _chunkPayloadWriter (WriteNextChunk returns only after the
    // fragment's last item was written) before ReleaseCapture runs, the envelope then copies those
    // bytes, and the sender is complete, so no later WriteNextChunk can touch the capture. The
    // runtime gave the capture to this transfer alone (releaseCaptureWhenDone), so no other
    // connection or tick reads it either.
    private WorldTransferSender? _activeTransfer;
    // World transfers queued or running: +1 when a message that will become a transfer is queued,
    // -1 when its final chunk is sent; 0 again once the connection faults or its writer ends.
    // Read by the service (HasWorldTransferPending) so it does not pile overflow resyncs behind a
    // transfer that is still going out. Guarded by _outboundSync.
    private int _pendingWorldTransfers;
    private long _backToBackWorldTransfers;
    // Set while an overflow resync waits for a transfer to finish (DeferOverflowResync).
    private int _overflowResyncDeferred;
    private bool _chunkDue;
    private int _nextWorldTransferId;
    private long _worldTransfers;
    private long _worldChunksSent;
    // The chunk payload is written here first, then wrapped in its envelope in _frameWriter.
    // Only touched while _sendLock is held, like _frameWriter.
    private readonly PooledFrameWriter _chunkPayloadWriter = new();
    private static readonly JsonWriterOptions ChunkPayloadWriterOptions = new();

    public ServerConnection(
        ITransportConnection transport,
        DedicatedServerHost host,
        IProtocolCodec codec,
        IMessageFramer framer)
        : this(transport, host, codec, framer, ServerConnectionOptions.Default)
    {
    }

    public ServerConnection(
        ITransportConnection transport,
        DedicatedServerHost host,
        IProtocolCodec codec,
        IMessageFramer framer,
        ServerConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        _transport = transport;
        _host = host;
        _codec = codec;
        _framer = framer;
        _maxWritePayloadBytes = framer is LengthPrefixedMessageFramer lengthPrefixed
            ? lengthPrefixed.MaxWritePayloadBytes
            : int.MaxValue;
        _options = options;
        _timeProvider = options.TimeProvider;
        _idleTimeoutTimestampTicks = ToTimestampTicks(options.IdleTimeout, _timeProvider);
        _sendStallTimestampTicks = ToTimestampTicks(options.SendStallTimeout, _timeProvider);
        _lastInboundTimestamp = _timeProvider.GetTimestamp();
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

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public long FramesSent => Interlocked.Read(ref _framesSent);

    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    // World transfers started (joins and resyncs) and world chunk frames written.
    public long WorldTransfers => Interlocked.Read(ref _worldTransfers);

    public long WorldChunksSent => Interlocked.Read(ref _worldChunksSent);

    // True while a world transfer (join or resync) is queued or still going out.
    public bool HasWorldTransferPending
    {
        get
        {
            lock (_outboundSync)
            {
                return _pendingWorldTransfers > 0;
            }
        }
    }

    // World transfers queued while another one was queued or running, so they went out back to
    // back: each is a full world sent right after another.
    public long BackToBackWorldTransfers => Interlocked.Read(ref _backToBackWorldTransfers);

    // The byte budget of one chunk's world fragment (tests shrink it to force many chunks).
    internal int WorldChunkBytes { get; set; } = WorldChunkMessage.DefaultChunkBytes;

    // Set when the writer failed, the backlog overflowed or a liveness check failed; the service
    // drops faulted connections.
    public bool IsFaulted { get; private set; }

    // Why the connection faulted; None while it has not.
    public ServerConnectionCloseReason CloseReason { get; private set; }

    public ServerConnectionOptions Options => _options;

    // Snapshots whose deltas were dropped for a world resync: merges past the caps, broadcasts
    // past them (FlagOverflowResync) and snapshots too large for the frame cap (EncodeServerFrame).
    public long OverflowResyncs => Interlocked.Read(ref _overflowResyncs);

    // Snapshots the writer sent without their deltas because the encoded frame passed the
    // framer's write cap.
    public long OversizedSnapshotFrames => Interlocked.Read(ref _oversizedSnapshotFrames);

    // Snapshots sent as their actor frame's keyframe twin because this connection did not hold the
    // frame's delta base (first snapshot after a join, merged snapshots, a resync in between).
    public long ActorKeyframesForced => Interlocked.Read(ref _actorKeyframesForced);

    // Called by the service once per advance, on its tick thread. Faults the connection when no
    // complete frame arrived within IdleTimeout, or the current socket write has been running for
    // longer than SendStallTimeout. Returns IsFaulted. Allocation-free while the connection is live.
    public bool CheckLiveness()
    {
        if (IsFaulted)
        {
            return true;
        }

        long now = _timeProvider.GetTimestamp();
        ServerConnectionCloseReason reason = ServerConnectionCloseReason.None;
        long sendStallLimitTicks = 0;
        if (_idleTimeoutTimestampTicks > 0 &&
            now - Volatile.Read(ref _lastInboundTimestamp) > _idleTimeoutTimestampTicks)
        {
            reason = ServerConnectionCloseReason.IdleTimeout;
        }
        else if (_sendStallTimestampTicks > 0 && Volatile.Read(ref _sendInFlight) != 0)
        {
            // A large frame gets extra time in proportion to its size, so a slow link still
            // making progress on a full world resync is not cut off.
            sendStallLimitTicks = SaturatingAdd(_sendStallTimestampTicks, Volatile.Read(ref _sendStallAllowanceTicks));
            if (now - Volatile.Read(ref _sendStartedTimestamp) > sendStallLimitTicks)
            {
                reason = ServerConnectionCloseReason.SendStall;
            }
        }

        if (reason == ServerConnectionCloseReason.None)
        {
            return false;
        }

        lock (_outboundSync)
        {
            Fault(
                reason == ServerConnectionCloseReason.IdleTimeout
                    ? $"no inbound frame for {_options.IdleTimeout.TotalSeconds:0.###} s"
                    : $"socket write stalled for {sendStallLimitTicks / (double)_timeProvider.TimestampFrequency:0.###} s",
                reason);
        }

        return true;
    }

    // True once after a merge overflowed the delta caps: the caller should get this connection a
    // full world resync, because the merged deltas it would have received were dropped.
    public bool TryTakeOverflowResync()
    {
        return Interlocked.Exchange(ref _overflowResyncPending, 0) != 0;
    }

    // The service sent this connection a broadcast snapshot without its deltas (they passed the
    // caps in one advance), so it needs a full world resync like an overflowing merge. Taken by
    // TryTakeOverflowResync on the same path, so the pending-transfer deferral applies.
    public void FlagOverflowResync()
    {
        Interlocked.Increment(ref _overflowResyncs);
        Volatile.Write(ref _overflowResyncPending, 1);
    }

    // The snapshot without its world mutations, damage events and world checksum: actors,
    // interaction mode and accepted command sequences only. Sent when the deltas are dropped for a
    // world resync; the checksum goes too, because it covers the dropped deltas and would only
    // make the client ask for a second resync of its own.
    internal static ServerSnapshotMessage WithoutDeltas(ServerSnapshotMessage message)
    {
        return message with
        {
            Snapshot = message.Snapshot with
            {
                WorldMutations = Array.Empty<WorldMutationDelta>(),
                DamageEvents = Array.Empty<DamageEventDelta>(),
                WorldChecksum = null,
                WorldChecksumSequence = 0
            }
        };
    }

    // True when the snapshot carries deltas that a WithoutDeltas copy would drop.
    internal static bool HasDeltas(in ServerSnapshotEnvelope snapshot)
    {
        return (snapshot.WorldMutations?.Count ?? 0) > 0 ||
            (snapshot.DamageEvents?.Count ?? 0) > 0 ||
            snapshot.WorldChecksum.HasValue;
    }

    // Puts back a flag taken by TryTakeOverflowResync whose resync the host refused (its
    // per-connection cooldown), so the next tick asks again instead of losing it.
    public void RearmOverflowResync()
    {
        Volatile.Write(ref _overflowResyncPending, 1);
    }

    // Puts back a flag taken by TryTakeOverflowResync because a world transfer is still queued or
    // going out (HasWorldTransferPending): a resync served now would only queue a second full world
    // behind it. The next tick after the transfer finished asks again. Returns true the first time
    // since the last TakeOverflowResyncDeferred, so a caller can count each wait once.
    public bool DeferOverflowResync()
    {
        Volatile.Write(ref _overflowResyncPending, 1);
        return Interlocked.Exchange(ref _overflowResyncDeferred, 1) == 0;
    }

    // True once when the overflow resync being served had waited behind a transfer.
    public bool TakeOverflowResyncDeferred()
    {
        return Interlocked.Exchange(ref _overflowResyncDeferred, 0) != 0;
    }

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

        Interlocked.Add(ref _bytesReceived, bytesRead);
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
        bool faulted;
        lock (_outboundSync)
        {
            writerTask = _writerTask;
            faulted = IsFaulted;
        }

        // A faulted connection may have a socket write that never completes (a send stall, or a
        // transport that ignores cancellation). Closing the transport first aborts that write, so
        // neither the writer task nor _sendLock below can hold up the caller (the tick thread).
        if (faulted)
        {
            await DisposeTransportAsync();
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
            _chunkPayloadWriter.Dispose();
        }
        finally
        {
            _sendLock.Release();
        }

        // _sendLock is deliberately not disposed: a sender still queued on WaitAsync would never be
        // woken by a disposed SemaphoreSlim. It owns no unmanaged handle unless AvailableWaitHandle
        // is read, which this class never does.
        _writerShutdown.Dispose();
        await DisposeTransportAsync();
    }

    // Closes the socket without waiting for the writer, which aborts a pending receive or send.
    // The service calls it at shutdown so every connection loop ends promptly; DisposeAsync
    // still releases the rest. Idempotent.
    public ValueTask CloseTransportAsync()
    {
        return DisposeTransportAsync();
    }

    private ValueTask DisposeTransportAsync()
    {
        return Interlocked.Exchange(ref _transportDisposeState, 1) == 0
            ? _transport.DisposeAsync()
            : ValueTask.CompletedTask;
    }

    private static long SaturatingAdd(long left, long right)
    {
        long sum = left + right;
        return sum < left ? long.MaxValue : sum;
    }

    // The extra stall allowance for writing frameLength bytes at the slowest accepted rate.
    private long SendStallAllowanceTicks(int frameLength)
    {
        int bytesPerSecond = _options.SendStallMinimumBytesPerSecond;
        if (bytesPerSecond <= 0)
        {
            return 0;
        }

        double ticks = (double)frameLength / bytesPerSecond * _timeProvider.TimestampFrequency;
        return ticks >= long.MaxValue ? long.MaxValue : (long)ticks;
    }

    private static long ToTimestampTicks(TimeSpan timeout, TimeProvider timeProvider)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            return 0;
        }

        double ticks = timeout.TotalSeconds * timeProvider.TimestampFrequency;
        return ticks >= long.MaxValue ? long.MaxValue : Math.Max(1L, (long)ticks);
    }

    private void EnqueueSnapshotEntry(OutboundEntry entry)
    {
        ServerSnapshotMessage snapshotMessage = (ServerSnapshotMessage)entry.Message;
        lock (_outboundSync)
        {
            // A merged snapshot no longer matches any shared payload, so it is encoded for this
            // connection alone. That only happens while this client lags behind the tick rate.
            // Same rule as ServerSnapshotEnvelope.TryCoalesce: full-world snapshots never merge.
            LinkedListNode<OutboundEntry>? last = _outbound.Last;
            if (last != null && ServerSnapshotCoalesceBuffer.CanCoalesce(snapshotMessage.Snapshot))
            {
                OutboundEntry pending = last.Value;
                if (pending.Coalesced != null)
                {
                    AppendCoalesced(pending.Coalesced, snapshotMessage.Snapshot);
                    return;
                }

                if (pending.Message is ServerSnapshotMessage pendingSnapshot &&
                    ServerSnapshotCoalesceBuffer.CanCoalesce(pendingSnapshot.Snapshot))
                {
                    ServerSnapshotCoalesceBuffer buffer =
                        _freeCoalesceBuffers.TryPop(out ServerSnapshotCoalesceBuffer? free) ? free : new();
                    buffer.Begin(pendingSnapshot.Snapshot);
                    last.Value = new OutboundEntry(pendingSnapshot, Shared: null, buffer);
                    AppendCoalesced(buffer, snapshotMessage.Snapshot);
                    return;
                }
            }
        }

        Enqueue(entry);
    }

    // Caller holds _outboundSync. Past either delta cap the merged deltas (this snapshot's
    // included) are dropped and a world resync is flagged: the client would otherwise receive an
    // unbounded backlog, and the resync replaces every delta it missed.
    private void AppendCoalesced(ServerSnapshotCoalesceBuffer buffer, in ServerSnapshotEnvelope newer)
    {
        bool overflow =
            buffer.WorldMutationCount + (newer.WorldMutations?.Count ?? 0) > _options.MaxCoalescedWorldMutations ||
            buffer.DamageEventCount + (newer.DamageEvents?.Count ?? 0) > _options.MaxCoalescedDamageEvents;
        buffer.Append(newer);
        Interlocked.Increment(ref _coalescedSnapshots);
        if (!overflow)
        {
            return;
        }

        buffer.DropDeltas();
        Interlocked.Increment(ref _overflowResyncs);
        Volatile.Write(ref _overflowResyncPending, 1);
        if (!NetworkDiagnosticsLog.IsEnabled)
        {
            return;
        }

        NetworkDiagnosticsLog.Write(
            "server.connection.coalesce-overflow",
            $"connection={ConnectionId} tick={buffer.ServerTick} merged deltas dropped, world resync requested");
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
                Fault($"outbound backlog exceeded {MaxPendingOutboundMessages} messages", ServerConnectionCloseReason.BacklogOverflow);
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

            // The writer turns exactly these entries into transfers (see WriteLoopAsync).
            if (entry.Shared == null && WorldTransfer.TryGetWorld(entry.Message, out _))
            {
                if (_pendingWorldTransfers > 0)
                {
                    Interlocked.Increment(ref _backToBackWorldTransfers);
                }

                _pendingWorldTransfers++;
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
                    OutboundEntry entry = default;
                    IServerToClientMessage? message = null;
                    bool sendChunk;
                    lock (_outboundSync)
                    {
                        LinkedListNode<OutboundEntry>? first = _outbound.First;
                        // A world transfer in progress alternates with the queue: a chunk after
                        // every queued message, and chunks back to back while the queue is empty.
                        // A faulted connection sends nothing more (Fault cleared the queue).
                        sendChunk = _activeTransfer != null && !IsFaulted && (_chunkDue || first == null);
                        if (!sendChunk)
                        {
                            if (first == null)
                            {
                                break;
                            }

                            entry = first.Value;
                            _outbound.Remove(first);
                            first.Value = default;
                            _freeNodes.Push(first);
                            // Dequeued, so no later snapshot merges into this buffer. The merged
                            // envelope aliases the buffer's lists until the send below is done.
                            message = entry.Coalesced != null
                                ? new ServerSnapshotMessage(entry.Coalesced.ToEnvelope())
                                : entry.Message;
                        }

                        _writing = true;
                    }

                    try
                    {
                        if (sendChunk)
                        {
                            await SendNextWorldChunkAsync(cancellationToken);
                        }
                        else if (entry.Shared == null && WorldTransfer.TryGetWorld(message!, out WorldSnapshot world))
                        {
                            await StartWorldTransferAsync(message!, world, cancellationToken);
                        }
                        else
                        {
                            await SendMessageAsync(message!, entry.Shared, cancellationToken);
                            _chunkDue = true;
                        }
                    }
                    finally
                    {
                        lock (_outboundSync)
                        {
                            // A transfer still running keeps the connection busy for
                            // FlushOutboundAsync even while the queue is empty; a faulted
                            // connection sends no more chunks, so it is not busy.
                            _writing = _activeTransfer != null && !IsFaulted;
                            if (entry.Coalesced != null)
                            {
                                entry.Coalesced.Reset();
                                _freeCoalesceBuffers.Push(entry.Coalesced);
                            }
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
                Fault($"{ex.GetType().Name}: {ex.Message}", ServerConnectionCloseReason.Error);
            }
        }
        finally
        {
            // An unfinished transfer's capture goes back to the pool; nothing will send it now.
            WorldTransferSender? unfinished = _activeTransfer;
            _activeTransfer = null;
            unfinished?.ReleaseCapture();
            lock (_outboundSync)
            {
                _writing = false;
                _pendingWorldTransfers = 0;
            }
        }
    }

    // Sends the carrier and the first chunk. A transfer still running is finished first, so the
    // client never sees a second carrier before the first transfer's final chunk.
    private async ValueTask StartWorldTransferAsync(
        IServerToClientMessage message,
        WorldSnapshot world,
        CancellationToken cancellationToken)
    {
        while (_activeTransfer != null)
        {
            await SendNextWorldChunkAsync(cancellationToken);
        }

        // A world exported by the runtime is a view over its pooled capture, encoded from the
        // capture directly; any other world is captured here, off the tick.
        WorldSnapshotCapture capture = WorldSnapshotCapture.TryGetCapture(world, out WorldSnapshotCapture? captured)
            ? captured
            : WorldSnapshotCapture.FromSnapshot(world);
        int transferId = ++_nextWorldTransferId;
        _activeTransfer = new WorldTransferSender(transferId, capture, releaseCaptureWhenDone: true);
        Interlocked.Increment(ref _worldTransfers);
        await SendMessageAsync(WorldTransfer.ToCarrier(message, transferId), sharedPayload: null, cancellationToken);
        await SendNextWorldChunkAsync(cancellationToken);
    }

    private async ValueTask SendNextWorldChunkAsync(CancellationToken cancellationToken)
    {
        WorldTransferSender transfer = _activeTransfer
            ?? throw new InvalidOperationException("No world transfer is in progress.");
        await SendFrameAsync(message: null, sharedPayload: null, transfer, cancellationToken);
        Interlocked.Increment(ref _worldChunksSent);
        _chunkDue = false;
        if (transfer.IsComplete)
        {
            _activeTransfer = null;
            lock (_outboundSync)
            {
                _pendingWorldTransfers = Math.Max(0, _pendingWorldTransfers - 1);
            }
        }
    }

    // Shared is set only for a snapshot broadcast unchanged; a merged snapshot is encoded alone.
    // Coalesced is set once later snapshots were merged into this one; the writer then sends the
    // buffer's merged envelope instead of Message.
    private readonly record struct OutboundEntry(
        IServerToClientMessage Message,
        SharedEncodedPayload? Shared,
        ServerSnapshotCoalesceBuffer? Coalesced = null);

    // Caller holds _outboundSync.
    private void Fault(string reason, ServerConnectionCloseReason closeReason)
    {
        if (IsFaulted)
        {
            return;
        }

        IsFaulted = true;
        CloseReason = closeReason;
        _outbound.Clear();
        _pendingWorldTransfers = 0;
        NetworkDiagnosticsLog.Write(
            "server.connection.fault",
            $"connection={ConnectionId} kind={closeReason} reason={reason}");
    }

    private void FlushInboundFrames()
    {
        while (_receiveBuffer.TryReadPayload(_framer, out ReadOnlySpan<byte> payload))
        {
            Interlocked.Increment(ref _framesReceived);
            // Any complete frame, an empty one included, shows the peer is alive.
            Volatile.Write(ref _lastInboundTimestamp, _timeProvider.GetTimestamp());
            if (payload.IsEmpty)
            {
                continue;
            }

            DecodedProtocolMessage decoded;
            try
            {
                // Server-only kinds (a world chunk, a snapshot) are refused before their payload is
                // decoded.
                decoded = _codec.DecodeFromClient(payload);
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

    private ValueTask SendMessageAsync(
        IServerToClientMessage message,
        SharedEncodedPayload? sharedPayload,
        CancellationToken cancellationToken)
    {
        return SendFrameAsync(message, sharedPayload, worldChunk: null, cancellationToken);
    }

    // Sends one frame: message, or the next chunk of worldChunk when message is null.
    private async ValueTask SendFrameAsync(
        IServerToClientMessage? message,
        SharedEncodedPayload? sharedPayload,
        WorldTransferSender? worldChunk,
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
            ReadOnlyMemory<byte> frame = EncodeServerFrame(message, sharedPayload, worldChunk);
            frameLength = frame.Length;
            // frame aliases _frameWriter's pooled buffer. Passing it to the transport is safe only
            // because the send is awaited here, inside _sendLock, before the next encode reuses the
            // buffer; transports must not keep the memory after SendAsync completes.
            // Bracketed for CheckLiveness: a write in flight for longer than SendStallTimeout plus
            // its size allowance faults the connection.
            Volatile.Write(ref _sendStallAllowanceTicks, SendStallAllowanceTicks(frameLength));
            Volatile.Write(ref _sendStartedTimestamp, _timeProvider.GetTimestamp());
            Volatile.Write(ref _sendInFlight, 1);
            try
            {
                await _transport.SendAsync(frame, cancellationToken);
            }
            finally
            {
                Volatile.Write(ref _sendInFlight, 0);
            }

            Interlocked.Add(ref _bytesSent, frameLength);
            Interlocked.Increment(ref _framesSent);
        }
        finally
        {
            _sendLock.Release();
            stopwatch?.Stop();
        }

        LogOutbound(message, worldChunk, frameLength, stopwatch);
    }

    // Caller holds _sendLock. The sequence is consumed only once the frame is fully encoded, so a
    // message that fails to encode leaves no gap.
    private ReadOnlyMemory<byte> EncodeServerFrame(
        IServerToClientMessage? message,
        SharedEncodedPayload? sharedPayload,
        WorldTransferSender? worldChunk)
    {
        TransportEnvelope envelope = new(
            TransportEnvelope.CurrentProtocolVersion,
            worldChunk != null ? WorldChunkMessage.Kind : message!.MessageKind,
            _nextOutboundSequence);

        if (worldChunk != null)
        {
            // The chunk's payload stops at the byte budget, which only a direct write can do; it
            // is then wrapped like a shared snapshot payload.
            _chunkPayloadWriter.Reset();
            Utf8JsonWriter payloadWriter = _chunkPayloadWriter.GetJsonWriter(ChunkPayloadWriterOptions);
            worldChunk.WriteNextChunk(payloadWriter, WorldChunkBytes);
            payloadWriter.Flush();
            _frameWriter.BeginFrame(_framer);
            _codec.WriteEnvelope(envelope, _chunkPayloadWriter.WrittenMemory.Span, _frameWriter);
            ReadOnlyMemory<byte> chunkFrame = _frameWriter.CompleteFrame(_framer);
            _nextOutboundSequence++;
            return chunkFrame;
        }

        _frameWriter.BeginFrame(_framer);
        // The actor frame the client holds once this frame is written (see _lastActorFrameSequence).
        ulong sentActorFrame = _lastActorFrameSequence;
        if (sharedPayload != null)
        {
            // The first writer to get here encodes the snapshot for every connection sharing it.
            // The body is the same for everyone; only this connection's own accepted command
            // sequence goes into its envelope header (SCALE-2), so a broadcast costs O(players)
            // instead of every connection carrying every session's sequence.
            ServerSnapshotEnvelope shared = sharedPayload.Message.Snapshot;
            bool keyframe = NeedsActorKeyframe(shared);
            envelope = envelope with { Ack = ResolveAck(shared) };
            _codec.WriteEnvelope(envelope, keyframe ? sharedPayload.KeyframeBytes : sharedPayload.Bytes, _frameWriter);
            sentActorFrame = shared.ActorDelta?.Sequence ?? 0;
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
                    // Encoded for this connection alone (merged, a resync, or sent directly): its
                    // actors go as a keyframe unless the client holds the frame's base.
                    _codec.Encode(envelope, WithActorKeyframeIfNeeded(snapshot), _frameWriter);
                    sentActorFrame = snapshot.Snapshot.ActorDelta?.Sequence ?? 0;
                    break;
                case ServerActionMessage action:
                    _codec.Encode(envelope, action, _frameWriter);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported server message type '{message.GetType().Name}' for connection encoding.");
            }
        }

        // A delta snapshot too large for the peer's frame cap (one advance's deltas, which the
        // merge caps do not bound) would fault this writer, and with a shared payload every
        // connection's writer at once. It goes out without its deltas instead, and the
        // connection is flagged for a world resync, which the service picks up on its next
        // broadcast. Only an oversized frame pays for this.
        if (_frameWriter.WrittenCount - _framer.HeaderLength > _maxWritePayloadBytes &&
            message is ServerSnapshotMessage { Snapshot.World: null } oversized &&
            HasDeltas(oversized.Snapshot))
        {
            int oversizedBytes = _frameWriter.WrittenCount - _framer.HeaderLength;
            _frameWriter.BeginFrame(_framer);
            // Encoded for this connection alone, with the full sequence map and no header ack.
            _codec.Encode(envelope with { Ack = null }, WithActorKeyframeIfNeeded(WithoutDeltas(oversized)), _frameWriter);
            sentActorFrame = oversized.Snapshot.ActorDelta?.Sequence ?? 0;
            Interlocked.Increment(ref _oversizedSnapshotFrames);
            FlagOverflowResync();
            NetworkDiagnosticsLog.Write(
                "server.connection.snapshot-oversized",
                $"connection={ConnectionId} tick={oversized.Snapshot.ServerTick} bytes={oversizedBytes} cap={_maxWritePayloadBytes} " +
                $"mutations={oversized.Snapshot.WorldMutations?.Count ?? 0} damage={oversized.Snapshot.DamageEvents?.Count ?? 0}; deltas dropped, world resync requested");
        }

        ReadOnlyMemory<byte> frame = _frameWriter.CompleteFrame(_framer);
        _nextOutboundSequence++;
        // A send that fails after this faults the connection, so nothing later relies on it.
        _lastActorFrameSequence = sentActorFrame;
        return frame;
    }

    // Caller holds _sendLock. True when snapshot's actors are a delta against a frame this
    // connection's client does not hold.
    private bool NeedsActorKeyframe(in ServerSnapshotEnvelope snapshot)
    {
        if (snapshot.ActorDelta is not { IsKeyframe: false } delta || delta.BaseSequence == _lastActorFrameSequence)
        {
            return false;
        }

        Interlocked.Increment(ref _actorKeyframesForced);
        return true;
    }

    // Caller holds _sendLock. Allocates only on the forced-keyframe path.
    private ServerSnapshotMessage WithActorKeyframeIfNeeded(ServerSnapshotMessage message)
    {
        return NeedsActorKeyframe(message.Snapshot)
            ? message with { Snapshot = message.Snapshot with { ActorDelta = message.Snapshot.ActorDelta!.Value.Keyframe } }
            : message;
    }

    // This connection's own accepted command sequence, read from the snapshot's full map by the
    // session this connection joined as. Lock-free lookups; allocates nothing.
    private TransportAck ResolveAck(in ServerSnapshotEnvelope snapshot)
    {
        if (_host.TryGetConnectionContext(ConnectionId, out ServerRoutingContext? context)
            && context?.LastJoinRequest?.RequestedSessionId is string sessionId
            && snapshot.LastAcceptedCommandSequences is IReadOnlyDictionary<string, ulong> sequences
            && sequences.TryGetValue(sessionId, out ulong sequence))
        {
            return new TransportAck(sessionId, sequence);
        }

        return new TransportAck(null, 0);
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

    private void LogOutbound(IServerToClientMessage? message, WorldTransferSender? worldChunk, int byteCount, Stopwatch? stopwatch)
    {
        if (!NetworkDiagnosticsLog.IsEnabled)
        {
            return;
        }

        if (message == null)
        {
            NetworkDiagnosticsLog.Write(
                "server.tx",
                $"connection={ConnectionId} kind={WorldChunkMessage.Kind} bytes={byteCount} sendMs={stopwatch?.Elapsed.TotalMilliseconds ?? 0d:0.00} " +
                $"transfer={worldChunk?.TransferId} chunk={worldChunk?.ChunksWritten - 1} final={worldChunk?.IsComplete}");
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

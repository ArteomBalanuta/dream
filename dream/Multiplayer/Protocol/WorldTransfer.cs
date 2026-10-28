using System.Text.Json;
using OpenTK.Mathematics;
using Dream.Simulation.World;

namespace Dream.Multiplayer.Protocol;

// The two ends of a chunked world transfer (WorldChunkMessage).
public static class WorldTransfer
{
    // The world a server message would carry inline in protocol v2: a JoinAccepted's InitialWorld
    // or a world snapshot (resync). Such a message is sent as a carrier plus chunks instead.
    public static bool TryGetWorld(IServerToClientMessage message, out WorldSnapshot world)
    {
        switch (message)
        {
            case JoinAcceptedMessage { InitialWorld: WorldSnapshot initialWorld, WorldTransferId: null }:
                world = initialWorld;
                return true;
            case ServerSnapshotMessage { Snapshot.World: WorldSnapshot snapshotWorld, WorldTransferId: null }:
                world = snapshotWorld;
                return true;
            default:
                world = default;
                return false;
        }
    }

    // The message without its world, announcing transfer transferId.
    public static IServerToClientMessage ToCarrier(IServerToClientMessage message, int transferId)
    {
        return message switch
        {
            JoinAcceptedMessage joinAccepted => joinAccepted with { InitialWorld = null, WorldTransferId = transferId },
            ServerSnapshotMessage snapshot => new ServerSnapshotMessage(snapshot.Snapshot with { World = null }, transferId),
            _ => throw new ArgumentException($"'{message.MessageKind}' cannot carry a world transfer.", nameof(message))
        };
    }

    // The transfer a received message announces, if any.
    public static int? GetTransferId(IServerToClientMessage message)
    {
        return message switch
        {
            JoinAcceptedMessage joinAccepted => joinAccepted.WorldTransferId,
            ServerSnapshotMessage snapshot => snapshot.WorldTransferId,
            _ => null
        };
    }

    // The carrier as protocol v2 would have delivered it: with the assembled world, no transfer id.
    public static IServerToClientMessage WithWorld(IServerToClientMessage carrier, WorldSnapshot world)
    {
        return carrier switch
        {
            JoinAcceptedMessage joinAccepted => joinAccepted with { InitialWorld = world, WorldTransferId = null },
            ServerSnapshotMessage snapshot => new ServerSnapshotMessage(snapshot.Snapshot with { World = world }),
            _ => throw new ArgumentException($"'{carrier.MessageKind}' cannot carry a world transfer.", nameof(carrier))
        };
    }
}

// Server side: one transfer's progress through its capture. Used by the connection writer only.
public sealed class WorldTransferSender
{
    private WorldSnapshotJsonConverter.Cursor _cursor;

    public WorldTransferSender(int transferId, WorldSnapshotCapture capture, bool releaseCaptureWhenDone)
    {
        ArgumentNullException.ThrowIfNull(capture);
        TransferId = transferId;
        Capture = capture;
        ReleaseCaptureWhenDone = releaseCaptureWhenDone;
    }

    public int TransferId { get; }

    public WorldSnapshotCapture Capture { get; }

    public bool ReleaseCaptureWhenDone { get; }

    public int ChunksWritten { get; private set; }

    public bool IsComplete { get; private set; }

    // Writes the next chunk's payload. Returns true when it was the final chunk; the capture is
    // released then if this transfer owns it.
    public bool WriteNextChunk(Utf8JsonWriter writer, int chunkBytes)
    {
        if (IsComplete)
        {
            throw new InvalidOperationException("The world transfer has already sent its final chunk.");
        }

        bool final = WorldChunkMessageJsonConverter.WriteChunk(
            writer,
            TransferId,
            ChunksWritten,
            Capture,
            ref _cursor,
            Math.Max(1, chunkBytes));
        ChunksWritten++;
        if (final)
        {
            IsComplete = true;
            ReleaseCapture();
        }

        return final;
    }

    // Also called when the connection ends with the transfer unfinished.
    public void ReleaseCapture()
    {
        if (ReleaseCaptureWhenDone)
        {
            Capture.Release();
        }
    }
}

// Bounds on one world transfer a client receives (WorldTransferReceiver). A carrier announces a
// transfer and the client then holds every message until the final chunk, so without bounds a
// server (or anything on the path) that never sends the final chunk makes the client hold
// messages and fragments forever while the session looks healthy: snapshots keep arriving, so the
// silence watchdog stays satisfied. Each bound ends the session with a WorldTransferLimitException.
public sealed record WorldTransferLimits
{
    // A cell holds at most one cube, one authored-subcell record and one rubble cell.
    public const int ItemsPerCell = 3;

    public static WorldTransferLimits Default { get; } = new();

    // Cubes, subcells and rubble cells together may be at most ItemsPerCell per cell of the
    // dimensions chunk 0 announces, but never fewer than MinItems (worlds whose cubes lie outside
    // their nominal size, as some tests build) and never more than MaxItems. A k=0 rubble entry is
    // about 8 B on the wire and over 200 B decoded, so the item count, not the wire size, bounds
    // the client's memory.
    public int MinItems { get; init; } = 1 << 16;

    // 4M items, about 1 GB decoded at the rubble worst case, and far beyond any world the server
    // builds today (floors up to 100 x 100 cells, 8 cells high: 80k cells, 240k items).
    public int MaxItems { get; init; } = 1 << 22;

    // Messages held behind the carrier. While a transfer runs the server sends one queued message
    // per chunk (ServerConnection), so a legitimate transfer holds about as many messages as it
    // has chunks; 4096 chunks of 64 KB would be a 256 MB world.
    public int MaxHeldMessages { get; init; } = 4096;

    // From the carrier's arrival to the final chunk. 120 s covers the 1.1 MB World85k resync at
    // the slowest link the server keeps (a 64 KB chunk inside the 5 s send-stall timeout, about
    // 12.5 KB/s: ~90 s). Zero or less turns the deadline off.
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(120);

    // The item budget for a world of these dimensions.
    public long MaxItemsFor(int width, int height, int depth)
    {
        long cells = (long)Math.Max(0, width) * Math.Max(0, height) * Math.Max(0, depth);
        long items = cells >= MaxItems ? MaxItems : cells * ItemsPerCell;
        return Math.Max(MinItems, Math.Min(items, MaxItems));
    }
}

// A world transfer broke one of its WorldTransferLimits, or its chunks disagree on the world's
// dimensions. A protocol error like a decode failure: the client ends the session with it.
public sealed class WorldTransferLimitException(string message) : InvalidOperationException(message);

// Client side, in arrival order: passes every server message on, except that a carrier and
// everything after it are held until its transfer's final chunk, then delivered as the carrier
// with the assembled world followed by the held messages. The consumer so sees exactly the
// protocol v2 message order. Chunks themselves are never delivered. Not thread-safe: one reader.
// Every transfer is bounded by Limits: items and held messages in Accept, the deadline in
// CheckDeadline, which the client's receive loop calls for every frame it drains.
public sealed class WorldTransferReassembler
{
    private readonly TimeProvider _timeProvider;
    private WorldTransferReceiver? _current;

    public WorldTransferReassembler()
        : this(WorldTransferLimits.Default)
    {
    }

    public WorldTransferReassembler(WorldTransferLimits limits, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        Limits = limits;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public WorldTransferLimits Limits { get; }

    // The transfer being received, if any.
    public WorldTransferReceiver? Current => Volatile.Read(ref _current);

    // Throws WorldTransferLimitException when the transfer in progress started more than
    // Limits.Deadline ago. Allocation-free when there is none or it is inside the deadline.
    public void CheckDeadline()
    {
        WorldTransferReceiver? current = _current;
        TimeSpan deadline = Limits.Deadline;
        if (current == null || deadline <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan elapsed = _timeProvider.GetElapsedTime(current.StartedTimestamp);
        if (elapsed > deadline)
        {
            throw new WorldTransferLimitException(
                $"World transfer {current.TransferId} did not finish within {deadline.TotalSeconds:0.###} s " +
                $"({current.ChunksReceived} chunks, {current.ItemCount} items and {current.HeldMessages.Count} held messages after {elapsed.TotalSeconds:0.#} s).");
        }
    }

    // Returns the transfer that this message completed (after its messages were delivered), or
    // null. A chunk with no transfer in progress, of another transfer, out of order, or a second
    // carrier before the first transfer finished is a protocol error (InvalidOperationException).
    public WorldTransferReceiver? Accept(IServerToClientMessage message, Action<IServerToClientMessage> deliver)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(deliver);
        WorldTransferReceiver? current = _current;
        if (message is WorldChunkMessage chunk)
        {
            if (current == null)
            {
                throw new InvalidOperationException(
                    $"Received chunk {chunk.ChunkIndex} of world transfer {chunk.TransferId} with no transfer in progress.");
            }

            if (!current.Add(chunk))
            {
                return null;
            }

            Volatile.Write(ref _current, null);
            deliver(WorldTransfer.WithWorld(current.Carrier, current.BuildWorld()));
            IReadOnlyList<IServerToClientMessage> held = current.HeldMessages;
            for (int i = 0; i < held.Count; i++)
            {
                deliver(held[i]);
            }

            return current;
        }

        int? transferId = WorldTransfer.GetTransferId(message);
        if (current != null)
        {
            if (transferId.HasValue)
            {
                throw new InvalidOperationException(
                    $"World transfer {transferId} started before transfer {current.TransferId} finished.");
            }

            current.Hold(message);
            return null;
        }

        if (transferId.HasValue)
        {
            Volatile.Write(ref _current, new WorldTransferReceiver(message, Limits, _timeProvider.GetTimestamp()));
            return null;
        }

        deliver(message);
        return null;
    }

    // Every message of a recorded stream, reassembled (tests and tools that replay frames).
    public static List<IServerToClientMessage> Reassemble(IEnumerable<IProtocolMessage> messages)
    {
        List<IServerToClientMessage> delivered = [];
        WorldTransferReassembler reassembler = new();
        foreach (IProtocolMessage message in messages)
        {
            if (message is IServerToClientMessage serverMessage)
            {
                reassembler.Accept(serverMessage, delivered.Add);
            }
        }

        return delivered;
    }
}

// Client side: the carrier of one transfer, the fragments received so far and the messages held
// behind the carrier until the world is complete.
public sealed class WorldTransferReceiver
{
    private readonly List<IServerToClientMessage> _held = [];
    private readonly List<PlacedCubeSnapshot> _cubes = [];
    private readonly List<Vector3> _subcells = [];
    private readonly List<FracturedCubeSnapshot> _rubble = [];
    private readonly WorldTransferLimits _limits;
    private int _width;
    private int _height;
    private int _depth;
    private ulong? _sequence;
    private long _maxItems;

    public WorldTransferReceiver(IServerToClientMessage carrier)
        : this(carrier, WorldTransferLimits.Default, startedTimestamp: 0)
    {
    }

    public WorldTransferReceiver(IServerToClientMessage carrier, WorldTransferLimits limits, long startedTimestamp)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        ArgumentNullException.ThrowIfNull(limits);
        Carrier = carrier;
        TransferId = WorldTransfer.GetTransferId(carrier)
            ?? throw new ArgumentException("The message does not announce a world transfer.", nameof(carrier));
        _limits = limits;
        StartedTimestamp = startedTimestamp;
    }

    public IServerToClientMessage Carrier { get; }

    public int TransferId { get; }

    // When the carrier arrived, a timestamp of the reassembler's TimeProvider.
    public long StartedTimestamp { get; }

    public int ChunksReceived { get; private set; }

    public bool IsComplete { get; private set; }

    // Cubes, subcells and rubble cells received so far.
    public long ItemCount => (long)_cubes.Count + _subcells.Count + _rubble.Count;

    public IReadOnlyList<IServerToClientMessage> HeldMessages => _held;

    // A message past the limits' MaxHeldMessages is a protocol error (WorldTransferLimitException).
    public void Hold(IServerToClientMessage message)
    {
        if (_held.Count >= _limits.MaxHeldMessages)
        {
            throw new WorldTransferLimitException(
                $"World transfer {TransferId} held more than {_limits.MaxHeldMessages} messages before its final chunk " +
                $"({ChunksReceived} chunks received).");
        }

        _held.Add(message);
    }

    // Appends a fragment. Chunks of another transfer, out of order, or after the final one are a
    // protocol error. Returns true when this was the final chunk.
    public bool Add(WorldChunkMessage chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (IsComplete)
        {
            throw new InvalidOperationException($"World transfer {TransferId} received a chunk after its final one.");
        }

        if (chunk.TransferId != TransferId || chunk.ChunkIndex != ChunksReceived)
        {
            throw new InvalidOperationException(
                $"Expected chunk {ChunksReceived} of world transfer {TransferId}, received chunk {chunk.ChunkIndex} of transfer {chunk.TransferId}.");
        }

        WorldSnapshot fragment = chunk.World;
        if (ChunksReceived == 0)
        {
            _width = fragment.Width;
            _height = fragment.Height;
            _depth = fragment.Depth;
            _sequence = fragment.MutationSequence;
            _maxItems = _limits.MaxItemsFor(_width, _height, _depth);
        }
        else if (fragment.Width != _width || fragment.Height != _height || fragment.Depth != _depth)
        {
            throw new WorldTransferLimitException(
                $"Chunk {chunk.ChunkIndex} of world transfer {TransferId} announces a {fragment.Width}x{fragment.Height}x{fragment.Depth} world; " +
                $"chunk 0 announced {_width}x{_height}x{_depth}.");
        }

        long items = ItemCount
            + (fragment.PlacedCubes?.Count ?? 0)
            + (fragment.AuthoredSubcellCells?.Count ?? 0)
            + (fragment.FracturedCubes?.Count ?? 0);
        if (items > _maxItems)
        {
            throw new WorldTransferLimitException(
                $"World transfer {TransferId} passed {_maxItems} items (cubes, subcells and rubble cells) for a " +
                $"{_width}x{_height}x{_depth} world at chunk {chunk.ChunkIndex}.");
        }

        AddRange(_cubes, fragment.PlacedCubes);
        AddRange(_subcells, fragment.AuthoredSubcellCells);
        AddRange(_rubble, fragment.FracturedCubes);
        ChunksReceived++;
        IsComplete = chunk.IsFinal;
        return IsComplete;
    }

    public WorldSnapshot BuildWorld()
    {
        if (!IsComplete)
        {
            throw new InvalidOperationException($"World transfer {TransferId} is not complete.");
        }

        return WorldSnapshotJsonConverter.Create(_width, _height, _depth, _sequence, _cubes, _subcells, _rubble);
    }

    private static void AddRange<T>(List<T> target, IReadOnlyList<T>? source)
    {
        if (source == null)
        {
            return;
        }

        for (int i = 0; i < source.Count; i++)
        {
            target.Add(source[i]);
        }
    }
}

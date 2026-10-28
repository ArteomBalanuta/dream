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

// Client side, in arrival order: passes every server message on, except that a carrier and
// everything after it are held until its transfer's final chunk, then delivered as the carrier
// with the assembled world followed by the held messages. The consumer so sees exactly the
// protocol v2 message order. Chunks themselves are never delivered. Not thread-safe: one reader.
public sealed class WorldTransferReassembler
{
    private WorldTransferReceiver? _current;

    // The transfer being received, if any.
    public WorldTransferReceiver? Current => Volatile.Read(ref _current);

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
            Volatile.Write(ref _current, new WorldTransferReceiver(message));
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
    private int _width;
    private int _height;
    private int _depth;
    private ulong? _sequence;

    public WorldTransferReceiver(IServerToClientMessage carrier)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        Carrier = carrier;
        TransferId = WorldTransfer.GetTransferId(carrier)
            ?? throw new ArgumentException("The message does not announce a world transfer.", nameof(carrier));
    }

    public IServerToClientMessage Carrier { get; }

    public int TransferId { get; }

    public int ChunksReceived { get; private set; }

    public bool IsComplete { get; private set; }

    public IReadOnlyList<IServerToClientMessage> HeldMessages => _held;

    public void Hold(IServerToClientMessage message)
    {
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

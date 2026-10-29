using System.Text.Json;
using OpenTK.Mathematics;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Simulation.World.Terrain;

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

    private readonly TerrainTransferPayload? _terrain;

    // terrain (protocol v8, stored worlds): its fragments go out as server.terrain-chunk frames
    // before world chunk 0.
    public WorldTransferSender(
        int transferId,
        WorldSnapshotCapture capture,
        bool releaseCaptureWhenDone,
        TerrainTransferPayload? terrain = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        TransferId = transferId;
        Capture = capture;
        ReleaseCaptureWhenDone = releaseCaptureWhenDone;
        _terrain = terrain;
    }

    // The message kind WriteNextChunk writes next: terrain fragments first, then world chunks.
    public string NextFrameKind =>
        _terrain != null && TerrainFragmentsWritten < _terrain.Fragments.Count
            ? TerrainChunkMessage.Kind
            : WorldChunkMessage.Kind;

    // Terrain frames written so far.
    public int TerrainFragmentsWritten { get; private set; }

    public int TransferId { get; }

    public WorldSnapshotCapture Capture { get; }

    public bool ReleaseCaptureWhenDone { get; }

    public int ChunksWritten { get; private set; }

    public bool IsComplete { get; private set; }

    // Writes the next frame's payload (see NextFrameKind). Returns true when it was the final world
    // chunk (a terrain fragment never is); the capture is released then if this transfer owns it.
    public bool WriteNextChunk(Utf8JsonWriter writer, int chunkBytes)
    {
        if (IsComplete)
        {
            throw new InvalidOperationException("The world transfer has already sent its final chunk.");
        }

        if (_terrain != null && TerrainFragmentsWritten < _terrain.Fragments.Count)
        {
            int fragment = TerrainFragmentsWritten;
            (int start, int length) = _terrain.Fragments[fragment].GetOffsetAndLength(_terrain.Chunks.Count);
            TerrainChunkMessageJsonConverter.WriteFragment(
                writer,
                TransferId,
                fragment,
                _terrain.Chunks,
                start,
                start + length,
                isFinal: fragment == _terrain.Fragments.Count - 1);
            TerrainFragmentsWritten++;
            return false;
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

    // Two bounds, both checked by WorldTransferReassembler.CheckDeadline:
    //
    // ProgressTimeout: from the carrier, or the latest chunk, to the next chunk. This is what
    // catches a transfer that stopped. The server keeps a connection only while each frame write
    // finishes inside the 5 s send-stall timeout (plus its size allowance), and while a transfer
    // runs its writer alternates one chunk and one queued message, so on any link the server keeps
    // a chunk follows the previous one within about two such writes (~10 s). 30 s is three times
    // that. Zero or less turns it off.
    public TimeSpan ProgressTimeout { get; init; } = TimeSpan.FromSeconds(30);

    // From the carrier's arrival to the final chunk, however steadily the chunks come: a backstop
    // for a transfer that trickles forever. The 1.1 MB World85k resync at the slowest link the
    // server keeps (a 64 KB chunk inside the 5 s send-stall timeout, about 12.5 KB/s) takes ~90 s
    // for its chunks alone, and the snapshot the writer sends between each pair of chunks pushes
    // it past 120 s on a 12.5-15 KB/s link; 300 s leaves more than twice that. Zero or less turns
    // it off.
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(300);

    // Stored terrain (protocol v8): the raw bytes of all chunk blobs of one transfer, of one blob, and
    // the decoded subcell overrides of the whole terrain (the decode budget).
    public long MaxTerrainBytes { get; init; } = 16L << 20;

    public int MaxTerrainBlobBytes { get; init; } = 1 << 20;

    public long MaxTerrainSubcellOverrides { get; init; } = 1L << 22;

    // The implied subcells of the whole terrain: resolution^3 for every decoded record that is not a
    // plain subdivision-free SolidCube (Map builds that many render/collision candidates for it, even
    // when the blob carries no override entries). World10k generated terrain totals 1,667,072 (pinned in
    // TerrainChunkCodecBudgetTests); the 8,388,608 default is about 5x that.
    public long MaxTerrainSubcells { get; init; } = 1L << 23;

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
// CheckDeadline, which the client's receive loop calls after every frame it drains (after the
// frame was accepted, so a final chunk that arrives just at a bound still completes its transfer).
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
    // Limits.Deadline ago, or received no chunk (counting the carrier as the start) for more than
    // Limits.ProgressTimeout. Allocation-free when there is none or it is inside both bounds.
    public void CheckDeadline()
    {
        WorldTransferReceiver? current = _current;
        if (current == null)
        {
            return;
        }

        TimeSpan deadline = Limits.Deadline;
        TimeSpan elapsed = _timeProvider.GetElapsedTime(current.StartedTimestamp);
        if (deadline > TimeSpan.Zero && elapsed > deadline)
        {
            throw new WorldTransferLimitException(
                $"World transfer {current.TransferId} did not finish within {deadline.TotalSeconds:0.###} s " +
                $"({current.ChunksReceived} chunks, {current.TerrainFragmentsReceived} terrain fragments, {current.ItemCount} items and {current.HeldMessages.Count} held messages after {elapsed.TotalSeconds:0.#} s).");
        }

        TimeSpan progressTimeout = Limits.ProgressTimeout;
        if (progressTimeout <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan stalled = _timeProvider.GetElapsedTime(current.LastProgressTimestamp);
        if (stalled > progressTimeout)
        {
            throw new WorldTransferLimitException(
                $"World transfer {current.TransferId} stalled: no chunk for {progressTimeout.TotalSeconds:0.###} s " +
                $"({current.ChunksReceived} chunks, {current.TerrainFragmentsReceived} terrain fragments, {current.ItemCount} items and {current.HeldMessages.Count} held messages after {elapsed.TotalSeconds:0.#} s; " +
                $"last progress {stalled.TotalSeconds:0.#} s ago).");
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
        if (message is TerrainChunkMessage terrain)
        {
            if (current == null)
            {
                throw new InvalidOperationException(
                    $"Received terrain fragment {terrain.Index} of world transfer {terrain.TransferId} with no transfer in progress.");
            }

            current.AddTerrain(terrain);
            current.LastProgressTimestamp = _timeProvider.GetTimestamp();
            return null;
        }

        if (message is WorldChunkMessage chunk)
        {
            if (current == null)
            {
                throw new InvalidOperationException(
                    $"Received chunk {chunk.ChunkIndex} of world transfer {chunk.TransferId} with no transfer in progress.");
            }

            if (!current.Add(chunk))
            {
                current.LastProgressTimestamp = _timeProvider.GetTimestamp();
                return null;
            }

            Volatile.Write(ref _current, null);
            IServerToClientMessage assembled = WorldTransfer.WithWorld(current.Carrier, current.BuildWorld());
            if (assembled is JoinAcceptedMessage joinAccepted && current.Terrain != null)
            {
                assembled = joinAccepted with { ReceivedTerrain = current.Terrain };
            }

            deliver(assembled);
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
    private readonly List<PersistedTerrainChunk> _terrain = [];
    private System.Security.Cryptography.IncrementalHash? _digest;
    private int _gridWidth;
    private int _gridDepth;
    private long _maxCellCount;
    private long _terrainBytes;

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
        LastProgressTimestamp = startedTimestamp;
        if (carrier is JoinAcceptedMessage { Terrain: TerrainTransferManifest manifest } joinAccepted)
        {
            ValidateManifest(manifest, joinAccepted.World, limits);
            Manifest = manifest;
            _gridWidth = GridCount(joinAccepted.World!.Width);
            _gridDepth = GridCount(joinAccepted.World.Depth);
            _maxCellCount = TerrainChunkFormat.ChunkSize * TerrainChunkFormat.ChunkSize * (long)joinAccepted.World.Height;
            _digest = TerrainTransferDigest.CreateIncremental();
        }
    }

    // The terrain the carrier announced, if any (protocol v8).
    public TerrainTransferManifest? Manifest { get; }

    public int TerrainFragmentsReceived { get; private set; }

    // True once the final terrain fragment arrived and its digest matched.
    public bool IsTerrainComplete { get; private set; }

    // The terrain chunks received, in wire order; null when the carrier announced none.
    public IReadOnlyList<PersistedTerrainChunk>? Terrain => Manifest == null ? null : _terrain;

    private static void ValidateManifest(TerrainTransferManifest manifest, WorldDescriptorWire? world, WorldTransferLimits limits)
    {
        if (world == null)
        {
            throw new InvalidDataException("A join-accepted carrier announces terrain without a world.");
        }

        if (world.MapSource != WorldMapSourceText.Stored)
        {
            throw new InvalidDataException($"A join-accepted carrier announces terrain for a '{world.MapSource}' world.");
        }

        long expectedChunks = (long)GridCount(world.Width)
            * GridCount(world.Depth);
        if (world.Width <= 0 || world.Depth <= 0 || world.Height <= 0 || manifest.Chunks != expectedChunks)
        {
            throw new InvalidDataException(
                $"The terrain manifest announces {manifest.Chunks} chunks for a {world.Width}x{world.Height}x{world.Depth} world ({expectedChunks} expected).");
        }

        if (manifest.Fragments < 1 || manifest.Fragments > manifest.Chunks)
        {
            throw new InvalidDataException(
                $"The terrain manifest announces {manifest.Fragments} fragments for {manifest.Chunks} chunks.");
        }

        if (manifest.Bytes <= 0)
        {
            throw new InvalidDataException($"The terrain manifest announces {manifest.Bytes} bytes.");
        }

        if (manifest.Bytes > limits.MaxTerrainBytes)
        {
            throw new WorldTransferLimitException(
                $"The terrain manifest announces {manifest.Bytes} bytes, over the {limits.MaxTerrainBytes} limit.");
        }

        if (manifest.Sha256 is not { Length: 64 } sha || !IsLowerHex(sha))
        {
            throw new InvalidDataException("The terrain manifest's sha256 is not 64 lowercase hex characters.");
        }
    }

    private static int GridCount(int cells) =>
        (int)(((long)cells + TerrainChunkFormat.ChunkSize - 1) / TerrainChunkFormat.ChunkSize);

    private static bool IsLowerHex(string text)
    {
        foreach (char c in text)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    // Appends a terrain fragment, validating it as the protocol demands (see the wave E plan). Any
    // failure is a protocol error that ends the session.
    public void AddTerrain(TerrainChunkMessage fragment)
    {
        try
        {
            AddTerrainCore(fragment);
        }
        catch
        {
            // The session ends; release the native hash now rather than at finalization.
            _digest?.Dispose();
            _digest = null;
            throw;
        }
    }

    private void AddTerrainCore(TerrainChunkMessage fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        if (Manifest == null)
        {
            throw new InvalidOperationException($"Received a terrain fragment for world transfer {TransferId}, whose carrier announced no terrain.");
        }

        if (fragment.TransferId != TransferId)
        {
            throw new InvalidOperationException(
                $"Received terrain fragment {fragment.Index} of transfer {fragment.TransferId} during world transfer {TransferId}.");
        }

        if (ChunksReceived > 0)
        {
            throw new InvalidOperationException($"World transfer {TransferId} received a terrain fragment after a world chunk.");
        }

        if (IsTerrainComplete || fragment.Index != TerrainFragmentsReceived)
        {
            throw new InvalidOperationException(
                $"Expected terrain fragment {TerrainFragmentsReceived} of world transfer {TransferId}, received fragment {fragment.Index}.");
        }

        IReadOnlyList<PersistedTerrainChunk> chunks = fragment.Chunks;
        if (chunks.Count == 0)
        {
            throw new InvalidDataException($"Terrain fragment {fragment.Index} of world transfer {TransferId} has no chunks.");
        }

        bool lastFragment = fragment.Index == Manifest.Fragments - 1;
        if (fragment.IsFinal != lastFragment)
        {
            throw new InvalidDataException(
                $"Terrain fragment {fragment.Index} of {Manifest.Fragments} is {(fragment.IsFinal ? "" : "not ")}marked final.");
        }

        for (int i = 0; i < chunks.Count; i++)
        {
            PersistedTerrainChunk chunk = chunks[i];
            if ((uint)chunk.ChunkX >= (uint)_gridWidth || (uint)chunk.ChunkZ >= (uint)_gridDepth)
            {
                throw new InvalidDataException(
                    $"Terrain chunk ({chunk.ChunkX}, {chunk.ChunkZ}) is outside the {_gridWidth}x{_gridDepth} chunk grid.");
            }

            if (_terrain.Count > 0)
            {
                PersistedTerrainChunk previous = _terrain[^1];
                if (chunk.ChunkZ < previous.ChunkZ || (chunk.ChunkZ == previous.ChunkZ && chunk.ChunkX <= previous.ChunkX))
                {
                    throw new InvalidDataException(
                        $"Terrain chunk ({chunk.ChunkX}, {chunk.ChunkZ}) is not after ({previous.ChunkX}, {previous.ChunkZ}) in (z, x) order.");
                }
            }

            if (chunk.FormatVersion != TerrainChunkFormat.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Terrain chunk ({chunk.ChunkX}, {chunk.ChunkZ}) has format version {chunk.FormatVersion}, expected {TerrainChunkFormat.CurrentVersion}.");
            }

            if (chunk.CellCount < 0 || chunk.CellCount > _maxCellCount)
            {
                throw new InvalidDataException(
                    $"Terrain chunk ({chunk.ChunkX}, {chunk.ChunkZ}) announces {chunk.CellCount} cells, over the {_maxCellCount} a chunk holds.");
            }

            if (chunk.Data == null || chunk.Data.Length == 0)
            {
                throw new InvalidDataException($"Terrain chunk ({chunk.ChunkX}, {chunk.ChunkZ}) has no data.");
            }

            if (chunk.Data.Length > _limits.MaxTerrainBlobBytes)
            {
                throw new WorldTransferLimitException(
                    $"Terrain chunk ({chunk.ChunkX}, {chunk.ChunkZ}) is {chunk.Data.Length} bytes, over the {_limits.MaxTerrainBlobBytes} limit.");
            }

            if (_terrain.Count >= Manifest.Chunks)
            {
                throw new InvalidDataException($"World transfer {TransferId} sent more than the {Manifest.Chunks} terrain chunks it announced.");
            }

            _terrainBytes += chunk.Data.Length;
            if (_terrainBytes > Manifest.Bytes)
            {
                throw new InvalidDataException($"World transfer {TransferId} sent more than the {Manifest.Bytes} terrain bytes it announced.");
            }

            TerrainTransferDigest.Append(_digest!, chunk);
            _terrain.Add(chunk);
        }

        TerrainFragmentsReceived++;
        if (!lastFragment)
        {
            return;
        }

        string digest = TerrainTransferDigest.Finish(_digest!);
        _digest!.Dispose();
        _digest = null;
        if (_terrain.Count != Manifest.Chunks || _terrainBytes != Manifest.Bytes)
        {
            throw new InvalidDataException(
                $"World transfer {TransferId} sent {_terrain.Count} terrain chunks and {_terrainBytes} bytes; it announced {Manifest.Chunks} and {Manifest.Bytes}.");
        }

        if (!string.Equals(digest, Manifest.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The terrain of world transfer {TransferId} does not match its announced sha256.");
        }

        IsTerrainComplete = true;
    }

    public IServerToClientMessage Carrier { get; }

    public int TransferId { get; }

    // When the carrier arrived, a timestamp of the reassembler's TimeProvider.
    public long StartedTimestamp { get; }

    // When the latest chunk (or, before the first, the carrier) arrived, on the same clock. Set by
    // the reassembler.
    public long LastProgressTimestamp { get; internal set; }

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

        if (Manifest != null && !IsTerrainComplete)
        {
            throw new InvalidOperationException(
                $"World transfer {TransferId} received world chunk {chunk.ChunkIndex} before its terrain was complete ({TerrainFragmentsReceived} of {Manifest.Fragments} fragments).");
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

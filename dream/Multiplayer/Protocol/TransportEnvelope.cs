namespace Dream.Multiplayer.Protocol;

public readonly record struct TransportEnvelope(
    int ProtocolVersion,
    string MessageKind,
    ulong Sequence)
{
    // Version 2 leaves null properties and derived properties out of payloads. Version 3 (SCALE-1)
    // encodes worlds compactly (WorldSnapshotJsonConverter) and sends join and resync worlds as
    // chunked transfers (WorldChunkMessage). Version 4 (SCALE-2) sends snapshot actors as
    // delta-coded frames ("actorFrame" in place of "actors") and moves each connection's accepted
    // command sequence into the envelope header (the Ack below); a v3 peer would find no "actors"
    // list and fail with a NullReferenceException, so the bump makes both sides refuse it with a
    // ProtocolVersionMismatchException instead. Version 5 (T1) adds the world clock anchor to the
    // join-accepted message (JoinAcceptedMessage.WorldClock); a v4 client would ignore it and show a
    // fixed time of day, so both sides refuse it instead. Version 6 (T2f) adds each player actor's
    // held light (ActorSnapshot.HeldLight, the Equipment group of an actor frame record, mask bit
    // 256); a v5 client would reject the mask, so both sides refuse it. Version 7 (persistence
    // wave B) adds the world descriptor to the join-accepted message (JoinAcceptedMessage.World): a
    // v6 client would size its map from the first snapshot and ignore the descriptor, so both sides
    // refuse it. Version 8 (persistence wave E) adds the stored-terrain manifest
    // (JoinAcceptedMessage.Terrain) and the server.terrain-chunk transfer of stored worlds; a v7 client could not
    // build a stored world, so both sides refuse it. Snapshot payloads are unchanged. Decode rejects any other version.
    public const int CurrentProtocolVersion = 8;

    // SCALE-2: the receiving connection's own last accepted command sequence, written in the
    // envelope header ("ack") ahead of a shared snapshot payload (ServerSnapshotMessage.SharedBody)
    // so the payload itself stays identical for every connection. Null on every other frame.
    public TransportAck? Ack { get; init; }
}

// One session's accepted command sequence. SessionId is null when the session has no accepted
// command yet; the header then carries an empty object, so the client still gets a (empty)
// sequence map.
public readonly record struct TransportAck(string? SessionId, ulong Sequence);

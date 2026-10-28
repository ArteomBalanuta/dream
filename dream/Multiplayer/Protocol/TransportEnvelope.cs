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
    // ProtocolVersionMismatchException instead. Decode rejects any other version.
    public const int CurrentProtocolVersion = 4;

    // SCALE-2: the receiving connection's own last accepted command sequence, written in the
    // envelope header ("ack") ahead of a shared snapshot payload (ServerSnapshotMessage.SharedBody)
    // so the payload itself stays identical for every connection. Null on every other frame.
    public TransportAck? Ack { get; init; }
}

// One session's accepted command sequence. SessionId is null when the session has no accepted
// command yet; the header then carries an empty object, so the client still gets a (empty)
// sequence map.
public readonly record struct TransportAck(string? SessionId, ulong Sequence);

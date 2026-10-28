namespace Dream.Multiplayer.Protocol;

public readonly record struct TransportEnvelope(
    int ProtocolVersion,
    string MessageKind,
    ulong Sequence)
{
    // Version 2 leaves null properties and derived properties out of payloads. Version 3 (SCALE-1)
    // encodes worlds compactly (WorldSnapshotJsonConverter) and sends join and resync worlds as
    // chunked transfers (WorldChunkMessage); SCALE-2 (still v3, which never shipped) adds
    // delta-coded actor frames and the per-connection Ack below. Decode rejects any other version.
    public const int CurrentProtocolVersion = 3;

    // SCALE-2: the receiving connection's own last accepted command sequence, written in the
    // envelope header ("ack") ahead of a shared snapshot payload (ServerSnapshotMessage.SharedBody)
    // so the payload itself stays identical for every connection. Null on every other frame.
    public TransportAck? Ack { get; init; }
}

// One session's accepted command sequence. SessionId is null when the session has no accepted
// command yet; the header then carries an empty object, so the client still gets a (empty)
// sequence map.
public readonly record struct TransportAck(string? SessionId, ulong Sequence);

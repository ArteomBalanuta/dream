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
    // build a stored world, so both sides refuse it. Version 9 (persistence wave G) adds the optional
    // accountToken to the join request (ClientJoinRequestMessage.AccountToken); a v8 client could not
    // identify its account, so both sides refuse it. Version 10 (persistence wave H) adds the
    // inventory unequip, move and destroy intents and the equip intent's target slot, and the stats,
    // light slots, rarity and equipment slots of an inventory snapshot's items (InventoryItemSnapshot,
    // PlayerInventorySnapshot.Equipment); a v9 client would show no gear and could not use the new
    // slots, so both sides refuse it. Actor snapshots are unchanged apart from this envelope's version
    // digit. Version 11 (persistence wave I) adds the server.inventory-push message (loot notices and
    // a fresh inventory snapshot) and the optional EnemyPresentation actor group (enemy level and
    // visual key); a v10 client would fail on the group mask, so both sides refuse it. A wolf-only
    // world's actor bytes are unchanged apart from the version digit. Version 12 (classic HUD) adds the
    // optional trailing "hit" field of a damage event (DamageEventDelta.HitKind, written only for a
    // critical or incoming hit); a v11 client would show every number as a regular hit, so both sides
    // refuse it. Snapshots without a critical hit are unchanged apart from the version digit. Version
    // 13 (classic HUD iteration 2) writes the inventory snapshot compactly
    // (PlayerInventorySnapshotJsonConverter: "bag" and "worn" entry arrays, item text rehydrated from
    // the shipped content catalog) for an 8x8 footprint bag and twelve worn slots, adds the optional
    // "targetCell" of the unequip intent, and widens the equip, move and destroy intents' slot indexes
    // (bag anchors 0..63, worn slots 100..111); a v12 client would misread the inventory, so both sides
    // refuse it. Actor and world snapshots are unchanged apart from the version digit. Version 15 (stat
    // points, M2-A) adds the "player.stats.spend" action (PlayerStatSpendIntent); 14 belongs to the
    // chat protocol (M1). Decode rejects
    // any other version.
    public const int CurrentProtocolVersion = 15;

    // SCALE-2: the receiving connection's own last accepted command sequence, written in the
    // envelope header ("ack") ahead of a shared snapshot payload (ServerSnapshotMessage.SharedBody)
    // so the payload itself stays identical for every connection. Null on every other frame.
    public TransportAck? Ack { get; init; }
}

// One session's accepted command sequence. SessionId is null when the session has no accepted
// command yet; the header then carries an empty object, so the client still gets a (empty)
// sequence map.
public readonly record struct TransportAck(string? SessionId, ulong Sequence);
